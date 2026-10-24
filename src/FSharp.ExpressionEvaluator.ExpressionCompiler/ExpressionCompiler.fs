namespace FSharp.ExpressionEvaluator

open System
open System.Collections.ObjectModel
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.Clr
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation
open FSharp.Compiler.Debugging.ExpressionEvaluation

/// Registered for F# frames (docs/DESIGN.md §4, §6). Expressions and assignments are compiled by the
/// F# compiler. The Locals query still comes from the next implementer (Roslyn's C# compiler), and so
/// does anything the F# compiler fails on internally (§6.10). FSHARP_EE_CSHARP=1 uses C# throughout.
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
            inspectionContext.GetClrLocalVariableQuery(instructionAddress, argumentsOnly)
            |> ReturnValues.filterLocals
