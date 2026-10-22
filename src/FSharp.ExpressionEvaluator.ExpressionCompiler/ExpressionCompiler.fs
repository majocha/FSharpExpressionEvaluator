namespace FSharp.ExpressionEvaluator

open System
open Microsoft.VisualStudio.Debugger
open Microsoft.VisualStudio.Debugger.CallStack
open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation

/// M0 spike (docs/DESIGN.md §4, §6.10): registered for F# frames, runs probes, and chains
/// every request to the next implementer (Roslyn's C# compiler and decoders).
type ExpressionCompiler() =

    static let log = Log.write "EC"
    static do log "ExpressionCompiler loaded"

    /// When set, frame names get a visible marker so the decoder path can be observed from DTE.
    static let markers = Environment.GetEnvironmentVariable "FSHARP_EE_SPIKE_MARKERS" = "1"

    interface IDkmClrExpressionCompiler with

        member _.CompileExpression(expression, instructionAddress, inspectionContext, error, result) =
            log $"CompileExpression '{expression.Text}' -> chained"
            Probes.run instructionAddress
            expression.CompileExpression(instructionAddress, inspectionContext, &error, &result)

        member _.CompileAssignment(expression, instructionAddress, lValue, error, result) =
            log $"CompileAssignment '{lValue.FullName} = {expression.Text}' -> chained"
            expression.CompileAssignment(instructionAddress, lValue, &error, &result)

        member _.GetClrLocalVariableQuery(inspectionContext, instructionAddress, argumentsOnly) =
            log $"GetClrLocalVariableQuery argumentsOnly={argumentsOnly} -> chained"
            Probes.run instructionAddress
            inspectionContext.GetClrLocalVariableQuery(instructionAddress, argumentsOnly)

    interface IDkmLanguageFrameDecoder with

        member _.GetFrameName(inspectionContext, workList, frame, argumentFlags, completionRoutine) =
            log $"GetFrameName flags={argumentFlags} -> chained"

            let complete (r: DkmGetFrameNameAsyncResult) =
                if markers && not (isNull r.FrameName) then
                    completionRoutine.Invoke(DkmGetFrameNameAsyncResult(r.FrameName + " [F#]"))
                else
                    completionRoutine.Invoke r

            inspectionContext.GetFrameName(workList, frame, argumentFlags, DkmCompletionRoutine<_>(complete))

        member _.GetFrameReturnType(inspectionContext, workList, frame, completionRoutine) =
            inspectionContext.GetFrameReturnType(workList, frame, completionRoutine)

    interface IDkmLanguageInstructionDecoder with

        member _.GetMethodName(languageInstructionAddress, argumentFlags) =
            log "GetMethodName -> chained"
            languageInstructionAddress.GetMethodName argumentFlags
