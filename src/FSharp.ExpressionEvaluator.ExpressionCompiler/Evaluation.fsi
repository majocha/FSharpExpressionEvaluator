/// Compiles F# expressions in the context of a stopped stack frame, for the Visual Studio
/// debugger's F# expression evaluator. The result is a small assembly with one static method
/// whose parameters mirror the frame, which the debugger executes against the frame's values.
module internal FSharp.ExpressionEvaluator.Evaluation

/// The contents of a module loaded in the debuggee.
[<RequireQualifiedAccess>]
type DebuggeeImage =
    /// A complete PE image (file layout). Needed for the frame's own module, whose method bodies
    /// supply the frame's local signature.
    | PE of image: byte[]

    /// The module's metadata blob (as the debugger exposes it) and some of its embedded managed
    /// resources, each with its offset in the resources directory. F# signature and optimization
    /// data are such resources; other resources and method bodies are not needed for references.
    | Metadata of metadata: byte[] * resources: (int * byte[]) list

/// A module loaded in the debuggee.
type DebuggeeModule =
    {
        /// The module's path in the debuggee, used to identify it; it need not exist on this machine.
        FileName: string

        Image: DebuggeeImage
    }

/// A local variable of the frame that is in scope at the stop location (from the PDB).
type FrameLocal =
    {
        Name: string

        /// Index of the local in the frame method's local signature.
        Slot: int
    }

/// What a debugger alias stands for (Concord's DkmClrAliasKind).
[<RequireQualifiedAccess>]
type AliasKind =
    /// `$exception`: the exception being thrown.
    | Exception

    /// `$stowedexception`: a WinRT stowed exception.
    | StowedException

    /// `$ReturnValue`, `$ReturnValue1`, ...: the value returned by a method just stepped over.
    | ReturnValue

    /// `$1`, `$2`, ...: an object id the user made.
    | ObjectId

    /// A variable the user declared in the Immediate window.
    | Variable

/// A pseudo-variable the debugger provides, read through its intrinsic methods.
type Alias =
    {
        /// The name the expression uses, e.g. `$exception` or `$1`.
        Name: string

        Kind: AliasKind

        /// The value's runtime type, as a CLR type name (assembly-qualified or not, with generic
        /// arguments in `[[...]]`). A type the session can't find reads as `obj`.
        TypeName: string
    }

/// The stopped frame an expression is evaluated in.
type FrameContext =
    {
        /// Simple name of the assembly that contains the frame's method.
        AssemblyName: string

        /// Metadata token of the frame's method.
        MethodToken: int

        /// Token of the frame method's local signature (StandAloneSig), as the debugger reports it,
        /// or 0 to take it from the method body (which needs a PE image of the frame's module).
        LocalSignatureToken: int

        /// Namespaces and modules opened at the frame's location (from the PDB import scope).
        Opens: string list

        /// Locals in scope at the frame's location, innermost last (later names shadow earlier ones).
        Locals: FrameLocal list

        /// The debugger's pseudo-variables.
        Aliases: Alias list

        /// Display name of the assembly defining `Microsoft.VisualStudio.Debugger.Clr.IntrinsicMethods`,
        /// which aliases are read through.
        IntrinsicsAssembly: string
    }

/// A compiled expression: an assembly with `TypeName.MethodName(this?, parameters...)`.
type CompiledExpression =
    {
        Assembly: byte[]
        TypeName: string
        MethodName: string

        /// The F# type of the result, for display.
        ResultType: string

        /// Format specifiers that followed the expression (`x, h` gives `h`).
        FormatSpecifiers: string list

        /// The expression may have side effects (assignments, calls, function applications), so
        /// the debugger won't run it where side effects are not allowed.
        PotentialSideEffect: bool

        /// The result is not a variable that can be assigned to.
        ReadOnlyResult: bool

        /// The result is a `bool` (as a breakpoint condition needs).
        BoolResult: bool
    }

/// A variable of the Locals window: the query method that reads it.
type CompiledLocal =
    {
        /// The variable's name, as an expression names it (`x`, `this`, `$exception`).
        Name: string

        MethodName: string

        /// The F# type of the variable, for display.
        ResultType: string

        /// The variable can't be assigned to.
        ReadOnlyResult: bool
    }

/// The Locals window's query: an assembly with one method per variable.
type CompiledLocals =
    {
        Assembly: byte[]
        TypeName: string
        Locals: CompiledLocal list
    }

/// A diagnostic from compiling an expression.
type EvaluationDiagnostic =
    {
        Number: int
        Message: string
        IsError: bool

        /// Where in the expression text the diagnostic is: lines from 1, columns from 0. None
        /// when it is not in the text (e.g. in the frame's context).
        Range: FSharp.Compiler.Text.range option
    }

/// Type-checking state for one debuggee: imports built once, reused for every expression.
/// Disposing it releases the copies of the debuggee's modules it holds.
[<Sealed>]
type EvaluationSession =
    interface System.IDisposable

    /// Creates a session over the modules the debuggee has loaded, read from memory: no file
    /// of the debuggee needs to exist on this machine.
    static member Create: modules: DebuggeeModule list -> EvaluationSession

    /// Compiles an expression in the context of a frame. The query method takes the frame's
    /// `this` and parameters as parameters and starts its locals with the frame's local signature,
    /// so the debugger can run it against the frame. With `localsAsParameters` the frame's locals
    /// are trailing parameters instead, so the method can be invoked directly (for testing).
    /// In a generic frame the query class takes the frame's class type parameters and the query
    /// method its method type parameters, to be instantiated as the frame's.
    member CompileExpression:
        frame: FrameContext * text: string * ?localsAsParameters: bool ->
            Result<CompiledExpression, EvaluationDiagnostic list>

    /// Compiles the Locals window's query for a frame: one method per variable shown, in order:
    /// the debugger's aliases, `this`, the parameters, a closure's captured values or a state
    /// machine's hoisted variables, then the locals in scope. Compiler temporaries are left out.
    /// With `argumentsOnly`, only the parameters (for the call stack's argument values).
    member CompileLocals:
        frame: FrameContext * argumentsOnly: bool * ?localsAsParameters: bool ->
            Result<CompiledLocals, EvaluationDiagnostic list>

    /// Compiles `target <- value` in the context of a frame, as for the debugger's edits of a
    /// value: the frame's parameters, locals and the fields of `this` can be assigned. The query
    /// method is as for `CompileExpression` and returns unit.
    member CompileAssignment:
        frame: FrameContext * target: string * value: string * ?localsAsParameters: bool ->
            Result<CompiledExpression, EvaluationDiagnostic list>

    /// Compiles queries in a frame of the given method and throws them away, so that the first
    /// real ones find the imports they need loaded and the compiler's code compiled by the JIT
    /// (docs/DESIGN.md §6.9). Returns whether both compiled; errors are otherwise ignored.
    member WarmUp: assemblyName: string * methodToken: int -> bool

    /// Runs the compiler once over a session of this process's own FSharp.Core and runtime
    /// assemblies, read from disk, then disposes it. Started before the debuggee's first break, it
    /// leaves the compiler's code compiled by the JIT for the debuggee's session (docs/DESIGN.md
    /// §6.9). Returns how many modules the session had and whether its queries compiled.
    static member WarmUpCompiler: unit -> int * bool
