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
/// next implementer, Roslyn's C# compiler (§6.10). FSHARP_EE_CSHARP=1 uses C# throughout.
type ExpressionCompiler() =

    static let log = Log.write "EC"
    static do log "ExpressionCompiler loaded"

    static let compilerId =
        DkmCompilerId(DkmVendorId.Microsoft, Guid "AB4F38C9-B6E6-43BA-BE3B-58080B2CCCE3")

    static let csharpOnly =
        Environment.GetEnvironmentVariable "FSHARP_EE_CSHARP" = "1"

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
            CustomTypeInfo = null
        )

    static let errorText (diagnostics: EvaluationDiagnostic list) =
        match diagnostics with
        | d :: _ -> $"error FS{d.Number:D4}: {d.Message}"
        | [] -> "error: the expression could not be compiled"

    /// Compiles with F#: Some(Ok query) or Some(Error message) for the user, or None when the F#
    /// compiler failed internally, so the caller falls back to C#.
    static let compile
        (what: string)
        (address: DkmClrInstructionAddress)
        (inspectionContext: DkmInspectionContext)
        (run: EvaluationSession -> FrameContext -> Result<CompiledExpression, EvaluationDiagnostic list>)
        =
        if csharpOnly then
            None
        else
            try
                let session = Debuggee.session address
                let frame = Debuggee.frame address inspectionContext

                let result =
                    match run session frame with
                    // Errors outside the text come from the PDB's opens, which may name namespaces of
                    // assemblies the debuggee has not loaded: try again without them.
                    | Error diagnostics when
                        not frame.Opens.IsEmpty && diagnostics |> List.exists (fun d -> d.Range.IsNone)
                        ->
                        run session { frame with Opens = [] }
                    | result -> result

                match result with
                | Ok compiled ->
                    log $"{what}: {compiled.ResultType}"
                    Some(Ok(query address.RuntimeInstance compiled))
                | Error diagnostics ->
                    let text = errorText diagnostics
                    log $"{what}: {text}"
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
        if csharpOnly then
            None
        else
            try
                let runtime = address.RuntimeInstance
                let session = Debuggee.session address
                let frame = Debuggee.frame address inspectionContext

                let displayNames =
                    match runtime.GetAliases inspectionContext with
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

                let result =
                    match session.CompileLocals(frame, argumentsOnly) with
                    | Error diagnostics when not frame.Opens.IsEmpty && diagnostics |> List.exists (fun d -> d.Range.IsNone) ->
                        session.CompileLocals({ frame with Opens = [] }, argumentsOnly)
                    | result -> result

                match result with
                | Ok compiled ->
                    log
                        $"GetClrLocalVariableQuery: {compiled.Locals.Length} variable(s), aliases {frame.Aliases |> List.map (fun a -> displayName a.Name)}"

                    let infos =
                        compiled.Locals
                        |> List.map (fun local ->
                            DkmClrLocalVariableInfo.Create(
                                displayName local.Name,
                                local.Name,
                                local.MethodName,
                                (if local.ReadOnlyResult then
                                     DkmClrCompilationResultFlags.ReadOnlyResult
                                 else
                                     DkmClrCompilationResultFlags.None),
                                DkmEvaluationResultCategory.Data,
                                null
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
            let compiled =
                compile $"CompileExpression '{expression.Text}'" instructionAddress inspectionContext (fun session frame ->
                    session.CompileExpression(frame, expression.Text))

            match compiled with
            | Some(Ok query) ->
                error <- null
                result <- query
            | Some(Error text) ->
                error <- text
                result <- null
            | None -> expression.CompileExpression(instructionAddress, inspectionContext, &error, &result)

        member _.CompileAssignment(expression, instructionAddress, lValue, error, result) =
            let compiled =
                compile
                    $"CompileAssignment '{lValue.FullName}' <- '{expression.Text}'"
                    instructionAddress
                    lValue.InspectionContext
                    (fun session frame -> session.CompileAssignment(frame, lValue.FullName, expression.Text))

            match compiled with
            | Some(Ok query) ->
                error <- null
                result <- query
            | Some(Error text) ->
                error <- text
                result <- null
            | None -> expression.CompileAssignment(instructionAddress, lValue, &error, &result)

        member _.GetClrLocalVariableQuery(inspectionContext, instructionAddress, argumentsOnly) =
            match compileLocals inspectionContext instructionAddress argumentsOnly with
            | Some query -> query
            | None ->
                inspectionContext.GetClrLocalVariableQuery(instructionAddress, argumentsOnly)
                |> ReturnValues.filterLocals
