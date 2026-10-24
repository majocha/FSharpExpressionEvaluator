namespace FSharp.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.ComponentInterfaces

/// Registered for F# frames (docs/DESIGN.md §4, §6.10). Until the F# compiler lands (M3/M4) it runs
/// the M0 probes and chains every request to the next implementer, Roslyn's C# compiler.
type ExpressionCompiler() =

    static let log = Log.write "EC"
    static do log "ExpressionCompiler loaded"

    interface IDkmClrExpressionCompiler with

        member _.CompileExpression(expression, instructionAddress, inspectionContext, error, result) =
            Probes.run instructionAddress
            expression.CompileExpression(instructionAddress, inspectionContext, &error, &result)

        member _.CompileAssignment(expression, instructionAddress, lValue, error, result) =
            expression.CompileAssignment(instructionAddress, lValue, &error, &result)

        member _.GetClrLocalVariableQuery(inspectionContext, instructionAddress, argumentsOnly) =
            Probes.run instructionAddress
            let query =
                inspectionContext.GetClrLocalVariableQuery(instructionAddress, argumentsOnly)
                |> ReturnValues.filterLocals

            log $"GetClrLocalVariableQuery 0x{instructionAddress.MethodId.Token:X8}+IL_{instructionAddress.ILOffset:X4} argumentsOnly={argumentsOnly}: {if isNull query then [] else [ for l in query.LocalInfo -> l.VariableName ]}"
            query
