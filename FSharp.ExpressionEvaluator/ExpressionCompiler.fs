namespace FSharp.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation
open Microsoft.VisualStudio.Debugger.Clr

module private CompilerHelpers =

    let private csharpLanguage =
        let compilerId = DkmCompilerId(DkmVendorId.Microsoft, DkmLanguageId.CSharp)
        DkmLanguage.Create("C#", compilerId)

    let tryCompileExpression
        (expression: DkmLanguageExpression)
        (instructionAddress: DkmClrInstructionAddress)
        (inspectionContext: DkmInspectionContext)
        (error: byref<string>)
        (result: byref<DkmCompiledClrInspectionQuery>) =
        match ExpressionLowering.tryLowerExpression expression.Text with
        | Some lowered ->
            let loweredExpression = DkmLanguageExpression.Create(csharpLanguage, expression.CompilationFlags, lowered, null)
            try
                loweredExpression.CompileExpression(instructionAddress, inspectionContext, &error, &result)
                true
            finally
                loweredExpression.Close()
        | None ->
            false

    let tryCompileAssignment
        (expression: DkmLanguageExpression)
        (instructionAddress: DkmClrInstructionAddress)
        (lValue: DkmEvaluationResult)
        (error: byref<string>)
        (result: byref<DkmCompiledClrInspectionQuery>) =
        match ExpressionLowering.tryLowerExpression expression.Text with
        | Some lowered ->
            let loweredExpression = DkmLanguageExpression.Create(csharpLanguage, expression.CompilationFlags, lowered, null)
            try
                loweredExpression.CompileAssignment(instructionAddress, lValue, &error, &result)
                true
            finally
                loweredExpression.Close()
        | None ->
            false

/// <summary>
/// Minimal F# <see cref="IDkmClrExpressionCompiler"/> implementation.
///
/// Supported expressions are first lowered from a small F# subset into a C#-EE
/// friendly form and then delegated to the built-in C# compiler. Unsupported
/// expressions raise <see cref="System.NotImplementedException"/> so Concord can
/// fall through to the next registered compiler.
/// </summary>
type ExpressionCompiler() =
    interface IDkmClrExpressionCompiler with

        member _.CompileExpression
            ( expression         : DkmLanguageExpression,
              instructionAddress : DkmClrInstructionAddress,
              inspectionContext  : DkmInspectionContext,
              error              : byref<string>,
              result             : byref<DkmCompiledClrInspectionQuery> ) =
            if not (CompilerHelpers.tryCompileExpression expression instructionAddress inspectionContext &error &result) then
                raise (System.NotImplementedException())

        member _.CompileAssignment
            ( expression         : DkmLanguageExpression,
              instructionAddress : DkmClrInstructionAddress,
              lValue             : DkmEvaluationResult,
              error              : byref<string>,
              result             : byref<DkmCompiledClrInspectionQuery> ) =
            if not (CompilerHelpers.tryCompileAssignment expression instructionAddress lValue &error &result) then
                raise (System.NotImplementedException())

        member _.GetClrLocalVariableQuery
            ( inspectionContext  : DkmInspectionContext,
              instructionAddress : DkmClrInstructionAddress,
              argumentsOnly      : bool )
            : DkmCompiledClrLocalsQuery =
            raise (System.NotImplementedException())
