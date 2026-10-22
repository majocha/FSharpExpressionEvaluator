/// M0 spike probes (docs/DESIGN.md §6.2, §6.3): log what Concord gives us for an F# frame.
module internal FSharp.ExpressionEvaluator.Probes

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Text
open Microsoft.DiaSymReader
open Microsoft.FSharp.NativeInterop
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr

let private log = Log.write "Probe"

let private seen = HashSet<string>()

/// Runs a probe once per key, logging instead of throwing.
let once (key: string) (probe: unit -> unit) =
    if lock seen (fun () -> seen.Add key) then
        try
            probe ()
        with e ->
            Log.exn "Probe" key e

let private describeImports (md: MetadataReader) (handle: ImportScopeHandle) =
    if handle.IsNil then
        "<none>"
    else
        [ for import in (md.GetImportScope handle).GetImports() ->
              match import.Kind with
              | ImportDefinitionKind.ImportNamespace -> "ns " + Encoding.UTF8.GetString(md.GetBlobBytes import.TargetNamespace)
              | ImportDefinitionKind.ImportType -> $"type 0x{MetadataTokens.GetToken(import.TargetType):X8}"
              | kind -> string kind ]
        |> String.concat "; "

/// Portable PDB through GetSymUnmanagedReader: scopes, locals, imports and CDIs at the current IL offset.
let pdb (address: DkmClrInstructionAddress) =
    let m = address.ModuleInstance
    let token = address.MethodId.Token
    let il = int address.ILOffset

    match m.GetSymUnmanagedReader() with
    | null -> log $"PDB {m.Name}: no symbol reader"
    | reader ->
        log $"PDB {m.Name} method 0x{token:X8} IL 0x{il:X}: reader {reader.GetType().FullName}, ISymUnmanagedReader5 = {reader :? ISymUnmanagedReader5}"

        match reader with
        | :? ISymUnmanagedReader5 as reader5 ->
            let mutable metadata = NativePtr.nullPtr<byte>
            let mutable size = 0
            let hr = reader5.GetPortableDebugMetadataByVersion(int address.MethodId.Version, &metadata, &size)
            log $"  GetPortableDebugMetadataByVersion({address.MethodId.Version}) hr=0x{hr:X8} size={size}"

            if hr = 0 then
                let md = MetadataReader(metadata, size)
                let methodHandle = MetadataTokens.MethodDefinitionHandle(token &&& 0xFFFFFF)
                let debugInfo = md.GetMethodDebugInformation methodHandle

                if not debugInfo.Document.IsNil then
                    let document = md.GetDocument debugInfo.Document
                    log $"  document {md.GetString document.Name} language {md.GetGuid document.Language}"

                for scopeHandle in md.GetLocalScopes methodHandle do
                    let scope = md.GetLocalScope scopeHandle
                    let marker = if scope.StartOffset <= il && il < scope.EndOffset then "*" else " "

                    let locals =
                        [ for h in scope.GetLocalVariables() ->
                              let v = md.GetLocalVariable h
                              let hidden = if v.Attributes = LocalVariableAttributes.DebuggerHidden then "(hidden)" else ""
                              $"{md.GetString v.Name}#{v.Index}{hidden}" ]
                        |> String.concat ", "

                    let constants =
                        [ for h in scope.GetLocalConstants() -> md.GetString (md.GetLocalConstant h).Name ]
                        |> String.concat ", "

                    let imports = describeImports md scope.ImportScope

                    log $"  {marker}scope [0x{scope.StartOffset:X},0x{scope.EndOffset:X}) locals: {locals} consts: {constants} imports: {imports}"

                for h in md.GetCustomDebugInformation methodHandle do
                    let cdi = md.GetCustomDebugInformation h
                    log $"  method CDI {md.GetGuid cdi.Kind} ({(md.GetBlobBytes cdi.Value).Length} bytes)"

                for h in md.GetCustomDebugInformation EntityHandle.ModuleDefinition do
                    let cdi = md.GetCustomDebugInformation h
                    log $"  module CDI {md.GetGuid cdi.Kind} ({(md.GetBlobBytes cdi.Value).Length} bytes)"
        | _ -> ()

    try
        log $"  local signature token 0x{m.GetLocalSignatureToken token:X8}"
    with e ->
        log $"  GetLocalSignatureToken threw {e.GetType().Name}"

let clrModulesInAppDomain (address: DkmClrInstructionAddress) =
    let appDomainId = address.ModuleInstance.AppDomain.Id

    address.RuntimeInstance.GetModuleInstances()
    |> Seq.choose (function
        | :? DkmClrModuleInstance as m when not m.AppDomain.IsUnloaded && m.AppDomain.Id = appDomainId -> Some m
        | _ -> None)
    |> Seq.toArray

