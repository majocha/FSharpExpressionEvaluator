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
open System.Threading.Tasks
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

/// Reads a loaded module's metadata in Concord's buffer, without a copy. The buffer can go away
/// when the module unloads or changes, so `read` must not keep the reader or values that refer to it.
let private readMetadata (m: DkmClrModuleInstance) (read: MetadataReader -> 'T) =
    let mutable size = 0u
    let ptr = m.GetMetaDataBytesPtr(&size)
    read (MetadataReader(NativePtr.ofNativeInt ptr, int size))

/// Reads a range of a module's image from debuggee memory. Reading the whole image at once fails
/// (unmapped gaps between sections), so only what is needed is read.
let private readImage (m: DkmClrModuleInstance) (offset: int) (size: int) =
    let buffer = Array.zeroCreate<byte> size
    let read = m.Process.ReadMemory(m.BaseAddress + uint64 offset, DkmReadMemoryFlags.None, buffer)

    if read <> size then
        failwith $"read {read} of {size} bytes at +0x{offset:X}"

    buffer

/// The simple name of a module's assembly, kept with the module instance.
[<AllowNullLiteral>]
type private AssemblyNameItem(name: string) =
    inherit DkmDataItem()
    member _.Name = name

/// The simple name of a loaded module's assembly.
let private assemblyName (m: DkmClrModuleInstance) =
    match m.GetDataItem<AssemblyNameItem>() with
    | null ->
        let name = readMetadata m (fun md -> md.GetString(md.GetAssemblyDefinition().Name))
        m.SetDataItem(DkmDataCreationDisposition.CreateAlways, AssemblyNameItem name)
        name
    | item -> item.Name

/// The simple name of an assembly and the assemblies it references.
let private assemblyAndReferences (md: MetadataReader) =
    md.GetString(md.GetAssemblyDefinition().Name),
    [ for h in md.AssemblyReferences -> (md.GetAssemblyReference h).GetAssemblyName() ]

/// A module as the compiler sees it: its metadata and its F# signature and optimization data
/// (manifest resources), read from the loaded image. Modules without F# data need no resources.
/// Also gives the assembly's simple name and its references, and whether the result can be kept
/// (not when the resources could not be read: the next session tries again).
let private readDebuggeeModule (m: DkmClrModuleInstance) (ptr: nativeint) (size: uint32) =
    let md = MetadataReader(NativePtr.ofNativeInt ptr, int size)

    if not md.IsAssembly then
        None, true
    else
        let fsharpResources =
            MetadataTypes.embeddedResources md
            |> List.filter (fun (name, _) -> name.StartsWith "FSharp")
            |> List.map snd

        let resources, complete =
            if fsharpResources.IsEmpty then
                [], true
            else
                try
                    let read = readImage m
                    let cli = PeImage.cliResources read (string m.MemoryLayout = "MemoryPE")
                    let resources =
                        fsharpResources |> List.map (fun offset -> offset, PeImage.readResource read cli offset)

                    Log.verbose "EE" (fun () -> $"F# resources of {m.Name} ({m.MemoryLayout}): {[ for offset, blob in resources -> (offset, blob.Length) ]}")
                    resources, true
                with e ->
                    // Without them the module still imports, as plain .NET metadata.
                    Log.exn "EE" $"F# resources of {m.Name}" e
                    [], false

        let fileName =
            if String.IsNullOrEmpty m.FullName then
                $"{m.Name}#{m.Mvid}"
            else
                m.FullName

        let name, references = assemblyAndReferences md

        Some(
            {
                FileName = fileName
                // The one copy of the metadata: the session keeps it and reads it later, on another
                // thread, when Concord's buffer can be gone.
                Image = DebuggeeImage.Metadata(copyBlob ptr size, resources)
                IsOptional = false
            },
            name,
            references
        ),
        complete

/// A module as `readDebuggeeModule` read it, kept with the module instance, so that a new session
/// (after another module loads) reads only the new modules. It is for the metadata buffer at
/// `Pointer` with `Size`: Edit and Continue (Hot Reload) changes the buffer, and the module is then
/// read again.
[<AllowNullLiteral>]
type private DebuggeeModuleItem(pointer: nativeint, size: uint32, value: (DebuggeeModule * string * AssemblyName list) option) =
    inherit DkmDataItem()
    member _.Pointer = pointer
    member _.Size = size
    member _.Value = value

let private debuggeeModule (m: DkmClrModuleInstance) =
    let mutable size = 0u
    let ptr = m.GetMetaDataBytesPtr(&size)

    match m.GetDataItem<DebuggeeModuleItem>() with
    | item when not (isNull item) && item.Pointer = ptr && item.Size = size -> item.Value
    | _ ->
        let value, complete = readDebuggeeModule m ptr size

        if complete then
            m.SetDataItem(DkmDataCreationDisposition.CreateAlways, DebuggeeModuleItem(ptr, size, value))

        value

/// An assembly referenced by the loaded ones but not loaded itself (e.g. the `netstandard` facade a
/// netstandard FSharp.Core references): its metadata from the debugger, which finds it as the
/// runtime would. The session can do without it: a blob that is empty or is not an assembly's
/// metadata gives no module.
let private referencedModule (appDomain: DkmClrAppDomain) (reference: AssemblyName) =
    try
        let mutable size = 0u
        let ptr = Phases.time "debugger" (fun () -> appDomain.GetMetaDataBytesPtr(reference.FullName, &size))

        if ptr = 0n || size = 0u then
            log $"no metadata for {reference.FullName}: empty blob"
            None
        else
            let blob = copyBlob ptr size

            match Evaluation.assemblyMetadataError blob with
            | Some error ->
                log $"no metadata for {reference.FullName}: {size} bytes that are not an assembly's metadata ({error})"
                None
            | None ->
                Some
                    {
                        // A path that names it but doesn't exist: nothing is read from disk.
                        FileName = Path.Combine(Path.GetTempPath(), "FSharpExpressionEvaluator", "not-loaded", reference.Name + ".dll")
                        Image = DebuggeeImage.Metadata(blob, [])
                        IsOptional = true
                    }
    with e ->
        log $"no metadata for {reference.FullName}: {e.GetType().Name}"
        None

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
                    for h in md.GetLocalScopes(MetadataTypes.methodHandle token) do
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
                            { Name = md.GetString v.Name; Slot = v.Index; Start = scope.StartOffset }
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

/// The IL of the frame's method when it is a state machine's `MoveNext`, read from the loaded image:
/// the stores to its fields give the scopes of the hoisted variables. Empty for other methods, for
/// a method changed by Edit and Continue (the image has its first version) and when it can't be read.
let private moveNextIL (address: DkmClrInstructionAddress) =
    let m = address.ModuleInstance

    let rva =
        readMetadata m (fun md ->
            let methodDef = md.GetMethodDefinition(MetadataTypes.methodHandle address.MethodId.Token)

            if md.GetString methodDef.Name <> "MoveNext" then 0 else methodDef.RelativeVirtualAddress)

    if rva = 0 || address.MethodId.Version > 1u then
        [||]
    else
        let read = readImage m

        let toImageOffset =
            if string m.MemoryLayout = "MemoryPE" then id else (PeImage.cliResources read false).ToImageOffset

        PeImage.readMethodIL read toImageOffset rva

let private aliasKind (kind: DkmClrAliasKind) =
    match kind with
    | DkmClrAliasKind.Exception -> Some AliasKind.Exception
    | DkmClrAliasKind.StowedException -> Some AliasKind.StowedException
    | DkmClrAliasKind.ReturnValue -> Some AliasKind.ReturnValue
    | DkmClrAliasKind.ObjectId -> Some AliasKind.ObjectId
    | DkmClrAliasKind.Variable -> Some AliasKind.Variable
    | _ -> None

/// The display name of the debugger's intrinsics assembly, kept with the runtime instance.
[<AllowNullLiteral>]
type private IntrinsicsAssemblyItem(name: string) =
    inherit DkmDataItem()
    member _.Name = name

/// The display name of the assembly with the debugger's intrinsic methods (aliases are read through it).
let private intrinsicsAssembly (runtime: DkmClrRuntimeInstance) =
    match runtime.GetDataItem<IntrinsicsAssemblyItem>() with
    | null ->
        let mutable size = 0u
        let ptr = runtime.GetIntrinsicAssemblyMetaDataBytesPtr(&size)
        let md = MetadataReader(NativePtr.ofNativeInt ptr, int size)
        let name = md.GetAssemblyDefinition().GetAssemblyName().FullName
        runtime.SetDataItem(DkmDataCreationDisposition.CreateAlways, IntrinsicsAssemblyItem name)
        name
    | item -> item.Name

/// The debugger's aliases in an inspection context ($exception, $ReturnValue, object IDs, variables
/// of the Immediate window); null when there are none.
let aliases (address: DkmClrInstructionAddress) (inspectionContext: DkmInspectionContext) =
    address.RuntimeInstance.GetAliases inspectionContext

/// The context of the stopped frame: its method, local signature, locals, opens and
/// `debuggerAliases` (as `aliases` gives them).
let frameWith (address: DkmClrInstructionAddress) (debuggerAliases: Collections.ObjectModel.ReadOnlyCollection<DkmClrAlias>) =
    let m = address.ModuleInstance
    let runtime = address.RuntimeInstance

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

    let methodIL =
        try
            moveNextIL address
        with e ->
            Log.exn "EE" "Method IL" e
            [||]

    let aliases =
        match debuggerAliases with
        | null -> []
        | aliases ->
            [
                for a in aliases do
                    match aliasKind a.Kind with
                    // An expression can't name an alias without a full name.
                    | Some _ when String.IsNullOrEmpty a.FullName -> ()
                    | Some kind ->
                        // A variable declared `let mutable` in the Immediate window says so in the
                        // custom type info it was created with.
                        let isMutable =
                            kind = AliasKind.Variable
                            && not (isNull a.CustomTypeInfoPayload)
                            && match FSharpTypes.Variable.tryDecode a.CustomTypeInfoPayloadTypeId (Seq.toArray a.CustomTypeInfoPayload) with
                               | Some(isMutable, _) -> isMutable
                               | None -> false

                        {
                            Name = a.FullName
                            Kind = kind
                            TypeName = a.Type
                            IsMutable = isMutable
                        }
                    | None -> ()
            ]

    {
        AssemblyName = assemblyName m
        MethodToken = address.MethodId.Token
        LocalSignatureToken = localSignatureToken
        Opens = opens
        Locals = locals
        ILOffset = int address.ILOffset
        MethodIL = methodIL
        Aliases = aliases
        IntrinsicsAssembly = intrinsicsAssembly runtime
    }

/// The context of the stopped frame: its method, local signature, locals, opens and the aliases.
let frame (address: DkmClrInstructionAddress) (inspectionContext: DkmInspectionContext) =
    frameWith address (aliases address inspectionContext)

/// When a runtime instance loaded its first module and FSharp.Core (Stopwatch timestamps; 0 when
/// FSharp.Core is not loaded), for the log of the session build (docs/DESIGN.md §6.9).
[<AllowNullLiteral>]
type private LoadTimesItem(firstModule: int64) =
    inherit DkmDataItem()
    member _.FirstModule = firstModule
    member val FSharpCore = 0L with get, set

/// The milliseconds since a Stopwatch timestamp.
let private msSince (timestamp: int64) =
    (Stopwatch.GetTimestamp() - timestamp) * 1000L / Stopwatch.Frequency

/// Notes the load of a CLR module, for the log of the session build. Concord calls it on its
/// event thread, one module at a time.
let moduleLoaded (moduleInstance: DkmModuleInstance) (isFSharpCore: bool) =
    let runtime = moduleInstance.RuntimeInstance

    let item =
        match runtime.GetDataItem<LoadTimesItem>() with
        | null ->
            let item = LoadTimesItem(Stopwatch.GetTimestamp())
            runtime.SetDataItem(DkmDataCreationDisposition.CreateAlways, item)
            log "the runtime loaded its first module"
            item
        | item -> item

    if isFSharpCore && item.FSharpCore = 0L then
        item.FSharpCore <- Stopwatch.GetTimestamp()
        log $"FSharp.Core loaded, {msSince item.FirstModule} ms after the runtime's first module"

/// When the session build starts, against the module loads, for the log.
let private buildStart (runtime: DkmRuntimeInstance) =
    match runtime.GetDataItem<LoadTimesItem>() with
    | null -> "no module load seen"
    | item when item.FSharpCore = 0L -> $"{msSince item.FirstModule} ms after the runtime's first module"
    | item -> $"{msSince item.FirstModule} ms after the runtime's first module, {msSince item.FSharpCore} ms after FSharp.Core"

/// The compiler's warm-up over devenv's own assemblies (`EvaluationSession.WarmUpCompiler`), run
/// once per process on a thread-pool thread, from when a debuggee loads FSharp.Core
/// (docs/DESIGN.md §6.9). The first break then finds most of the compiler compiled by the JIT.
let private compilerWarmUp =
    lazy
        (Task.Run(fun () ->
            let sw = Stopwatch.StartNew()

            try
                let modules, compiled = EvaluationSession.WarmUpCompiler()
                log $"compiler warmed up over {modules} modules in {sw.ElapsedMilliseconds} ms (compiled: {compiled})"
            with e ->
                Log.exn "EE" "compiler warm-up" e))

/// Starts the compiler's warm-up, unless it has started already.
let warmUpCompiler () = compilerWarmUp.Force() |> ignore

/// The evaluation session of an AppDomain, kept with it while its set of modules stays the same,
/// built on a thread-pool thread (docs/DESIGN.md §6.9). Replacing it, or the AppDomain going away,
/// releases the session, once built. A build that failed stays, so that the same modules are not
/// built again.
[<AllowNullLiteral>]
type private SessionItem(modules: Guid[], session: Task<EvaluationSession>) =
    inherit DkmDataItem()
    member _.Modules = modules
    member _.Session = session

    override _.OnClose() =
        session.ContinueWith(fun (t: Task<EvaluationSession>) ->
            if t.Status = TaskStatus.RanToCompletion then
                (t.Result :> IDisposable).Dispose())
        |> ignore

/// What a session item is built for: the modules loaded in the AppDomain, by MVID in order (compared
/// as arrays, element by element).
let private modulesKey (modules: DkmClrModuleInstance[]) =
    let mvids = modules |> Array.map _.Mvid
    Array.sortInPlace mvids
    mvids

/// The session item of the frame's AppDomain for the modules loaded in it now. A new one copies the
/// modules and the frame's context out of the debuggee here, while it is stopped (the debugger's
/// buffers of referenced assemblies go away when they load). Then it builds the session and warms
/// it up in the frame on a thread-pool thread.
let private sessionItem (address: DkmClrInstructionAddress) =
    let appDomain = address.ModuleInstance.AppDomain
    let modules = modulesInAppDomain address
    let key = modulesKey modules

    match appDomain.GetDataItem<SessionItem>() with
    | item when not (isNull item) && item.Modules = key -> item
    | _ ->
        log $"session for AppDomain {appDomain.Id}: build starts, {buildStart address.RuntimeInstance}"
        let sw = Stopwatch.StartNew()
        let loaded = modules |> Array.choose debuggeeModule |> List.ofArray
        let readLoaded = sw.ElapsedMilliseconds
        let loadedNames = loaded |> List.map (fun (_, name, _) -> name) |> set

        let referenced, referencedTimes =
            Phases.record (fun () ->
                loaded
                |> List.collect (fun (_, _, references) -> references)
                |> List.filter (fun r -> not (loadedNames.Contains r.Name))
                |> List.distinctBy _.Name
                |> List.choose (referencedModule appDomain))

        let debuggeeModules = (loaded |> List.map (fun (m, _, _) -> m)) @ referenced

        // The frame as the first Locals query sees it, without the aliases: the warm-up then loads
        // the imports of its variables' types.
        let frame =
            try
                frameWith address null
            with e ->
                Log.exn "EE" "warm-up frame" e
                EvaluationSession.MethodFrame(assemblyName address.ModuleInstance, address.MethodId.Token)

        let build () =
            let sw = Stopwatch.StartNew()

            let session, createTimes =
                try
                    Phases.record (fun () -> EvaluationSession.Create debuggeeModules)
                with e ->
                    // Logged once: the item stays for these modules, and evaluations fall back to C#
                    // until they change.
                    Log.exn
                        "EE"
                        $"session for AppDomain {appDomain.Id} ({loaded.Length} loaded and {referenced.Length} referenced modules; C# until they change)"
                        e

                    reraise ()

            let created = sw.ElapsedMilliseconds
            // Also beside the compiler's warm-up, if it still runs: the sessions are separate, and the
            // JIT compiles each method once, for both threads. The frame's own work (its variables'
            // types) then runs while the compiler warms up.
            let compiled = session.WarmUp frame

            log
                $"session for AppDomain {appDomain.Id}: {loaded.Length} loaded and {referenced.Length} referenced modules, read in {readLoaded} + {referencedTimes}, built in {createTimes}, warmed up in {sw.ElapsedMilliseconds - created} ms (compiled: {compiled})"

            session

        let item = SessionItem(key, Task.Run build)
        appDomain.SetDataItem(DkmDataCreationDisposition.CreateAlways, item)
        item

/// Starts building the session of the frame's AppDomain, unless it is built or being built for the
/// modules loaded now. The frame decoder calls it at every F# frame; only the first one of a break
/// after modules changed does more than look.
let warmUp (address: DkmClrInstructionAddress) = sessionItem address |> ignore

/// The evaluation session for the frame's AppDomain: imports built from every module loaded in it,
/// rebuilt when modules are loaded or unloaded. Building if it is still being built after
/// `timeout`; Failed, at once, while the modules are those a build failed for.
let session (timeout: TimeSpan) (address: DkmClrInstructionAddress) =
    let item = sessionItem address
    let running = not item.Session.IsCompleted
    let sw = Stopwatch.StartNew()

    match SessionBuild.state item.Session timeout with
    | SessionBuild.Building ->
        log $"session for AppDomain {address.ModuleInstance.AppDomain.Id}: still building after {timeout.TotalSeconds} s"
        SessionBuild.Building
    | state ->
        if running then
            log $"session for AppDomain {address.ModuleInstance.AppDomain.Id}: a query waited {sw.ElapsedMilliseconds} ms for it"

        state

/// The F# names of the types of the frame method's parameters and return type, where the F#
/// signature says more than IL (`EvaluationSession.SignatureTypeNames`). Only from a session that is
/// built for the modules loaded now: the decoders neither wait for one nor start one (a breakpoint's
/// name can be asked for while the debuggee runs), and name the types from IL until then.
let signatureTypeNames (address: DkmClrInstructionAddress) =
    match address.ModuleInstance.AppDomain.GetDataItem<SessionItem>() with
    | item when
        not (isNull item)
        && item.Session.Status = TaskStatus.RanToCompletion
        && item.Modules = modulesKey (modulesInAppDomain address)
        ->
        item.Session.Result.SignatureTypeNames(assemblyName address.ModuleInstance, address.MethodId.Token)
    | _ -> [], None
