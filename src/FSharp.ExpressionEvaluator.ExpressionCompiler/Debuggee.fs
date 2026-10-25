/// What the F# compiler needs from Concord (docs/DESIGN.md §6.2, §6.3): the AppDomain's modules
/// read from debuggee memory, an evaluation session per AppDomain, and the frame's context.
module internal FSharp.ExpressionEvaluator.Debuggee

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Runtime.InteropServices
open System.Text
open Microsoft.DiaSymReader
open Microsoft.FSharp.NativeInterop
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.Evaluation
open FSharp.ExpressionEvaluator.Evaluation

let private log = Log.write "EE"

/// The CLR modules loaded in the frame's AppDomain.
let private modulesInAppDomain (address: DkmClrInstructionAddress) =
    let appDomainId = address.ModuleInstance.AppDomain.Id

    address.RuntimeInstance.GetModuleInstances()
    |> Seq.choose (function
        | :? DkmClrModuleInstance as m when not m.AppDomain.IsUnloaded && m.AppDomain.Id = appDomainId -> Some m
        | _ -> None)
    |> Seq.toArray

/// A copy of a metadata blob: Concord's pointer is only valid while the process is stopped.
let private copyBlob (ptr: nativeint) (size: uint32) =
    let bytes = Array.zeroCreate<byte> (int size)
    Marshal.Copy(ptr, bytes, 0, int size)
    bytes

let private metadataReader (metadata: byte[]) =
    MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create<byte>(metadata))

/// Reads a range of a module's image from debuggee memory. Reading the whole image at once fails
/// (unmapped gaps between sections), so only what is needed is read.
let private readImage (m: DkmClrModuleInstance) (offset: int) (size: int) =
    let buffer = Array.zeroCreate<byte> size
    let read = m.Process.ReadMemory(m.BaseAddress + uint64 offset, DkmReadMemoryFlags.None, buffer)

    if read <> size then
        failwith $"read {read} of {size} bytes at +0x{offset:X}"

    buffer

/// The simple name of an assembly and the assemblies it references.
let private assemblyAndReferences (md: MetadataReader) =
    md.GetString(md.GetAssemblyDefinition().Name),
    [ for h in md.AssemblyReferences -> (md.GetAssemblyReference h).GetAssemblyName() ]

/// A module as the compiler sees it: its metadata and its F# signature and optimization data
/// (manifest resources), read from the loaded image. Modules without F# data need no resources.
/// Also gives the assembly's simple name and its references.
let private debuggeeModule (m: DkmClrModuleInstance) =
    let mutable size = 0u
    let metadata = copyBlob (m.GetMetaDataBytesPtr(&size)) size
    use provider = metadataReader metadata
    let md = provider.GetMetadataReader()

    if not md.IsAssembly then
        None
    else
        let fsharpResources =
            [
                for h in md.ManifestResources do
                    let r = md.GetManifestResource h

                    if r.Implementation.IsNil && (md.GetString r.Name).StartsWith "FSharp" then
                        int r.Offset
            ]

        let resources =
            if fsharpResources.IsEmpty then
                []
            else
                try
                    let read = readImage m
                    let cli = PeImage.cliResources read (string m.MemoryLayout = "MemoryPE")
                    let resources =
                        fsharpResources |> List.map (fun offset -> offset, PeImage.readResource read cli offset)

                    log $"F# resources of {m.Name} ({m.MemoryLayout}): {[ for offset, blob in resources -> (offset, blob.Length) ]}"
                    resources
                with e ->
                    // Without them the module still imports, as plain .NET metadata.
                    Log.exn "EE" $"F# resources of {m.Name}" e
                    []

        let fileName =
            if String.IsNullOrEmpty m.FullName then
                $"{m.Name}#{m.Mvid}"
            else
                m.FullName

        let name, references = assemblyAndReferences md

        Some(
            {
                FileName = fileName
                Image = DebuggeeImage.Metadata(metadata, resources)
            },
            name,
            references
        )

/// An assembly referenced by the loaded ones but not loaded itself (e.g. the `netstandard` facade a
/// netstandard FSharp.Core references): its metadata from the debugger, which finds it as the
/// runtime would.
let private referencedModule (appDomain: DkmClrAppDomain) (reference: AssemblyName) =
    try
        let mutable size = 0u
        let ptr = appDomain.GetMetaDataBytesPtr(reference.FullName, &size)

        Some
            {
                // A path that names it but doesn't exist: nothing is read from disk.
                FileName = Path.Combine(Path.GetTempPath(), "FSharpExpressionEvaluator", "not-loaded", reference.Name + ".dll")
                Image = DebuggeeImage.Metadata(copyBlob ptr size, [])
            }
    with e ->
        log $"no metadata for {reference.FullName}: {e.GetType().Name}"
        None

/// The evaluation session of an AppDomain, kept with it while its set of modules stays the same.
/// Replacing it, or the AppDomain going away, releases the session.
[<AllowNullLiteral>]
type private SessionItem(modules: string, session: EvaluationSession) =
    inherit DkmDataItem()
    member _.Modules = modules
    member _.Session = session
    override _.OnClose() = (session :> IDisposable).Dispose()

