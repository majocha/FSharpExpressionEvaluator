namespace FSharp.ExpressionEvaluator

open System
open System.Collections.ObjectModel
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation
open FSharp.ExpressionEvaluator.Evaluation

/// Registered for F# frames (docs/DESIGN.md §4, §6). Expressions, assignments and the Locals query
/// are compiled by the F# compiler. Anything the F# compiler fails on internally falls back to the
/// next implementer, Roslyn's C# compiler (§6.10). With the F# EE switched off (Settings.fs), C#
/// compiles everything, as if this component were not installed.
type ExpressionCompiler() =

    static let log = Log.write "EC"
    static do log "ExpressionCompiler loaded"

    static let compilerId =
        DkmCompilerId(DkmVendorId.Microsoft, Guid "AB4F38C9-B6E6-43BA-BE3B-58080B2CCCE3")

    /// The result's F# type for the result provider's Type column (docs/DESIGN.md §6.8), when it
    /// says more than the CLR type.
    static let customTypeInfo (fsharpType: string option) =
        match fsharpType with
        | Some name -> DkmClrCustomTypeInfo.Create(FSharpTypes.TypeInfo.payloadId, ReadOnlyCollection(FSharpTypes.TypeInfo.encode name))
        | None -> null

    /// A Locals row's custom type info: its F# type, or the temporaries under the
    /// `[compiler generated]` row.
    static let localTypeInfo (local: CompiledLocal) =
        match local.CompilerGenerated with
        | [] -> customTypeInfo local.FSharpType
        | names ->
            DkmClrCustomTypeInfo.Create(
                FSharpTypes.CompilerGenerated.payloadId,
                ReadOnlyCollection(FSharpTypes.CompilerGenerated.encode names)
            )

    static let query (runtime: DkmClrRuntimeInstance) (compiled: CompiledExpression) =
        let flag condition value =
            if condition then value else DkmClrCompilationResultFlags.None

        let flags =
            flag compiled.PotentialSideEffect DkmClrCompilationResultFlags.PotentialSideEffect
            ||| flag compiled.ReadOnlyResult DkmClrCompilationResultFlags.ReadOnlyResult
            ||| flag compiled.BoolResult DkmClrCompilationResultFlags.BoolResult

        DkmCompiledClrInspectionQuery.Create(
            runtime,
            DataContainer = null,
            LanguageId = compilerId,
            Binary = ReadOnlyCollection compiled.Assembly,
            TypeName = compiled.TypeName,
            MethodName = compiled.MethodName,
            FormatSpecifiers = ReadOnlyCollection(ResizeArray compiled.FormatSpecifiers),
            CompilationFlags = flags,
            ResultCategory = DkmEvaluationResultCategory.Data,
            Access = DkmEvaluationResultAccessType.None,
            StorageType = DkmEvaluationResultStorageType.None,
            TypeModifierFlags = DkmEvaluationResultTypeModifierFlags.None,
            CustomTypeInfo = customTypeInfo compiled.FSharpType
        )

    /// The error while the session is still being built (docs/DESIGN.md §6.9): no C# instead, as it
    /// would read F# names differently.
    static let stillLoading = "F# EE is still loading; try again"

    /// How long an expression waits for a session still being built: a DataTip blocks the editor.
    static let expressionTimeout = TimeSpan.FromSeconds 3.0

    /// How long the Locals query waits: it comes right after the first break, and its C# fallback
    /// would show the frame as C# sees it until the next one.
    static let localsTimeout = TimeSpan.FromSeconds 10.0

    static let errorText (diagnostics: EvaluationDiagnostic list) =
        match diagnostics with
        | d :: _ -> $"error FS{d.Number:D4}: {d.Message}"
        | [] -> "error: the expression could not be compiled"

    /// Runs `run` in the frame. Errors outside the text come from the PDB's opens, which may name
    /// namespaces of assemblies the debuggee has not loaded: then it runs again without them.
    static let withoutFailingOpens (frame: FrameContext) (run: FrameContext -> Result<'T, EvaluationDiagnostic list>) =
        match run frame with
        | Error diagnostics when not frame.Opens.IsEmpty && diagnostics |> List.exists (fun d -> d.Range.IsNone) ->
            run { frame with Opens = [] }
        | result -> result

    /// The error text and query of a compilation, as the out parameters take them.
    static let outputs (compiled: Result<DkmCompiledClrInspectionQuery, string>) =
        match compiled with
        | Ok query -> null, query
        | Error text -> text, null

    /// Compiles with F#: Some(Ok query) or Some(Error message) for the user, or None when the F#
    /// compiler failed internally, so the caller falls back to C#.
    static let compile
        (what: string)
        (address: DkmClrInstructionAddress)
        (inspectionContext: DkmInspectionContext)
        (run: EvaluationSession -> FrameContext -> Result<CompiledExpression, EvaluationDiagnostic list>)
        =
        if not (Settings.enabled ()) then
            None
        else
            try
                let sw = Diagnostics.Stopwatch.StartNew()

                match Debuggee.session expressionTimeout address with
                | None -> Some(Error stillLoading)
                | Some session ->
                    let frame = Debuggee.frame address inspectionContext

                    match withoutFailingOpens frame (run session) with
                    | Ok compiled ->
                        Log.verbose "EC" (fun () ->
                            let fsharpType =
                                compiled.FSharpType |> Option.map (fun t -> $" (Type column {t})") |> Option.defaultValue ""

                            $"{what}: {compiled.ResultType}{fsharpType}, {sw.ElapsedMilliseconds} ms")
                        Some(Ok(query address.RuntimeInstance compiled))
                    | Error diagnostics ->
                        let text = errorText diagnostics
                        Log.verbose "EC" (fun () -> $"{what}: {text}, {sw.ElapsedMilliseconds} ms")
                        Some(Error text)
            with e ->
                Log.exn "EC" what e
                None

    /// The Locals query from F#, or None to fall back to C#. Return values are listed by the names
    /// the debugger gives them (the method that returned), except FSharp.Core's. `$ReturnValue` is
    /// the last of `$ReturnValue1`, `$ReturnValue2`, ..., so it is not listed again.
    static let compileLocals
        (inspectionContext: DkmInspectionContext)
        (address: DkmClrInstructionAddress)
        (argumentsOnly: bool)
        =
        if not (Settings.enabled ()) then
            None
        else
            try
                let sw = Diagnostics.Stopwatch.StartNew()

                match Debuggee.session localsTimeout address with
                // The C# query if it takes even longer.
                | None -> None
                | Some session ->
                    let runtime = address.RuntimeInstance
                    let debuggerAliases = Debuggee.aliases address inspectionContext
                    let frame = Debuggee.frameWith address debuggerAliases

                    let displayNames =
                        match debuggerAliases with
                        | null -> Map.empty
                        | aliases -> aliases |> Seq.map (fun a -> a.FullName, a.Name) |> Map.ofSeq

                    let displayName name =
                        displayNames |> Map.tryFind name |> Option.defaultValue name

                    let returnValues =
                        frame.Aliases |> List.filter (fun a -> a.Kind = AliasKind.ReturnValue)

                    let listed (a: Alias) =
                        a.Kind <> AliasKind.ReturnValue
                        || not (
                            (displayName a.Name).StartsWith("Microsoft.FSharp.", StringComparison.Ordinal)
                            || (a.Name = "$ReturnValue" && returnValues.Length > 1)
                        )

                    let frame =
                        { frame with
                            Aliases = frame.Aliases |> List.filter listed
                        }

                    match withoutFailingOpens frame (fun frame -> session.CompileLocals(frame, argumentsOnly)) with
                    | Ok compiled ->
                        Log.verbose "EC" (fun () ->
                            $"GetClrLocalVariableQuery: {compiled.Locals.Length} variable(s), aliases {frame.Aliases |> List.map (fun a -> displayName a.Name)}, {sw.ElapsedMilliseconds} ms")

                        let infos =
                            compiled.Locals
                            |> List.map (fun local ->
                                DkmClrLocalVariableInfo.Create(
                                    displayName local.Name,
                                    local.FullName,
                                    local.MethodName,
                                    (if local.ReadOnlyResult then
                                         DkmClrCompilationResultFlags.ReadOnlyResult
                                     else
                                         DkmClrCompilationResultFlags.None),
                                    DkmEvaluationResultCategory.Data,
                                    localTypeInfo local
                                ))
                            |> ResizeArray

                        Some(
                            DkmCompiledClrLocalsQuery.Create(
                                runtime,
                                null,
                                compilerId,
                                ReadOnlyCollection compiled.Assembly,
                                compiled.TypeName,
                                ReadOnlyCollection infos
                            )
                        )
                    | Error diagnostics ->
                        log $"GetClrLocalVariableQuery: {errorText diagnostics}"
                        None
            with e ->
                Log.exn "EC" "GetClrLocalVariableQuery" e
                None

    interface IDkmClrExpressionCompiler with

        member _.CompileExpression(expression, instructionAddress, inspectionContext, error, result) =
            // The Immediate window's text is a statement (no TreatAsExpression): it can declare a
            // variable, `let x = ...` (docs/DESIGN.md §17, item 31). Watch and DataTips are expressions.
            let isStatement =
                not (expression.CompilationFlags.HasFlag DkmEvaluationFlags.TreatAsExpression)

            let compiled =
                compile $"CompileExpression '{expression.Text}'" instructionAddress inspectionContext (fun session frame ->
                    if isStatement then
                        session.CompileStatement(frame, expression.Text)
                    else
                        session.CompileExpression(frame, expression.Text))

            match compiled with
            | Some compiled ->
                let text, query = outputs compiled
                error <- text
                result <- query
            | None -> expression.CompileExpression(instructionAddress, inspectionContext, &error, &result)

        member _.CompileAssignment(expression, instructionAddress, lValue, error, result) =
            let compiled =
                compile
                    $"CompileAssignment '{lValue.FullName}' <- '{expression.Text}'"
                    instructionAddress
                    lValue.InspectionContext
                    (fun session frame -> session.CompileAssignment(frame, lValue.FullName, expression.Text))

            match compiled with
            | Some compiled ->
                let text, query = outputs compiled
                error <- text
                result <- query
            | None -> expression.CompileAssignment(instructionAddress, lValue, &error, &result)

        member _.GetClrLocalVariableQuery(inspectionContext, instructionAddress, argumentsOnly) =
            match compileLocals inspectionContext instructionAddress argumentsOnly with
            | Some query -> query
            | None ->
                let query = inspectionContext.GetClrLocalVariableQuery(instructionAddress, argumentsOnly)

                if Settings.enabled () then
                    ReturnValues.filterLocals query
                else
                    query
