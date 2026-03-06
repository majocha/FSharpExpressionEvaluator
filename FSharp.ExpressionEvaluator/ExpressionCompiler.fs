namespace FSharp.ExpressionEvaluator

open Microsoft.VisualStudio.Debugger.ComponentInterfaces
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation
open Microsoft.VisualStudio.Debugger.Clr

/// <summary>
/// Skeleton implementation of <see cref="IDkmClrExpressionCompiler"/> for F#.
///
/// Status
/// ------
/// This class is <b>not yet registered</b> with the Concord component system
/// (it is absent from <c>Compiler.vsdconfigxml</c>'s <c>VsdConfigXmlFiles</c>
/// item group). It exists as an architectural placeholder and as the intended
/// home for future F#-specific expression compilation.
///
/// Roadmap
/// -------
/// 1. <b>Delegation</b> – implement each method by throwing
///    <see cref="System.NotImplementedException"/> (maps to E_NOTIMPL in the
///    Concord component host), which causes the host to route the call to the
///    next registered compiler—typically the C# EE at a lower component level.
///    This provides safe pass-through without disrupting C# or VB debugging.
///
/// 2. <b>F#-specific preprocessing</b> – transform F# expression syntax (e.g.
///    pipeline operators, pattern-match literals, DU construction) into a form
///    the underlying C# EE can evaluate, and delegate the transformed
///    expression.
///
/// 3. <b>Full F# compilation</b> – invoke the F# compiler service
///    (e.g. FSharp.Compiler.Service) to produce MSIL directly, replacing the
///    C# EE delegation entirely.
///
/// Roslyn reference
/// ----------------
/// The structure mirrors <c>CSharpExpressionCompiler</c> in the Roslyn
/// <c>src/ExpressionEvaluator/CSharp/Source/ExpressionCompiler/</c> subtree.
/// Each method corresponds to the same method on
/// <c>IDkmClrExpressionCompiler</c>.
/// </summary>
type ExpressionCompiler() =
    interface IDkmClrExpressionCompiler with

        /// Compile <paramref name="expression"/> into MSIL for evaluation.
        /// Currently unimplemented; raises E_NOTIMPL so the Concord host falls
        /// through to the next registered compiler.
        member _.CompileExpression
            ( expression         : DkmLanguageExpression,
              instructionAddress : DkmClrInstructionAddress,
              inspectionContext  : DkmInspectionContext,
              error              : byref<string>,
              result             : byref<DkmCompiledClrInspectionQuery> ) =
            raise (System.NotImplementedException())

        /// Compile an assignment expression.
        /// Currently unimplemented; raises E_NOTIMPL.
        member _.CompileAssignment
            ( expression         : DkmLanguageExpression,
              instructionAddress : DkmClrInstructionAddress,
              lValue             : DkmEvaluationResult,
              error              : byref<string>,
              result             : byref<DkmCompiledClrInspectionQuery> ) =
            raise (System.NotImplementedException())

        /// Return a query for the local variables visible at the current
        /// instruction address.
        /// Currently unimplemented; raises E_NOTIMPL.
        member _.GetClrLocalVariableQuery
            ( inspectionContext  : DkmInspectionContext,
              instructionAddress : DkmClrInstructionAddress,
              argumentsOnly      : bool )
            : DkmCompiledClrLocalsQuery =
            raise (System.NotImplementedException())