/// The evaluation session for the frame's AppDomain: imports built from every module loaded in it,
/// rebuilt when modules are loaded or unloaded.
let session (address: DkmClrInstructionAddress) =
    let appDomain = address.ModuleInstance.AppDomain
    let modules = modulesInAppDomain address

    let key =
        modules |> Array.map (fun m -> string m.Mvid) |> Array.sort |> String.concat ","

    match appDomain.GetDataItem<SessionItem>() with
    | item when not (isNull item) && item.Modules = key -> item.Session
    | _ ->
        let sw = Stopwatch.StartNew()
        let loaded = modules |> Array.choose debuggeeModule |> List.ofArray
        let loadedNames = loaded |> List.map (fun (_, name, _) -> name) |> set

        let referenced =
            loaded
            |> List.collect (fun (_, _, references) -> references)
            |> List.filter (fun r -> not (loadedNames.Contains r.Name))
            |> List.distinctBy _.Name
            |> List.choose (referencedModule appDomain)

        let debuggeeModules = (loaded |> List.map (fun (m, _, _) -> m)) @ referenced
        let session = EvaluationSession.Create debuggeeModules
        appDomain.SetDataItem(DkmDataCreationDisposition.CreateAlways, SessionItem(key, session))
        log $"session for AppDomain {appDomain.Id}: {loaded.Length} loaded and {referenced.Length} referenced modules, {sw.ElapsedMilliseconds} ms"
        session

/// The frame's locals in scope at its IL offset and the namespaces open there, from the portable PDB.
let private pdbScope (address: DkmClrInstructionAddress) =
    let token = address.MethodId.Token
    let il = int address.ILOffset

    match address.ModuleInstance.GetSymUnmanagedReader() with
    | :? ISymUnmanagedReader5 as reader ->
        let mutable metadata = NativePtr.nullPtr<byte>
        let mutable size = 0

        if reader.GetPortableDebugMetadataByVersion(int address.MethodId.Version, &metadata, &size) <> 0 then
            [], []
        else
            let md = MetadataReader(metadata, size)

            let scopes =
                [
                    for h in md.GetLocalScopes(MetadataTokens.MethodDefinitionHandle(token &&& 0xFFFFFF)) do
                        let scope = md.GetLocalScope h

                        if scope.StartOffset <= il && il < scope.EndOffset then
                            scope
                ]

            // Scopes come outermost first: inner names shadow outer ones.
            let locals =
                [
                    for scope in scopes do
                        for h in scope.GetLocalVariables() do
                            let v = md.GetLocalVariable h
                            { Name = md.GetString v.Name; Slot = v.Index }
                ]

            let opens =
                match List.tryLast scopes with
                | None -> []
                | Some scope ->
                    let rec namespaces (h: ImportScopeHandle) =
                        if h.IsNil then
                            []
                        else
                            let importScope = md.GetImportScope h

                            namespaces importScope.Parent
                            @ [
                                for import in importScope.GetImports() do
                                    if import.Kind = ImportDefinitionKind.ImportNamespace then
                                        Encoding.UTF8.GetString(md.GetBlobBytes import.TargetNamespace)
                            ]

                    namespaces scope.ImportScope |> List.distinct

            locals, opens
    | _ -> [], []

let private aliasKind (kind: DkmClrAliasKind) =
    match kind with
    | DkmClrAliasKind.Exception -> Some AliasKind.Exception
    | DkmClrAliasKind.StowedException -> Some AliasKind.StowedException
    | DkmClrAliasKind.ReturnValue -> Some AliasKind.ReturnValue
    | DkmClrAliasKind.ObjectId -> Some AliasKind.ObjectId
    | DkmClrAliasKind.Variable -> Some AliasKind.Variable
    | _ -> None

/// The display name of the assembly with the debugger's intrinsic methods (aliases are read through it).
let private intrinsicsAssembly (runtime: DkmClrRuntimeInstance) =
    let mutable size = 0u
    let metadata = copyBlob (runtime.GetIntrinsicAssemblyMetaDataBytesPtr(&size)) size
    use provider = metadataReader metadata
    (provider.GetMetadataReader().GetAssemblyDefinition().GetAssemblyName()).FullName

/// The context of the stopped frame: its method, local signature, locals, opens and the aliases.
let frame (address: DkmClrInstructionAddress) (inspectionContext: DkmInspectionContext) =
    let m = address.ModuleInstance
    let runtime = address.RuntimeInstance
    let mutable size = 0u
    use provider = metadataReader (copyBlob (m.GetMetaDataBytesPtr(&size)) size)
    let md = provider.GetMetadataReader()
    let assemblyName = md.GetString(md.GetAssemblyDefinition().Name)

    let localSignatureToken =
        try
            m.GetLocalSignatureToken address.MethodId.Token
        with _ ->
            0

    let locals, opens =
        try
            pdbScope address
        with e ->
            Log.exn "EE" "PDB scope" e
            [], []

    let aliases =
        match runtime.GetAliases inspectionContext with
        | null -> []
        | aliases ->
            [
                for a in aliases do
                    match aliasKind a.Kind with
                    | Some kind ->
                        {
                            Name = a.FullName
                            Kind = kind
                            TypeName = a.Type
                        }
                    | None -> ()
            ]

    {
        AssemblyName = assemblyName
        MethodToken = address.MethodId.Token
        LocalSignatureToken = localSignatureToken
        Opens = opens
        Locals = locals
        Aliases = aliases
        IntrinsicsAssembly = intrinsicsAssembly runtime
    }