/// In-memory metadata pointers for every module in the AppDomain (Roslyn's MetadataBlock source).
let metadataBlocks (address: DkmClrInstructionAddress) =
    let sw = Stopwatch.StartNew()
    let modules = clrModulesInAppDomain address
    let mutable total = 0L
    let mutable failures = 0

    for m in modules do
        try
            let mutable size = 0u
            let ptr = m.GetMetaDataBytesPtr(&size)
            total <- total + int64 size

            if m.UniqueId = address.ModuleInstance.UniqueId then
                let md = MetadataReader(NativePtr.ofNativeInt ptr, int size)
                let resources =
                    [ for h in md.ManifestResources -> md.GetString (md.GetManifestResource h).Name ]
                    |> String.concat ", "

                let mvid = md.GetGuid(md.GetModuleDefinition().Mvid)
                log $"Metadata of {m.Name}: {size} bytes, raw metadata readable, MVID {mvid}, manifest resource rows: {resources}"
        with e ->
            failures <- failures + 1
            log $"Metadata of {m.Name} failed: {e.GetType().Name}: {e.Message}"

    let mutable intrinsicsSize = 0u
    address.RuntimeInstance.GetIntrinsicAssemblyMetaDataBytesPtr(&intrinsicsSize) |> ignore

    log
        $"Metadata: {modules.Length} modules in AppDomain {address.ModuleInstance.AppDomain.Id}, {total / 1024L} KB, {failures} failures, intrinsics {intrinsicsSize} bytes, {sw.ElapsedMilliseconds} ms"

let private isFSharpResource (name: string) =
    name.StartsWith "FSharpSignature" || name.StartsWith "FSharpOptimization"

/// Reads a range of the module image from debuggee memory. Reading the whole image at once
/// fails (unmapped gaps between sections), so callers read only what they need.
let private readImage (m: DkmClrModuleInstance) (offset: int) (size: int) =
    let buffer = Array.zeroCreate<byte> size
    let read = m.Process.ReadMemory(m.BaseAddress + uint64 offset, DkmReadMemoryFlags.None, buffer)
    if read <> size then failwith $"read {read} of {size} bytes at +0x{offset:X}"
    buffer

/// F# signature/optimization resources, read from the loaded image in debuggee memory:
/// PE headers from the image, resource offsets from the in-memory metadata, then the blobs.
let fsharpResources (m: DkmClrModuleInstance) =
    let sw = Stopwatch.StartNew()
    let layout = string m.MemoryLayout
    log $"Resources of {m.Name}: base 0x{m.BaseAddress:X} size {m.Size} layout {layout} flags {m.Flags} file exists {File.Exists m.FullName}"

    // MemoryPE is the loader's mapped layout (RVA = offset).
    let read = readImage m
    let cli = PeImage.cliResources read (layout = "MemoryPE")
    let mutable metadataSize = 0u
    let metadataPtr = m.GetMetaDataBytesPtr(&metadataSize)
    let md = MetadataReader(NativePtr.ofNativeInt metadataPtr, int metadataSize)

    // Cross-check against the file on disk, read with the same parser in file layout.
    let fromFile =
        if File.Exists m.FullName then
            let bytes = File.ReadAllBytes m.FullName
            let readFile offset count = Array.sub bytes offset count
            Some(readFile, PeImage.cliResources readFile false)
        else
            None

    let resources =
        [ for h in md.ManifestResources do
              let resource = md.GetManifestResource h
              let name = md.GetString resource.Name

              if resource.Implementation.IsNil && isFSharpResource name then
                  let blob = PeImage.readResource read cli (int resource.Offset)

                  let check =
                      match fromFile with
                      | Some(readFile, fileCli) ->
                          if PeImage.readResource readFile fileCli (int resource.Offset) = blob then "= file" else "DIFFERS from file"
                      | None -> "no file"

                  $"{name} ({blob.Length} bytes, {check})" ]

    let names = String.concat ", " resources
    log $"  {resources.Length} F# resources read from debuggee memory in {sw.ElapsedMilliseconds} ms: {names}"

let run (address: DkmClrInstructionAddress) =
    let m = address.ModuleInstance
    once $"pdb:{m.Mvid}:{address.MethodId.Token}:{address.ILOffset}" (fun () -> pdb address)
    once $"metadata:{m.AppDomain.Id}" (fun () -> metadataBlocks address)
    once $"resources:{m.Mvid}" (fun () -> fsharpResources m)

    for fsharpCore in clrModulesInAppDomain address |> Array.filter (fun x -> x.Name.Equals("FSharp.Core.dll", StringComparison.OrdinalIgnoreCase)) do
        once $"resources:{fsharpCore.Mvid}" (fun () -> fsharpResources fsharpCore)
