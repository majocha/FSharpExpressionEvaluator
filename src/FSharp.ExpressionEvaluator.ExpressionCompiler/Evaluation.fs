module internal FSharp.ExpressionEvaluator.Evaluation

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Internal.Utilities.Library
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.CheckBasics
open FSharp.Compiler.CheckDeclarations
open FSharp.Compiler.CheckExpressions
open FSharp.Compiler.CheckExpressionsOps
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CompilerConfig
open FSharp.Compiler.CompilerDiagnostics
open FSharp.Compiler.CompilerImports
open FSharp.Compiler.DependencyManager
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.IlxGen
open FSharp.Compiler.Lexhelp
open FSharp.Compiler.NameResolution
open FSharp.Compiler.OptimizeInputs
open FSharp.Compiler.ParseAndCheckInputs
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeBasics
open FSharp.Compiler.TypedTreeOps

[<RequireQualifiedAccess>]
type DebuggeeImage =
    | PE of image: byte[]
    | Metadata of metadata: byte[] * resources: (int * byte[]) list

type DebuggeeModule =
    {
        FileName: string
        Image: DebuggeeImage
    }

type FrameLocal = { Name: string; Slot: int }

[<RequireQualifiedAccess>]
type AliasKind =
    | Exception
    | StowedException
    | ReturnValue
    | ObjectId
    | Variable

type Alias =
    {
        Name: string
        Kind: AliasKind
        TypeName: string
    }

type FrameContext =
    {
        AssemblyName: string
        MethodToken: int
        LocalSignatureToken: int
        Opens: string list
        Locals: FrameLocal list
        Aliases: Alias list
        IntrinsicsAssembly: string
    }

type CompiledExpression =
    {
        Assembly: byte[]
        TypeName: string
        MethodName: string
        ResultType: string
        FormatSpecifiers: string list
        PotentialSideEffect: bool
        ReadOnlyResult: bool
        BoolResult: bool
    }

type CompiledLocal =
    {
        Name: string
        MethodName: string
        ResultType: string
        ReadOnlyResult: bool
    }

type CompiledLocals =
    {
        Assembly: byte[]
        TypeName: string
        Locals: CompiledLocal list
    }

type EvaluationDiagnostic =
    {
        Number: int
        Message: string
        IsError: bool
        Range: range option
    }

/// Sequences the compilation phases, stopping at the first that reports errors.
type private ResultBuilder() =
    member _.Bind(r: Result<'T, 'E>, f: 'T -> Result<'U, 'E>) = Result.bind f r
    member _.Return x : Result<'T, 'E> = Ok x
    member _.Zero() : Result<unit, 'E> = Ok()
    member _.Combine(r: Result<unit, 'E>, rest: Result<'T, 'E>) = Result.bind (fun () -> rest) r
    member _.Delay f = f ()

let private result = ResultBuilder()

/// Names of the synthesized query module and method (as Roslyn's `<>x.<>m0`).
[<Literal>]
let private QueryTypeName = "<>x"

[<Literal>]
let private QueryMethodName = "<>m0"

/// The name of the n-th query method of the query module: `<>m0`, `<>m1`, ...
let private queryMethodName (n: int) = $"<>m{n}"

let private isQueryMethodName (name: string) = name.StartsWith "<>m"

let private queryFileName = "<>x.fs"

/// Placeholder methods of `<>x` standing for loads and stores of fields of compiler-generated
/// classes until code is generated (see `replaceFieldPlaceholders`): `<>ldfld0`, `<>stfld1`, ...
let private fieldPlaceholderName load (n: int) =
    if load then $"<>ldfld{n}" else $"<>stfld{n}"

let private isFieldLoadPlaceholder (mspec: ILMethodSpec) =
    mspec.MethodRef.DeclaringTypeRef.Name = QueryTypeName && mspec.Name.StartsWith "<>ldfld"

let private toDiagnostic (d: PhasedDiagnostic) =
    {
        Number = d.Number
        Message = d.FormatCore(false, false)
        IsError = (d.Severity = FSharpDiagnosticSeverity.Error)
        Range = d.Range
    }

/// Runs `f` with a capturing diagnostics logger; fails with the captured errors, if any, other
/// than the `ignored` ones.
let private withDiagnosticsIgnoring (ignored: int list) (phase: BuildPhase) (f: unit -> 'T) : Result<'T, EvaluationDiagnostic list> =
    let logger = CapturingDiagnosticsLogger("ExpressionEvaluation")

    use _ = UseBuildPhase phase
    use _ = UseDiagnosticsLogger logger

    let value =
        try
            Some(f ())
        with
        | ReportedError _ -> None
        | e ->
            errorRecovery e range0
            None

    let errors =
        logger.Diagnostics
        |> List.map toDiagnostic
        |> List.filter (fun d -> d.IsError && not (List.contains d.Number ignored))

    match value with
    | Some value when errors.IsEmpty -> Result.Ok value
    | _ -> Result.Error errors

let private withDiagnostics phase f = withDiagnosticsIgnoring [] phase f

/// Finds a method definition by token, with the chain of type definitions enclosing it.
let private tryFindMethod (ilModule: ILModuleDef) (token: int) =
    let rid = token &&& 0x00FFFFFF

    let rec search (enclosing: ILTypeDef list) (typeDefs: ILTypeDef list) =
        typeDefs
        |> List.tryPick (fun td ->
            let path = enclosing @ [ td ]

            match td.Methods.AsList() |> List.tryFind (fun md -> md.MetadataIndex = rid) with
            | Some md -> Some(path, md)
            | None -> search path (td.NestedTypes.AsList()))

    search [] (ilModule.TypeDefs.AsList())

/// The module or namespace at a path of names, e.g. `["TestDebuggee"; "Program"]`.
let rec private tryModuleOrNamespace (modul: ModuleOrNamespace) path =
    match path with
    | [] -> Some modul
    | part :: rest ->
        match modul.ModuleOrNamespaceType.ModulesAndNamespacesByDemangledName.TryFind part with
        | Some child -> tryModuleOrNamespace child rest
        | None -> None

/// The F# entities corresponding to a chain of IL type definitions, outermost first, as far as
/// they exist in the F# view of the assembly (closures and other generated types do not).
let private fsharpEntities (ccu: CcuThunk) (enclosing: ILTypeDef list) =
    match enclosing with
    | [] -> []
    | outer :: nested ->
        let ns, name = splitILTypeName outer.Name

        match tryModuleOrNamespace ccu.Contents ns with
        | None -> []
        | Some nsEntity ->
            let rec types (parent: Entity) (names: string list) acc =
                match names with
                | [] -> List.rev acc
                | compiledName :: rest ->
                    match parent.ModuleOrNamespaceType.AllEntitiesByCompiledAndLogicalMangledNames.TryFind compiledName with
                    | Some e -> types e rest (e :: acc)
                    | None -> List.rev acc

            types nsEntity (name :: (nested |> List.map _.Name)) []

/// The modules whose top-level code a frame is in: `main@` of an implicit entry point, or a static
/// initializer. That code is in `<StartupCode$Assembly>.$My.Program`, which is no F# entity, so the
/// modules' values (mutable ones are not locals there) would not be in scope. A file that starts
/// with a module names the type after it. A file that starts with a namespace names it after the
/// file, and its modules are those whose values the code sets: the type has a field `name@line`
/// for each of them, outer modules first.
let private startupCodeModules (ccu: CcuThunk) (enclosing: ILTypeDef list) =
    match enclosing with
    | [ td ] when td.Name.StartsWith "<StartupCode$" ->
        match td.Name.IndexOf ">.$" with
        | -1 -> []
        | i ->
            match tryModuleOrNamespace ccu.Contents (List.ofArray (td.Name.Substring(i + 3).Split '.')) with
            | Some modul when modul.IsModule -> [ modul ]
            | _ ->
                let fields =
                    td.Fields.AsList()
                    |> List.choose (fun f ->
                        match f.Name.LastIndexOf '@' with
                        | -1 -> None
                        | at ->
                            match Int32.TryParse(f.Name.Substring(at + 1)) with
                            | true, line -> Some(f.Name.Substring(0, at), line)
                            | _ -> None)
                    |> set

                let rec modules (parent: ModuleOrNamespace) =
                    [
                        for child in parent.ModuleOrNamespaceType.ModuleAndNamespaceDefinitions do
                            if child.IsModule then
                                child
                            yield! modules child
                    ]

                modules ccu.Contents
                |> List.filter (fun modul ->
                    modul.ModuleOrNamespaceType.AllValsAndMembers
                    |> Seq.exists (fun v -> fields.Contains(v.LogicalName, v.Range.StartLine)))
    | _ -> []

/// The path of a module or namespace as an `open` names it, e.g. `global.TestDebuggee.Program`.
let private sourcePath (e: Entity) =
    PrettyNaming.MangledGlobalName :: e.CompilationPath.MangledPath @ [ e.DisplayName ]

/// Lines of the query source before the expression: the module, `let <>m0 () =` and `(`.
[<Literal>]
let private LinesBeforeExpression = 3

/// Columns the expression is indented by in the query source.
[<Literal>]
let private ExpressionIndent = 8

/// The source of the query module: `let <>m0 () = (expression)` and so on, one method per
/// expression. The frame's opens are not in it but in the environment it is checked in, under the
/// frame's values, which an open would otherwise shadow.
let private querySource (texts: string list) =
    let query n (text: string) =
        let indented =
            text.Replace("\r\n", "\n").Split('\n')
            |> Array.map (fun line -> String(' ', ExpressionIndent) + line)
            |> String.concat "\n"

        $"let ``{queryMethodName n}`` () =\n    (\n{indented}\n    )\n"

    $"module ``{QueryTypeName}``\n" + String.concat "" (List.mapi query texts)

/// The end of the lexical unit of `text` at `i`: past a string or character literal, a
/// backticked name or a line comment starting there, otherwise `i + 1`.
let private unitEnd (text: string) i =
    let n = text.Length

    let at i (s: string) =
        String.CompareOrdinal(text, i, s, 0, s.Length) = 0

    let after (closing: string) from =
        match text.IndexOf(closing, from, StringComparison.Ordinal) with
        | -1 -> n
        | j -> j + closing.Length

    // End of the string literal starting at i (regular, verbatim or triple-quoted)
    let stringEnd i =
        if at i "\"\"\"" then
            after "\"\"\"" (i + 3)
        else
            let verbatim = i > 0 && text[i - 1] = '@'

            let rec scan j =
                if j >= n then n
                elif verbatim && at j "\"\"" then scan (j + 2)
                elif not verbatim && text[j] = '\\' then scan (j + 2)
                elif text[j] = '"' then j + 1
                else scan (j + 1)

            scan (i + 1)

    if text[i] = '"' then
        stringEnd i
    elif at i "``" then
        after "``" (i + 2)
    elif at i "//" then
        after "\n" i
    elif text[i] = '\'' && i + 2 < n && text[i + 2] = '\'' then
        i + 3
    elif text[i] = '\'' && i + 1 < n && text[i + 1] = '\\' then
        after "'" (i + 2)
    else
        i + 1

/// Alias names such as `$exception` and `$1` are not F# identifiers: outside string and character
/// literals, backticked names and line comments, `$name` becomes ``$name``.
let private quoteDollarNames (text: string) =
    let out = Text.StringBuilder()
    // For each character of the output, the index of the input character it comes from
    let origins = ResizeArray<int>()
    let n = text.Length
    let isNameChar c = Char.IsLetterOrDigit c || c = '_'

    let rec go i =
        if i < n then
            if
                text[i] = '$'
                && i + 1 < n
                && isNameChar text[i + 1]
                && (i = 0 || not (isNameChar text[i - 1]))
            then
                let mutable j = i + 1

                while j < n && isNameChar text[j] do
                    j <- j + 1

                out.Append("``").Append(text, i, j - i).Append("``") |> ignore
                origins.AddRange [ i; i ]
                origins.AddRange [ i .. j - 1 ]
                origins.AddRange [ j; j ]
                go j
            else
                let next = unitEnd text i
                out.Append(text, i, next - i) |> ignore
                origins.AddRange [ i .. next - 1 ]
                go next

    go 0
    out.ToString(), origins.ToArray()

/// Maps a range of the query source onto the user's expression text (before `$name` quoting):
/// lines from 1, columns from 0, in a file named "expression". Ranges outside it map to None.
let private rangeInExpression (text: string) (quoted: string) (origins: int[]) (r: range) =
    let lineStarts (s: string) =
        [|
            yield 0
            for i in 0 .. s.Length - 1 do
                if s[i] = '\n' then
                    yield i + 1
        |]

    let quotedStarts = lineStarts quoted
    let textStarts = lineStarts text
    let first = LinesBeforeExpression + 1

    let toExpression (p: pos) =
        let line = p.Line - first

        if line < 0 || line >= quotedStarts.Length then
            None
        else
            let lineEnd =
                if line + 1 < quotedStarts.Length then
                    quotedStarts[line + 1] - 1
                else
                    quoted.Length

            let offset = min lineEnd (quotedStarts[line] + max 0 (p.Column - ExpressionIndent))

            let original =
                if offset < origins.Length then
                    origins[offset]
                else
                    text.Length

            let textLine = (textStarts |> Array.findIndexBack (fun start -> start <= original))
            Some(Position.mkPos (textLine + 1) (original - textStarts[textLine]))

    match toExpression r.Start, toExpression r.End with
    | Some startPos, Some endPos -> Some(mkRange "expression" startPos endPos)
    | _ -> None

/// Format specifiers the debugger knows (as for C#), besides `view(...)`.
let private knownFormatSpecifiers =
    set [ "ac"; "d"; "dynamic"; "h"; "hidden"; "nq"; "nse"; "raw"; "results" ]

/// Splits trailing format specifiers off an expression: `x, h, nq` is `x` with `h` and `nq`. A
/// trailing run of known specifiers after top-level commas counts, so `(x, h)` stays a tuple.
let private splitFormatSpecifiers (text: string) =
    // Top-level commas, outside brackets, literals and comments
    let commas =
        let rec go i depth acc =
            if i >= text.Length then
                List.rev acc
            else
                match text[i] with
                | '('
                | '['
                | '{' -> go (i + 1) (depth + 1) acc
                | ')'
                | ']'
                | '}' -> go (i + 1) (depth - 1) acc
                | ',' when depth = 0 -> go (i + 1) depth (i :: acc)
                | _ -> go (unitEnd text i) depth acc

        go 0 0 []

    let isSpecifier (s: string) =
        knownFormatSpecifiers.Contains s || (s.StartsWith "view(" && s.EndsWith ")")

    // Walk the segments after the commas from the end while they are specifiers.
    let ends = commas @ [ text.Length ]

    let segments =
        List.zip (-1 :: commas) ends
        |> List.map (fun (start, stop) -> text.Substring(start + 1, stop - start - 1).Trim())

    let specifiers = segments |> List.rev |> List.takeWhile isSpecifier |> List.rev

    if specifiers.IsEmpty || specifiers.Length = segments.Length then
        text, []
    else
        let cut = List.item (commas.Length - specifiers.Length) commas
        text.Substring(0, cut), specifiers

/// A CLR type name as the debugger reports it, e.g. ``System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]][], System.Private.CoreLib``.
type private ClrTypeName =
    {
        /// Full name, nested types separated by '+'
        Name: string
        Args: ClrTypeName list
        ArrayRanks: int list
        Assembly: string option
    }

let private parseClrTypeName (s: string) =
    let mutable i = 0
    let peek () = if i < s.Length then s[i] else '\000'

    // `nested`: inside brackets, where an assembly name ends at ']'
    let rec parse nested =
        let start = i

        while i < s.Length && "[],".IndexOf s[i] < 0 do
            i <- i + 1

        let name = s.Substring(start, i - start).Trim()

        let args =
            if peek () = '[' && i + 1 < s.Length && s[i + 1] <> ']' && s[i + 1] <> ',' then
                i <- i + 1

                let rec loop acc =
                    let arg =
                        if peek () = '[' then
                            i <- i + 1
                            let arg = parse true
                            i <- i + 1 // ']'
                            arg
                        else
                            parse true

                    if peek () = ',' then
                        i <- i + 1
                        loop (arg :: acc)
                    else
                        i <- i + 1 // ']'
                        List.rev (arg :: acc)

                loop []
            else
                []

        let ranks =
            [
                while peek () = '[' do
                    let close = s.IndexOf(']', i)
                    let close = if close < 0 then s.Length - 1 else close
                    yield s.Substring(i + 1, close - i - 1).Split(',').Length
                    i <- close + 1
            ]

        let assembly =
            if peek () = ',' then
                i <- i + 1
                let start = i

                while i < s.Length && not (nested && s[i] = ']') do
                    i <- i + 1

                Some(s.Substring(start, i - start).Trim())
            else
                None

        {
            Name = name
            Args = args
            ArrayRanks = ranks
            Assembly = assembly
        }

    parse false

/// The F# type for a CLR type name, looked up in the given assembly when it names one the
/// session imports, otherwise in every imported assembly.
let private resolveClrType
    (g: TcGlobals.TcGlobals)
    (amap: Import.ImportMap)
    (scopes: ILScopeRef list)
    (scopeOfAssembly: string -> ILScopeRef option)
    (typeName: string)
    =
    let m = range0

    let rec resolve (t: ClrTypeName) =
        let path = t.Name.Split('+') |> List.ofArray
        let enclosing, name = List.frontAndBack path

        let candidates =
            match t.Assembly |> Option.bind scopeOfAssembly with
            | Some scope -> scope :: scopes
            | None -> scopes

        let tcref =
            candidates
            |> List.tryPick (fun scope ->
                match ILTypeRef.Create(scope, enclosing, name) with
                | Import.TryImportILTypeRef amap m tcref -> Some tcref
                | _ -> None)

        let args = t.Args |> List.map resolve

        match tcref with
        | Some(tcref: TyconRef) when args |> List.forall Option.isSome && tcref.Typars.Length = args.Length ->
            let ty = g.improveType tcref (args |> List.choose id) g.knownWithoutNull

            Some(
                (ty, List.rev t.ArrayRanks)
                ||> List.fold (fun ty rank ->
                    if rank = 1 then
                        mkArrayType g ty
                    else
                        mkArrayTy g rank g.knownWithoutNull ty m)
            )
        | _ -> None

    try
        resolve (parseClrTypeName typeName)
    with _ ->
        None
    |> Option.defaultValue g.obj_ty_ambivalent

/// Replaces reads and writes of the given Vals with expressions built at their range: field
/// variables become field loads and stores on `this` (as class `let` fields are compiled), aliases
/// become calls to the debugger's intrinsic methods.
let private replaceVariables (reads: (Val * (range -> Expr)) list) (writes: (Val * (range -> Expr -> Expr)) list) expr =
    match reads, writes with
    | [], [] -> expr
    | _ ->
        let readsByStamp = reads |> List.map (fun (v, f) -> v.Stamp, f) |> dict
        let writesByStamp = writes |> List.map (fun (v, f) -> v.Stamp, f) |> dict

        let intercept recurse (e: Expr) =
            match e with
            | Expr.Val(vref, _, m) when readsByStamp.ContainsKey vref.Stamp ->
                let read = readsByStamp[vref.Stamp]
                Some(read m)
            | Expr.Op(TOp.LValueOp(LSet, vref), _, [ value ], m) when writesByStamp.ContainsKey vref.Stamp ->
                let write = writesByStamp[vref.Stamp]
                Some(write m (recurse value))
            | _ -> None

        RewriteExpr
            {
                PreIntercept = Some intercept
                PostTransform = (fun _ -> None)
                PreInterceptBinding = None
                RewriteQuotations = true
                StackGuard = StackGuard("ExpressionEvaluation")
            }
            expr

/// Gives `<>m0` the frame's `this`, parameters and locals as its own parameters: the type checker
/// saw them as free locals, so after this rewrite IlxGen stores them as method arguments. Other
/// variables (fields of `this`, aliases) are replaced by their reads, and a generic result type
/// is fixed to `obj`, since the debugger runs the query without choosing type arguments for it.
/// In a generic frame `<>m0` is generic over the frame's type parameters, class ones first.
let private bindFrameValues
    (g: TcGlobals.TcGlobals)
    (frameTypars: Typars)
    (frameVals: Val list)
    (reads: (Val * (range -> Expr)) list)
    (writes: (Val * (range -> Expr -> Expr)) list)
    (contents: ModuleOrNamespaceContents)
    =
    let checkedBodies = Collections.Generic.Dictionary<string, Expr * TType>()

    let rewriteBinding (TBind(v, expr, sp) as bind) =
        if not (isQueryMethodName v.LogicalName) then
            bind
        else
            let expr =
                match stripDebugPoints expr with
                | Expr.TyLambda(_, typars, body, _, _) ->
                    instExpr g (mkTyparInst typars (typars |> List.map (fun _ -> g.obj_ty_ambivalent))) body
                | expr -> expr

            match stripDebugPoints expr with
            | Expr.Lambda(_, _, _, [ unitArg ], body0, m, bodyTy) ->
                let body = replaceVariables reads writes body0

                // Frame values are mutable only so that an assignment checks; its writes are now
                // stores to the arguments. Immutable copies keep the optimizer from turning them
                // into ref cells (as it does for mutable values used in an inner lambda).
                let frameVals, body =
                    if frameVals |> List.exists _.IsMutable then
                        let copies =
                            frameVals |> List.map (fun v -> fst (mkLocal v.Range v.LogicalName v.Type))

                        let remap =
                            (Remap.Empty, frameVals, copies)
                            |||> List.fold2 (fun r v c -> addValRemap v c r)

                        copies, remapExpr g CloneAll remap body
                    else
                        frameVals, body

                let lambda, args =
                    if frameVals.IsEmpty then
                        let (ValReprInfo(_, args, _)) = v.ValReprInfo.Value
                        let lambda = mkLambda m unitArg (body, bodyTy)
                        mkTypeLambda m frameTypars (lambda, tyOfExpr g lambda), args
                    else
                        mkMultiLambdas g m frameTypars (frameVals |> List.map List.singleton) (body, bodyTy),
                        frameVals |> List.map (fun _ -> [ ValReprInfo.unnamedTopArg1 ])

                v.SetValReprInfo(Some(ValReprInfo(ValReprInfo.InferTyparInfo frameTypars, args, ValReprInfo.unnamedRetVal)))
                v.SetType(tyOfExpr g lambda)
                checkedBodies[v.LogicalName] <- (body0, bodyTy)
                TBind(v, lambda, sp)
            | _ -> bind

    let rec rewrite contents =
        match contents with
        | TMDefRec(isRec, opens, tycons, bindings, m) ->
            let bindings =
                bindings
                |> List.map (function
                    | ModuleOrNamespaceBinding.Binding bind -> ModuleOrNamespaceBinding.Binding(rewriteBinding bind)
                    | ModuleOrNamespaceBinding.Module(mspec, def) -> ModuleOrNamespaceBinding.Module(mspec, rewrite def))

            TMDefRec(isRec, opens, tycons, bindings, m)
        | TMDefLet(bind, m) -> TMDefLet(rewriteBinding bind, m)
        | TMDefs defs -> TMDefs(defs |> List.map rewrite)
        | TMDefDo _
        | TMDefOpens _ -> contents

    let contents = rewrite contents
    contents, checkedBodies

/// FSharp.Core functions known to have no side effects, by module and name.
let private pureFSharpCoreFunctions =
    set
        [
            "Operators.fst"
            "Operators.snd"
            "Operators.id"
            "List.length"
            "List.isEmpty"
            "List.head"
            "List.tail"
            "Array.length"
            "Option.isSome"
            "Option.isNone"
            "Option.get"
        ]

/// Whether an expression may have side effects: assignments, method and constructor calls and
/// function applications may; field reads, property getters, inline functions (operators) and
/// some FSharp.Core functions don't.
let private mayHaveSideEffects (g: TcGlobals.TcGlobals) (expr: Expr) =
    let mutable sideEffects = false

    let isPure (vref: ValRef) =
        vref.ShouldInline
        || vref.IsPropertyGetterMethod
        || (match ccuOfValRef vref with
            | Some ccu when ccuEq ccu g.fslibCcu && vref.HasDeclaringEntity ->
                pureFSharpCoreFunctions.Contains(vref.DeclaringEntity.DisplayName + "." + vref.LogicalName)
            | _ -> false)

    let hasSideEffects instr =
        match instr with
        | I_call(_, mspec, _) when isFieldLoadPlaceholder mspec -> false
        | I_stfld _
        | I_stsfld _
        | I_stelem _
        | I_stelem_any _
        | I_stind _
        | I_call _
        | I_callvirt _
        | I_callconstraint _
        | I_newobj _ -> true
        | _ -> false

    let intercept _ (e: Expr) =
        match e with
        | Expr.App(f, _, _, args, _) ->
            match stripDebugPoints f with
            | Expr.Val(vref, _, _) when isPure vref -> ()
            | Expr.Val _ when args.IsEmpty -> ()
            | _ -> sideEffects <- true
        | Expr.Op(op, _, _, _) ->
            match op with
            | TOp.LValueOp((LSet | LByrefSet), _)
            | TOp.ValFieldSet _
            | TOp.UnionCaseFieldSet _
            | TOp.ExnFieldSet _ -> sideEffects <- true
            | TOp.ILCall(isCtor = isCtor; isProperty = isProperty; ilMethRef = mref) when
                isCtor || not (isProperty && mref.Name.StartsWith "get_")
                ->
                sideEffects <- true
            | TOp.ILAsm(instrs, _) when List.exists hasSideEffects instrs -> sideEffects <- true
            | _ -> ()
        | _ -> ()

        None

    RewriteExpr
        {
            PreIntercept = Some intercept
            PostTransform = (fun _ -> None)
            PreInterceptBinding = None
            RewriteQuotations = false
            StackGuard = StackGuard("ExpressionEvaluation")
        }
        expr
    |> ignore

    sideEffects

/// Turns `Seq.toList` and `Seq.toArray` into `List.ofSeq` and `Array.ofSeq`, which IlxGen leaves as
/// calls. It lowers the former, for list and array expressions (`[ 1 .. 3 ]`, `[| for … |]`), to
/// loops filling a ListCollector/ArrayCollector struct local, but the debugger runs queries with an
/// IL interpreter that passes a struct local to a call by copy, so the result would come out empty.
let private avoidCollectorLowering (g: TcGlobals.TcGlobals) (implFile: CheckedImplFile) =
    // The replacements have the same signatures, `seq<'T> -> 'T list` and `seq<'T> -> 'T[]`, so
    // they link with the same key, under another FSharp.Core module and name.
    let sibling (vref: ValRef) moduleName name =
        let nlr = vref.nlr
        let enclosing = nlr.EnclosingEntity.nlr
        let path = Array.append (Array.truncate (enclosing.Path.Length - 1) enclosing.Path) [| moduleName |]
        let key = ValLinkageFullKey({ nlr.ItemKey.PartialKey with LogicalName = name }, nlr.ItemKey.TypeForLinkage)
        mkNonLocalValRef (mkNonLocalEntityRef enclosing.Ccu path) key

    let listOfSeq = sibling g.seq_to_list_vref "ListModule" "ofSeq"
    let arrayOfSeq = sibling g.seq_to_array_vref "ArrayModule" "ofSeq"

    let replace (e: Expr) =
        match e with
        | Expr.App(Expr.Val(vref, flags, mVal), fty, tyargs, args, m) when valRefEq g vref g.seq_to_list_vref ->
            Some(Expr.App(Expr.Val(listOfSeq, flags, mVal), fty, tyargs, args, m))
        | Expr.App(Expr.Val(vref, flags, mVal), fty, tyargs, args, m) when valRefEq g vref g.seq_to_array_vref ->
            Some(Expr.App(Expr.Val(arrayOfSeq, flags, mVal), fty, tyargs, args, m))
        | _ -> None

    RewriteImplFile
        {
            PreIntercept = None
            PostTransform = replace
            PreInterceptBinding = None
            RewriteQuotations = false
            StackGuard = StackGuard("ExpressionEvaluation")
        }
        implFile

/// What the query module is compiled for.
[<RequireQualifiedAccess>]
type private QueryRequest =
    /// One expression (or assignment) as the user wrote it.
    | Expression of text: string
    /// One query per variable the Locals window shows (only the parameters with argumentsOnly).
    | Locals of argumentsOnly: bool

/// Compiler temporaries the Locals window doesn't show (docs/DESIGN.md §6.4); expressions can
/// still name them.
let private isHiddenLocalName (name: string) =
    name.Contains "@"
    || name.StartsWith "_arg"
    || name.StartsWith "activePatternResult"
    || name.StartsWith "unitVar"
    || List.contains
        name
        [
            "matchValue"
            "copyOfStruct"
            "tupledArg"
            "patternInput"
            "inputSequence"
            "enumerator"
            "resultOfApply"
            "objectArg"
        ]

/// A query of the module: its method and the properties of its result.
type private CompiledQuery =
    {
        /// The variable, for a Locals query
        Name: string
        MethodName: string
        ResultType: string
        PotentialSideEffect: bool
        ReadOnlyResult: bool
        BoolResult: bool
    }

/// A PE image holding only a metadata blob and embedded managed resources (at their original
/// offsets in the resources directory): enough for the IL reader to import a reference.
type private MetadataOnlyPEBuilder(metadata: byte[], resources: byte[]) =
    inherit PEBuilder(PEHeaderBuilder.CreateLibraryHeader(), null)

    let directories = PEDirectoriesBuilder()

    override _.CreateSections() =
        ImmutableArray.Create(
            PEBuilder.Section(
                ".text",
                SectionCharacteristics.MemRead
                ||| SectionCharacteristics.ContainsInitializedData
            )
        )

    override _.SerializeSection(_name, location) =
        let corHeaderSize = 72
        let metadataRva = location.RelativeVirtualAddress + corHeaderSize
        let resourcesRva = metadataRva + ((metadata.Length + 3) &&& ~~~3)
        let builder = BlobBuilder()

        // CLI header (ECMA-335 II.25.3.3)
        builder.WriteInt32 corHeaderSize
        builder.WriteUInt16 2us
        builder.WriteUInt16 5us
        builder.WriteInt32 metadataRva
        builder.WriteInt32 metadata.Length
        builder.WriteInt32 1 // COMIMAGE_FLAGS_ILONLY
        builder.WriteInt32 0 // entry point
        builder.WriteInt32(if resources.Length = 0 then 0 else resourcesRva)
        builder.WriteInt32 resources.Length

        // strong name signature, code manager, vtable fixups, export address table jumps, managed native header
        for _ in 1..5 do
            builder.WriteInt64 0L

        builder.WriteBytes metadata
        builder.Align 4
        builder.WriteBytes resources
        directories.CorHeaderTable <- DirectoryEntry(location.RelativeVirtualAddress, corHeaderSize)
        builder

    override _.GetDirectories() = directories

let private metadataOnlyImage (metadata: byte[]) (resources: (int * byte[]) list) =
    // Each resource is a length-prefixed blob at its offset in the resources directory. The IL
    // reader reads every embedded resource listed in the metadata, so resources that were not
    // provided get an empty blob at their offset.
    let allOffsets =
        use provider =
            MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create<byte>(metadata))

        let md = provider.GetMetadataReader()

        [
            for h in md.ManifestResources do
                let r = md.GetManifestResource h

                if r.Implementation.IsNil then
                    int r.Offset
        ]

    let size =
        let provided =
            resources
            |> List.fold (fun size (offset, data: byte[]) -> max size (offset + 4 + data.Length)) 0

        allOffsets |> List.fold (fun size offset -> max size (offset + 4)) provided

    let directory = Array.zeroCreate<byte> size

    for offset, data in resources do
        BitConverter.GetBytes(data.Length).CopyTo(directory, offset)
        data.CopyTo(directory, offset + 4)

    let blob = BlobBuilder()
    MetadataOnlyPEBuilder(metadata, directory).Serialize(blob) |> ignore
    blob.ToArray()

/// A type in a local signature, with whether the local is pinned.
[<Struct>]
type private SignatureType = { Type: ILType; Pinned: bool }

/// Decodes a method's local signature (StandAloneSig) from the module's metadata, giving the same
/// AbstractIL locals the IL reader would produce from the method body.
let private decodeLocalSignature
    (g: TcGlobals.TcGlobals)
    (metadata: byte[])
    (scopeRef: ILScopeRef)
    (assemblyRefs: ILAssemblyRef list)
    (classTypars: int)
    (token: int)
    : ILLocals =
    use provider =
        MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create<byte>(metadata))

    let md = provider.GetMetadataReader()
    let plain t = { Type = t; Pinned = false }

    let fullName (ns: StringHandle) (name: StringHandle) =
        match md.GetString ns with
        | "" -> md.GetString name
        | ns -> ns + "." + md.GetString name

    let rec typeDefRef (h: TypeDefinitionHandle) =
        let td = md.GetTypeDefinition h
        let declaring = td.GetDeclaringType()

        if declaring.IsNil then
            ILTypeRef.Create(scopeRef, [], fullName td.Namespace td.Name)
        else
            let outer = typeDefRef declaring
            ILTypeRef.Create(scopeRef, outer.Enclosing @ [ outer.Name ], md.GetString td.Name)

    let rec typeRefRef (h: TypeReferenceHandle) =
        let tr = md.GetTypeReference h
        let scope = tr.ResolutionScope

        match scope.Kind with
        | HandleKind.TypeReference ->
            let outer = typeRefRef (TypeReferenceHandle.op_Explicit scope)
            ILTypeRef.Create(outer.Scope, outer.Enclosing @ [ outer.Name ], md.GetString tr.Name)
        | HandleKind.AssemblyReference ->
            let row =
                MetadataTokens.GetRowNumber(AssemblyReferenceHandle.op_Explicit scope: AssemblyReferenceHandle)

            ILTypeRef.Create(ILScopeRef.Assembly assemblyRefs[row - 1], [], fullName tr.Namespace tr.Name)
        | HandleKind.ModuleReference ->
            let moduleRef = md.GetModuleReference(ModuleReferenceHandle.op_Explicit scope)

            ILTypeRef.Create(
                ILScopeRef.Module(ILModuleRef.Create(md.GetString moduleRef.Name, true, None)),
                [],
                fullName tr.Namespace tr.Name
            )
        | _ -> ILTypeRef.Create(scopeRef, [], fullName tr.Namespace tr.Name)

    let named (tref: ILTypeRef) (rawKind: byte) =
        let spec = ILTypeSpec.Create(tref, [])
        // SignatureTypeKind.ValueType
        plain (
            if rawKind = 0x11uy then
                ILType.Value spec
            else
                ILType.Boxed spec
        )

    let types =
        { new ISignatureTypeProvider<SignatureType, unit> with
            member _.GetPrimitiveType code =
                plain (
                    match code with
                    | PrimitiveTypeCode.Void -> ILType.Void
                    | PrimitiveTypeCode.Boolean -> g.ilg.typ_Bool
                    | PrimitiveTypeCode.Char -> g.ilg.typ_Char
                    | PrimitiveTypeCode.SByte -> g.ilg.typ_SByte
                    | PrimitiveTypeCode.Byte -> g.ilg.typ_Byte
                    | PrimitiveTypeCode.Int16 -> g.ilg.typ_Int16
                    | PrimitiveTypeCode.UInt16 -> g.ilg.typ_UInt16
                    | PrimitiveTypeCode.Int32 -> g.ilg.typ_Int32
                    | PrimitiveTypeCode.UInt32 -> g.ilg.typ_UInt32
                    | PrimitiveTypeCode.Int64 -> g.ilg.typ_Int64
                    | PrimitiveTypeCode.UInt64 -> g.ilg.typ_UInt64
                    | PrimitiveTypeCode.Single -> g.ilg.typ_Single
                    | PrimitiveTypeCode.Double -> g.ilg.typ_Double
                    | PrimitiveTypeCode.String -> g.ilg.typ_String
                    | PrimitiveTypeCode.IntPtr -> g.ilg.typ_IntPtr
                    | PrimitiveTypeCode.UIntPtr -> g.ilg.typ_UIntPtr
                    | PrimitiveTypeCode.TypedReference -> g.ilg.typ_TypedReference
                    | _ -> g.ilg.typ_Object
                )

            member _.GetTypeFromDefinition(_, h, rawKind) = named (typeDefRef h) rawKind
            member _.GetTypeFromReference(_, h, rawKind) = named (typeRefRef h) rawKind

            member this.GetTypeFromSpecification(_, context, h, _) =
                (md.GetTypeSpecification h).DecodeSignature(this, context)

            member _.GetGenericInstantiation(generic, args) =
                let args = [ for a in args -> a.Type ]

                plain (
                    match generic.Type with
                    | ILType.Value spec -> ILType.Value(ILTypeSpec.Create(spec.TypeRef, args))
                    | ILType.Boxed spec -> ILType.Boxed(ILTypeSpec.Create(spec.TypeRef, args))
                    | t -> t
                )

            member _.GetSZArrayType element =
                plain (mkILArrTy (element.Type, ILArrayShape.SingleDimensional))

            member _.GetArrayType(element, shape) =
                plain (mkILArrTy (element.Type, ILArrayShape.FromRank shape.Rank))

            member _.GetByReferenceType element = plain (ILType.Byref element.Type)
            member _.GetPointerType element = plain (ILType.Ptr element.Type)
            member _.GetGenericTypeParameter(_, index) = plain (ILType.TypeVar(uint16 index))

            member _.GetGenericMethodParameter(_, index) =
                plain (ILType.TypeVar(uint16 (classTypars + index)))

            member _.GetFunctionPointerType _ = plain g.ilg.typ_IntPtr

            member _.GetModifiedType(modifier, unmodified, isRequired) =
                { unmodified with
                    Type = ILType.Modified(isRequired, modifier.Type.TypeRef, unmodified.Type)
                }

            member _.GetPinnedType element = { element with Pinned = true }
        }

    let signature =
        md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(token &&& 0x00FFFFFF))

    [
        for local in signature.DecodeLocalSignature(types, ()) ->
            {
                Type = local.Type
                IsPinned = local.Pinned
                DebugInfo = None
            }
    ]

/// IlxGen saw the frame's locals as the trailing parameters of `<>m0`. The debugger instead
/// needs them as the method's leading locals, in the frame's exact local signature: drop those
/// parameters, prepend the frame's locals, and retarget argument and local instructions.
let private parametersToFrameLocals (frameLocalSignature: ILLocals) (slots: int list) (md: ILMethodDef) =
    let ownParameters = md.Parameters.Length - slots.Length
    let shift = uint16 frameLocalSignature.Length
    let slotOf (i: uint16) = uint16 slots[int i - ownParameters]
    let isFrameLocal (i: uint16) = int i >= ownParameters
    let body = md.MethodBody

    let rewrite instr =
        match instr with
        | I_ldarg i when isFrameLocal i -> I_ldloc(slotOf i)
        | I_ldarga i when isFrameLocal i -> I_ldloca(slotOf i)
        | I_starg i when isFrameLocal i -> I_stloc(slotOf i)
        | I_ldloc i -> I_ldloc(i + shift)
        | I_ldloca i -> I_ldloca(i + shift)
        | I_stloc i -> I_stloc(i + shift)
        | instr -> instr

    let code =
        { body.Code with
            Instrs = body.Code.Instrs |> Array.map rewrite
            Locals =
                body.Code.Locals
                |> List.map (fun info ->
                    { info with
                        DebugMappings =
                            info.DebugMappings
                            |> List.map (fun mapping ->
                                { mapping with
                                    LocalIndex = mapping.LocalIndex + int shift
                                })
                    })
        }

    let body =
        { body with
            Locals =
                (frameLocalSignature |> List.map (fun l -> { l with DebugInfo = None }))
                @ body.Locals
            Code = code
        }

    md.With(parameters = List.take ownParameters md.Parameters, body = notlazy (MethodBody.IL(notlazy body)))

/// Replaces the calls to field placeholders in a generated type (and its nested types, e.g. a
/// lambda's closure) with the field loads and stores they stand for. Until then the fields of
/// compiler-generated classes stay out of the typed tree: those classes are not in their assembly's
/// F# signature, so the compiler could not import them to check the fields' accessibility. A call
/// takes the owner (and the value) and returns the value (or nothing), as the instruction does.
let rec private replaceFieldPlaceholders (fields: Collections.Generic.IReadOnlyDictionary<string, ILInstr>) (td: ILTypeDef) =
    let replace instr =
        match instr with
        | I_call(_, mspec, _) when mspec.MethodRef.DeclaringTypeRef.Name = QueryTypeName ->
            match fields.TryGetValue mspec.Name with
            | true, field -> field
            | _ -> instr
        | _ -> instr

    let methods =
        td.Methods.AsList()
        |> List.map (fun md ->
            match md.Body with
            | MethodBody.IL body ->
                let body = body.Value

                md.With(
                    body =
                        notlazy (
                            MethodBody.IL(
                                notlazy
                                    { body with
                                        Code =
                                            { body.Code with
                                                Instrs = Array.map replace body.Code.Instrs
                                            }
                                    }
                            )
                        )
                )
            | _ -> md)

    td.With(
        methods = mkILMethods methods,
        nestedTypes = mkILTypeDefs (td.NestedTypes.AsList() |> List.map (replaceFieldPlaceholders fields))
    )

/// `IgnoresAccessChecksToAttribute` (defined in the query assembly, as the runtime expects) applied
/// for the debuggee's assemblies. The debugger's IL interpreter ignores accessibility; this lets
/// CoreCLR do the same when the query assembly runs as ordinary code (func-eval of lambdas, tests).
let private ignoresAccessChecksTo (g: TcGlobals.TcGlobals) (assemblyNames: string list) =
    let name = "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute"
    let typeRef = mkILTyRef (ILScopeRef.Local, name)
    let ty = mkILNonGenericBoxedTy typeRef

    let ctor =
        mkILSimpleStorageCtor (
            Some g.ilg.typ_Attribute.TypeSpec,
            ty,
            [ mkILParamNamed ("assemblyName", g.ilg.typ_String) ],
            [],
            ILMemberAccess.Public,
            None,
            None
        )

    let typeDef =
        (mkILSimpleClass
            g.ilg
            (name,
             ILTypeDefAccess.Public,
             mkILMethods [ ctor ],
             emptyILFields,
             emptyILTypeDefs,
             emptyILProperties,
             emptyILEvents,
             emptyILCustomAttrs,
             ILTypeInit.BeforeField))
            .With(extends = notlazy (Some g.ilg.typ_Attribute))

    let attributes =
        assemblyNames
        |> List.map (fun n -> mkILCustomAttribute (typeRef, [ g.ilg.typ_String ], [ ILAttribElem.String(Some n) ], []))

    typeDef, attributes

/// The token of a method's local signature, read from its body in a full image (0 when it has none).
let private localSignatureTokenOf (image: byte[]) (methodToken: int) =
    use pe = new PEReader(ImmutableArray.Create<byte>(image))
    let md = pe.GetMetadataReader()
    let methodDef = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(methodToken &&& 0xFFFFFF))

    if methodDef.RelativeVirtualAddress = 0 then
        0
    else
        let body = pe.GetMethodBody methodDef.RelativeVirtualAddress

        if body.LocalSignature.IsNil then
            0
        else
            MetadataTokens.GetToken(StandaloneSignatureHandle.op_Implicit body.LocalSignature: EntityHandle)

/// The metadata blob of a module image.
let private metadataOf (image: DebuggeeImage) =
    match image with
    | DebuggeeImage.Metadata(metadata, _) -> metadata
    | DebuggeeImage.PE image ->
        use pe = new PEReader(ImmutableArray.Create<byte>(image))
        let content = pe.GetMetadata().GetContent()
        Array.init content.Length (fun i -> content[i])

/// The debuggee's modules as files for the compiler, served from memory. The compiler reads every
/// assembly through its FileSystem, which this compiler copy has to itself. Each session's modules
/// are files under a directory of their own, as sessions may see different contents at one path;
/// the directory doesn't exist, and other paths go to the disk.
[<Sealed>]
type private DebuggeeFileSystem() =
    inherit FSharp.Compiler.IO.DefaultFileSystem()

    static let instance =
        lazy
            (let fs = DebuggeeFileSystem()
             FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- fs
             fs)

    // FCS reads a file once more when it is no longer cached: the time keeps cached copies valid.
    static let writeTime = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    let files = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<byte[]>>(StringComparer.OrdinalIgnoreCase)
    let mutable sessions = 0

    /// The file system, installed as the compiler's on first use.
    static member Instance = instance.Value

    /// Adds a session's modules, under a new directory, and returns their paths in the same order.
    member _.Add(modules: (string * Lazy<byte[]>) list) =
        let n = Threading.Interlocked.Increment &sessions
        let root = Path.Combine(Path.GetTempPath(), "FSharpExpressionEvaluator", $"session{n}")

        modules
        |> List.map (fun (path: string, contents) ->
            // The debuggee's path, made relative: `C:\app\a.dll` is `<root>\C\app\a.dll`.
            let file = Path.Combine(root, path.Replace(":", "").TrimStart('\\', '/'))
            files[file] <- contents
            file)

    member _.Remove(paths: string list) =
        for path in paths do
            files.TryRemove path |> ignore

    override _.FileExistsShim path =
        files.ContainsKey path || base.FileExistsShim path

    override _.OpenFileForReadShim(path, ?useMemoryMappedFile, ?shouldShadowCopy) =
        match files.TryGetValue path with
        | true, contents -> new MemoryStream(contents.Value, false) :> Stream
        | _ -> base.OpenFileForReadShim(path, ?useMemoryMappedFile = useMemoryMappedFile, ?shouldShadowCopy = shouldShadowCopy)

    override _.GetLastWriteTimeShim path =
        if files.ContainsKey path then writeTime else base.GetLastWriteTimeShim path

[<Sealed>]
type EvaluationSession
    (
        tcConfig: TcConfig,
        tcGlobals: TcGlobals.TcGlobals,
        tcImports: TcImports,
        modules: Map<string, DebuggeeImage>,
        files: string list
    ) =

    let ctok = AssumeCompilationThreadWithoutEvidence()
    let lexResourceManager = LexResourceManager()
    let amap = tcImports.GetImportMap()

    interface IDisposable with
        member _.Dispose() =
            DebuggeeFileSystem.Instance.Remove files

    static member Create(modules: DebuggeeModule list) =
        let files =
            DebuggeeFileSystem.Instance.Add
                [
                    for m in modules ->
                        m.FileName,
                        lazy
                            (match m.Image with
                             | DebuggeeImage.PE image -> image
                             | DebuggeeImage.Metadata(metadata, resources) -> metadataOnlyImage metadata resources)
                ]

        let fsharpCore =
            files
            |> List.tryFind (fun file -> String.Equals(Path.GetFileName file, "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase))

        let binariesDir =
            match fsharpCore with
            | Some file ->
                Path.GetDirectoryName file
                |> Option.ofObj
                |> Option.defaultValue AppContext.BaseDirectory
            | None -> AppContext.BaseDirectory

        let tcConfigB =
            TcConfigBuilder.CreateNew(
                SimulatedMSBuildReferenceResolver.getResolver (),
                defaultFSharpBinariesDir = binariesDir,
                reduceMemoryUsage = ReduceMemoryFlag.Yes,
                implicitIncludeDir = Directory.GetCurrentDirectory(),
                isInteractive = false,
                isInvalidationSupported = false,
                defaultCopyFSharpCore = CopyFSharpCoreFlag.No,
                tryGetMetadataSnapshot = (fun _ -> None),
                sdkDirOverride = None,
                rangeForErrors = range0
            )

        // Compile against exactly what the debuggee has loaded: implementation assemblies, no SDK packs.
        tcConfigB.implicitlyReferenceDotNetAssemblies <- false
        tcConfigB.implicitlyResolveAssemblies <- false
        tcConfigB.SetUseSdkRefs false
        tcConfigB.primaryAssembly <- PrimaryAssembly.System_Runtime
        tcConfigB.target <- CompilerTarget.Dll
        tcConfigB.debuginfo <- false
        tcConfigB.emitTailcalls <- false
        // A query is one small module: nothing to gain from generating its code in parallel.
        tcConfigB.parallelIlxGen <- false

        tcConfigB.optSettings <-
            { tcConfigB.optSettings with
                jitOptUser = Some false
                localOptUser = Some false
            }

        for file in files do
            tcConfigB.AddReferencedAssemblyByPath(range0, file)

        let tcConfig = TcConfig.Create(tcConfigB, validate = false)
        let tcConfigP = TcConfigProvider.Constant tcConfig

        let tcGlobals, tcImports =
            try
                TcImports.BuildTcImports(tcConfigP, new DependencyProvider())
                |> Async.RunSynchronouslyImmediate
            with _ ->
                DebuggeeFileSystem.Instance.Remove files
                reraise ()

        new EvaluationSession(
            tcConfig,
            tcGlobals,
            tcImports,
            List.zip files modules |> List.map (fun (file, m) -> file, m.Image) |> Map.ofList,
            files
        )

    member this.CompileExpression(frame: FrameContext, text: string, ?localsAsParameters: bool) =
        let text, formatSpecifiers = splitFormatSpecifiers text

        this.Compile(frame, QueryRequest.Expression text, defaultArg localsAsParameters false, false)
        |> Result.map (fun (assembly, queries) ->
            let query = List.exactlyOne queries

            {
                Assembly = assembly
                TypeName = QueryTypeName
                MethodName = query.MethodName
                ResultType = query.ResultType
                FormatSpecifiers = formatSpecifiers
                PotentialSideEffect = query.PotentialSideEffect
                ReadOnlyResult = query.ReadOnlyResult
                BoolResult = query.BoolResult
            })

    member this.CompileAssignment(frame: FrameContext, target: string, value: string, ?localsAsParameters: bool) =
        let text = $"{target} <- (\n{value}\n)"

        this.Compile(frame, QueryRequest.Expression text, defaultArg localsAsParameters false, true)
        |> Result.map (fun (assembly, queries) ->
            let query = List.exactlyOne queries

            {
                Assembly = assembly
                TypeName = QueryTypeName
                MethodName = query.MethodName
                ResultType = query.ResultType
                FormatSpecifiers = []
                PotentialSideEffect = query.PotentialSideEffect
                ReadOnlyResult = query.ReadOnlyResult
                BoolResult = query.BoolResult
            })

    member this.CompileLocals(frame: FrameContext, argumentsOnly: bool, ?localsAsParameters: bool) =
        this.Compile(frame, QueryRequest.Locals argumentsOnly, defaultArg localsAsParameters false, false)
        |> Result.map (fun (assembly, queries) ->
            {
                Assembly = assembly
                TypeName = QueryTypeName
                Locals =
                    queries
                    |> List.map (fun q ->
                        {
                            Name = q.Name
                            MethodName = q.MethodName
                            ResultType = q.ResultType
                            ReadOnlyResult = q.ReadOnlyResult
                        })
            })

    member this.WarmUp(assemblyName: string, methodToken: int) =
        // The frame as far as it is known without the debugger: no locals, opens or aliases.
        let frame =
            {
                AssemblyName = assemblyName
                MethodToken = methodToken
                LocalSignatureToken = 0
                Opens = []
                Locals = []
                Aliases = []
                IntrinsicsAssembly = ""
            }

        try
            // A closure and FSharp.Core functions, so that optimization and code generation run
            // through more of the compiler.
            this.CompileExpression(frame, "[ 0 ] |> List.map (fun i -> i + 1) |> List.sum") |> ignore
            this.CompileLocals(frame, false) |> ignore
        with _ ->
            ()

    member private _.Compile(frame: FrameContext, request: QueryRequest, localsAsParameters: bool, assigning: bool) =
        let g = tcGlobals
        let m = range0

        let text =
            match request with
            | QueryRequest.Expression text -> text.Replace("\r\n", "\n")
            | QueryRequest.Locals _ -> ""

        let quoted, origins = quoteDollarNames text

        // Ranges of diagnostics are given in the user's text, as far as they fall in it.
        let inExpression (diagnostics: EvaluationDiagnostic list) =
            match request with
            | QueryRequest.Expression _ ->
                diagnostics
                |> List.map (fun d ->
                    { d with
                        Range = d.Range |> Option.bind (rangeInExpression text quoted origins)
                    })
            | QueryRequest.Locals _ -> diagnostics |> List.map (fun d -> { d with Range = None })

        result {
            // The frame's method and the F# entities enclosing it.
            let! dllInfo, ccu, ilModule, enclosing, methodDef =
                withDiagnostics BuildPhase.TypeCheck (fun () ->
                    let dllInfo = tcImports.FindDllInfo(ctok, m, frame.AssemblyName)

                    let ccu =
                        tcImports.GetImportedAssemblies()
                        |> List.find (fun a -> a.ILScopeRef = dllInfo.ILScopeRef)
                        |> _.FSharpViewOfMetadata

                    let ilModule =
                        match dllInfo.RawMetadata.TryGetILModuleDef() with
                        | Some ilModule -> ilModule
                        | None -> failwith $"No metadata for '{frame.AssemblyName}'"

                    match tryFindMethod ilModule frame.MethodToken with
                    | Some(enclosing, md) -> dllInfo, ccu, ilModule, enclosing, md
                    | None -> failwith $"Method 0x{frame.MethodToken:X8} not found in '{frame.AssemblyName}'")

            let entities = fsharpEntities ccu enclosing

            // The modules whose values the frame sees unqualified, and whose private ones it can access.
            let scopeEntities = entities @ startupCodeModules ccu enclosing

            // Opens: enclosing namespace and modules (their values are in scope unqualified), then the PDB's.
            let opens =
                [
                    for e in scopeEntities do
                        if
                            e.IsModuleOrNamespace
                            && not (EntityHasWellKnownAttribute g WellKnownEntityAttributes.RequireQualifiedAccessAttribute e)
                        then
                            sourcePath e
                    for o in frame.Opens do
                        List.ofArray (o.Split '.')
                ]
                |> List.distinct

            // `this` and the parameters, as locals the expression can name.
            let declaringType = enclosing |> List.last

            let declaringTypeRef =
                mkRefForNestedILTypeDef dllInfo.ILScopeRef (enclosing |> List.take (enclosing.Length - 1), declaringType)

            // The frame's type parameters, class ones first (as IL numbers them), become rigid
            // typars the expression can name and that `<>m0` is generic over. When the frame's
            // method is an F# value (a module function or a member), they are copies of its
            // typars, so they keep the F# constraints IL does not record (comparison, equality,
            // member constraints); a member's typars are its class's and then its own.
            let typarCount = declaringType.GenericParams.Length + methodDef.GenericParams.Length

            let frameVal =
                match List.tryLast entities with
                | Some entity when typarCount > 0 && entities.Length = enclosing.Length ->
                    // A module's values are in its own table; a type's members on the type.
                    let candidates =
                        if entity.IsModuleOrNamespace then
                            Seq.toList entity.ModuleOrNamespaceType.AllValsAndMembers
                        else
                            entity.MembersOfFSharpTyconSorted |> List.map _.Deref

                    candidates
                    |> List.tryFind (fun v ->
                        v.Typars.Length = typarCount
                        && v.CompiledName g.CompilerGlobalState = methodDef.Name)
                | _ -> None

            let frameTypars =
                match frameVal with
                | Some v ->
                    let copies = copyTypars false v.Typars
                    let inst = mkTyparInst v.Typars (copies |> List.map mkTyparTy)

                    for copy, original in List.zip copies v.Typars do
                        copy.SetConstraints(instTyparConstraints inst original.Constraints)

                    copies
                | None ->
                    (declaringType.GenericParams @ methodDef.GenericParams)
                    |> List.map (fun p -> Construct.NewRigidTypar p.Name m)

            let classTypars = List.take declaringType.GenericParams.Length frameTypars
            let frameTinst = frameTypars |> List.map mkTyparTy

            // Anonymous record types are classes the compiler generates in each assembly that uses
            // them (`<>f__AnonymousType<hash>`1`, generic over the field types in field-name order),
            // not entities of its signature, so `ImportILType` can't import them. They become
            // `{| … |}` types again: the field names are the properties typed by each type
            // parameter, and the assembly is the CCU defining the class. IlxGen derives the class's
            // name from the CCU's name and the field names, which must give back this class, so
            // that queries name the frame's class (IlxGen only generates one for the query's own CCU).
            let tryAnonRecdInfo (tref: ILTypeRef) =
                // The frame's own module names its types with a local scope.
                let tref = rescopeILTypeRef dllInfo.ILScopeRef tref

                if not (tref.Name.StartsWith "<>f__AnonymousType") || not tref.Enclosing.IsEmpty then
                    None
                else
                    let typeDefsOf (binary: ImportedBinary) =
                        binary.RawMetadata.TryGetILModuleDef() |> Option.map _.TypeDefs

                    let typeDefs =
                        if tref.Scope = dllInfo.ILScopeRef then
                            Some ilModule.TypeDefs
                        else
                            match tref.Scope with
                            | ILScopeRef.Assembly aref ->
                                tcImports.TryFindDllInfo(ctok, m, aref.Name, lookupOnly = true)
                                |> Option.bind typeDefsOf
                            | _ -> None

                    let ccu =
                        tcImports.GetImportedAssemblies()
                        |> List.tryFind (fun a -> a.ILScopeRef = tref.Scope)
                        |> Option.map _.FSharpViewOfMetadata

                    match typeDefs, ccu with
                    | Some typeDefs, Some ccu ->
                        match (if typeDefs.ExistsByName tref.Name then Some(typeDefs.FindByName tref.Name) else None) with
                        | Some td ->
                            let names =
                                td.GenericParams
                                |> List.mapi (fun i _ ->
                                    td.Properties.AsList()
                                    |> List.tryFind (fun p -> p.PropertyType = ILType.TypeVar(uint16 i))
                                    |> Option.map _.Name)

                            if names |> List.forall Option.isSome then
                                let ids = names |> List.map (fun n -> Ident(n.Value, m)) |> Array.ofList
                                let info = AnonRecdTypeInfo.Create(ccu, TupInfo.Const td.IsStruct, ids)

                                if info.ILTypeRef.Name = tref.Name && info.ILTypeRef.Scope = tref.Scope then
                                    Some info
                                else
                                    None
                            else
                                None
                        | None -> None
                    | _ -> None

            let rec containsAnonRecd (ty: ILType) =
                match ty with
                | ILType.Boxed tspec
                | ILType.Value tspec ->
                    (tryAnonRecdInfo tspec.TypeRef).IsSome
                    || tspec.GenericArgs |> List.exists containsAnonRecd
                | ILType.Array(_, ty)
                | ILType.Byref ty
                | ILType.Modified(_, _, ty) -> containsAnonRecd ty
                | _ -> false

            // `ImportILType`, but for types with anonymous records in them.
            let rec importType ty =
                if not (containsAnonRecd ty) then
                    Import.ImportILType amap m frameTinst ty
                else
                    match ty with
                    | ILType.Boxed tspec
                    | ILType.Value tspec ->
                        let args = tspec.GenericArgs |> List.map importType

                        match tryAnonRecdInfo tspec.TypeRef with
                        | Some info -> TType_anon(info, args)
                        | None ->
                            let tcref = Import.ImportILTypeRef amap m tspec.TypeRef
                            g.improveType tcref args (Nullness.Known NullnessInfo.AmbivalentToNull)
                    | ILType.Array(bounds, ty) ->
                        mkArrayTy g bounds.Rank (Nullness.Known NullnessInfo.AmbivalentToNull) (importType ty) m
                    | ILType.Byref ty -> mkByrefTy g (importType ty)
                    | ILType.Modified(_, _, ty) -> importType ty
                    | _ -> Import.ImportILType amap m frameTinst ty

            // Some types of frame values have no F# view (an anonymous record class whose name doesn't
            // match its fields, for example).
            let tryImportType ty =
                let logger = CapturingDiagnosticsLogger("ExpressionEvaluation")
                use _ = UseDiagnosticsLogger logger

                try
                    let imported = importType ty
                    if logger.ErrorCount = 0 then Some imported else None
                with _ ->
                    None

            let isValueType = declaringType.IsStruct

            let thisILType =
                let args = classTypars |> List.mapi (fun i _ -> ILType.TypeVar(uint16 i))

                if isValueType then
                    mkILValueTy declaringTypeRef args
                else
                    mkILBoxedTy declaringTypeRef args

            let isGenerated (t: ILType) =
                t.IsNominal
                && (t.TypeRef.Name.Contains "@"
                    || t.TypeRef.Enclosing |> List.exists (fun n -> n.Contains "@"))

            // Compiler-generated types (closures, `seq` and `task` state machines) are not part of
            // the assembly's F# signature, and the user didn't write them: their `this` is hidden,
            // and the original `this`, if any, is their hoisted `this` field. A generated class's
            // `this` is typed as the nearest base type F# can see (e.g. FSharpFunc<_,_>); a
            // resumable state machine's (a struct) as `nativeint` for checking, then given the
            // struct's byref type in IL. Field loads still name the generated type: it exists in IL.
            let isGeneratedFrame = entities.Length <> enclosing.Length
            let isStateMachine = isGeneratedFrame && isValueType

            let thisVal =
                if methodDef.IsStatic then
                    []
                elif isStateMachine then
                    [ fst (mkLocal m "<>this" g.nativeint_ty) ]
                elif isGeneratedFrame then
                    let baseType =
                        match declaringType.Extends.Value with
                        | Some baseType -> rescopeILType dllInfo.ILScopeRef baseType
                        | None -> g.ilg.typ_Object

                    [ fst (mkLocal m "<>this" (importType baseType)) ]
                elif isValueType then
                    [ fst (mkLocal m "this" (mkByrefTy g (importType thisILType))) ]
                else
                    [ fst (mkLocal m "this" (importType thisILType)) ]

            // Variables of the frame. When compiling an assignment they are mutable, so the checker
            // lets the expression assign them (and, as F# requires, no closure captures them).
            let mkVariable name ty =
                fst (mkLocalAux m name ty (if assigning then Mutable else Immutable) false)

            // A type definition of the frame's assembly.
            let tryFindTypeDef (tref: ILTypeRef) =
                if tref.Scope <> dllInfo.ILScopeRef then
                    None
                else
                    try
                        let outer = ilModule.TypeDefs.FindByName(List.head (tref.Enclosing @ [ tref.Name ]))

                        Some(
                            (outer, List.tail (tref.Enclosing @ [ tref.Name ]))
                            ||> List.fold (fun td name -> td.NestedTypes.FindByName name)
                        )
                    with _ ->
                        None

            // Loads and stores of fields of generated types, by the name of the placeholder method
            // standing for them (`replaceFieldPlaceholders`).
            let fieldPlaceholders = Collections.Generic.Dictionary<string, ILInstr>()

            let fieldAccess load (field: ILFieldSpec) (ownerType: ILType) =
                let instr = if load then mkNormalLdfld field else mkNormalStfld field

                if isGenerated ownerType then
                    let name = fieldPlaceholderName load fieldPlaceholders.Count
                    fieldPlaceholders[name] <- instr
                    let queryType = mkILNonGenericBoxedTy (mkILTyRef (ILScopeRef.Local, QueryTypeName))

                    let args, ret =
                        if load then [ ownerType ], field.FormalType else [ ownerType; field.FormalType ], ILType.Void

                    I_call(Normalcall, mkILNonGenericStaticMethSpecInTy (queryType, name, args, ret), None)
                else
                    instr

            // Instance fields that read as variables: those of `ownerType` (defined by `td`), read
            // from `owner`. Compiler-generated and backing fields (`X@`, `_tag`) are left out.
            let fieldsOf (td: ILTypeDef) (ownerType: ILType) (owner: range -> Expr) =
                td.Fields.AsList()
                |> List.filter (fun f ->
                    not f.IsStatic
                    && not (f.Name.Contains "@")
                    && not (f.Name.StartsWith "_")
                    && not (isGenerated (rescopeILType dllInfo.ILScopeRef f.FieldType)))
                |> List.choose (fun f ->
                    let formalType = rescopeILType dllInfo.ILScopeRef f.FieldType
                    let fieldType = instILType ownerType.GenericArgs formalType

                    tryImportType fieldType
                    |> Option.map (fun ty ->
                        let field = mkILFieldSpecInTy (ownerType, f.Name, formalType)
                        let load = fieldAccess true field ownerType
                        let store = fieldAccess false field ownerType

                        f.Name,
                        fieldType,
                        ty,
                        (fun m -> mkAsmExpr ([ load ], [], [ owner m ], [ ty ], m)),
                        (fun m value -> mkAsmExpr ([ store ], [], [ owner m; value ], [], m))))

            // Plumbing fields of state machines, not user variables: those of resumable code
            // (`task`), and of `seq` (a GeneratedSequenceBase<_> subclass).
            let isPlumbing =
                let isSeq =
                    match declaringType.Extends.Value with
                    | Some baseType when baseType.IsNominal ->
                        baseType.TypeRef.FullName = "Microsoft.FSharp.Core.CompilerServices.GeneratedSequenceBase`1"
                    | _ -> false

                fun name ->
                    (isStateMachine
                     && (name = "Data" || name = "ResumptionPoint" || name = "awaiter"))
                    || (isGeneratedFrame
                        && isSeq
                        && (name = "pc"
                            || name = "current"
                            || name = "enum"
                            || name = "redirect"
                            || name = "redirectTo"))

            // Fields of `this` as variables: a closure's captured values, a class's `let` fields, a
            // state machine's hoisted variables. A hoisted `this` also brings the fields of the
            // original `this`, shadowed by the frame's own.
            // Also the names of the frame's own fields, which a generated frame's Locals flatten.
            let fieldVariables, ownFieldNames =
                match thisVal with
                | [ thisVal ] ->
                    let own =
                        fieldsOf declaringType thisILType (fun m -> exprForVal m thisVal)
                        |> List.filter (fun (name, _, _, _, _) -> not (isPlumbing name))

                    let ofOriginalThis =
                        match own |> List.tryFind (fun (name, _, _, _, _) -> name = "this") with
                        | Some(_, (ILType.Boxed _ as originalThisType), _, read, _) ->
                            match tryFindTypeDef originalThisType.TypeRef with
                            | Some td -> fieldsOf td originalThisType read
                            | None -> []
                        | _ -> []

                    (ofOriginalThis @ own)
                    |> List.map (fun (name, _, ty, read, write) -> mkVariable name ty, read, write),
                    own |> List.map (fun (name, _, _, _, _) -> name)
                | _ -> [], []

            // The debugger's aliases, read through its intrinsic methods (as Roslyn binds them).
            let aliasVariables =
                let intrinsics =
                    lazy
                        (let assembly =
                            ILAssemblyRef.FromAssemblyName(Reflection.AssemblyName frame.IntrinsicsAssembly)

                         mkILNonGenericBoxedTy (
                             mkILTyRef (ILScopeRef.Assembly assembly, "Microsoft.VisualStudio.Debugger.Clr.IntrinsicMethods")
                         ))

                let call name argTys args (retILTy, retTy) m =
                    let spec =
                        mkILNonGenericStaticMethSpecInTy (intrinsics.Value, name, argTys, retILTy)

                    mkAsmExpr ([ mkNormalCall spec ], [], args, [ retTy ], m), retTy

                let exn = g.iltyp_Exception, g.exn_ty
                let obj = g.ilg.typ_Object, g.obj_ty_ambivalent

                let scopes = tcImports.GetImportedAssemblies() |> List.map _.ILScopeRef

                let scopeOfAssembly (name: string) =
                    try
                        tcImports.TryFindDllInfo(ctok, m, Reflection.AssemblyName(name).Name, lookupOnly = true)
                        |> Option.map _.ILScopeRef
                    with _ ->
                        None

                frame.Aliases
                |> List.map (fun alias ->
                    let ty = resolveClrType g amap scopes scopeOfAssembly alias.TypeName

                    let read m =
                        let value, valueTy =
                            match alias.Kind with
                            | AliasKind.Exception -> call "GetException" [] [] exn m
                            | AliasKind.StowedException -> call "GetStowedException" [] [] exn m
                            | AliasKind.ReturnValue ->
                                // `$ReturnValue` is the last value returned, `$ReturnValueN` the Nth.
                                let index =
                                    match Int32.TryParse(alias.Name.Substring(min alias.Name.Length "$ReturnValue".Length)) with
                                    | true, index -> index
                                    | _ -> 0

                                call "GetReturnValue" [ g.ilg.typ_Int32 ] [ mkInt32 g m index ] obj m
                            | AliasKind.ObjectId
                            | AliasKind.Variable -> call "GetObjectByAlias" [ g.ilg.typ_String ] [ mkString g m alias.Name ] obj m

                        if typeEquiv g valueTy ty then
                            value
                        else
                            mkCallUnbox g m ty (mkCoerceIfNeeded g g.obj_ty_ambivalent valueTy value)

                    fst (mkLocal m alias.Name ty), read)

            let parameterVals =
                methodDef.Parameters
                |> List.mapi (fun i p ->
                    // A parameter keeps its position among the query's arguments: `obj` when its type
                    // can't be imported.
                    let ty = tryImportType p.Type |> Option.defaultValue g.obj_ty_ambivalent
                    mkVariable (defaultArg p.Name $"arg{i}") ty)

            // The frame's locals, typed from its own local signature: decoded from the module's
            // metadata by token, or taken from the method body when the module is a full image.
            let frameLocalSignature =
                match modules.TryFind dllInfo.FileName with
                | Some image ->
                    let token =
                        match image with
                        | _ when frame.LocalSignatureToken <> 0 -> frame.LocalSignatureToken
                        | DebuggeeImage.PE image -> localSignatureTokenOf image frame.MethodToken
                        // Only a full image has method bodies; a metadata-only one has nothing to read
                        | DebuggeeImage.Metadata _ -> 0

                    if token = 0 then
                        []
                    else
                        decodeLocalSignature
                            g
                            (metadataOf image)
                            dllInfo.ILScopeRef
                            dllInfo.ILAssemblyRefs
                            declaringType.GenericParams.Length
                            token
                | None -> []

            // Locals whose type can't be imported are left out.
            let typedLocals =
                frame.Locals
                |> List.filter (fun l -> l.Slot < frameLocalSignature.Length)
                |> List.choose (fun l -> tryImportType frameLocalSignature[l.Slot].Type |> Option.map (fun ty -> l, ty))

            let localVals = typedLocals |> List.map (fun (l, ty) -> mkVariable l.Name ty)

            let localSlots = typedLocals |> List.map (fun (l, _) -> l.Slot)

            let frameVals = thisVal @ parameterVals @ localVals

            // Assignments to frame values store to the query's arguments, which the frame's values
            // are (IlxGen never stores to an argument itself); for locals in the frame's slots that
            // becomes a store to the slot.
            let frameValueWrites =
                frameVals
                |> List.mapi (fun i v -> v, (fun m value -> mkAsmExpr ([ I_starg(uint16 i) ], [], [ value ], [], m)))

            // Name scopes, outermost first: fields of `this` are shadowed by parameters, those by
            // locals, and those by the debugger's variables.
            let scopedVals =
                thisVal
                @ (fieldVariables |> List.map (fun (v, _, _) -> v))
                @ parameterVals
                @ localVals
                @ (aliasVariables |> List.map fst)

            // The query module's expressions, each with what it stands for: the user's expression,
            // or one per variable the Locals window shows. Locals with no variable to show still
            // compile one (unnamed, `()`), which is not returned.
            let queries =
                match request with
                | QueryRequest.Expression _ -> [ text, quoted ]
                | QueryRequest.Locals argumentsOnly ->
                    let localNames = localVals |> List.map _.LogicalName |> set

                    // Pieces of a tuple parameter (`pair_0`, `pair_1`) when the tuple is a local (`pair`)
                    let isTuplePiece (name: string) =
                        match name.LastIndexOf '_' with
                        | i when i > 0 && i + 1 < name.Length ->
                            localNames.Contains(name.Substring(0, i))
                            && Seq.forall Char.IsDigit (name.Substring(i + 1))
                        | _ -> false

                    let parameters =
                        parameterVals
                        |> List.map _.LogicalName
                        |> List.filter (fun name -> not (isHiddenLocalName name || isTuplePiece name))

                    let fieldNames =
                        fieldVariables |> List.map (fun (v: Val, _, _) -> v.LogicalName) |> set

                    let names =
                        if argumentsOnly then
                            parameters
                        else
                            [
                                yield! frame.Aliases |> List.map _.Name
                                // `this` as the user wrote it: a generated frame's own `this` is hidden,
                                // its hoisted `this` field stands for it.
                                if
                                    thisVal |> List.exists (fun v -> v.LogicalName = "this")
                                    || fieldNames.Contains "this"
                                then
                                    "this"
                                yield! parameters
                                // A closure's captured values and a state machine's hoisted variables,
                                // flattened; a class's own fields are shown under `this`.
                                if isGeneratedFrame then
                                    yield!
                                        ownFieldNames
                                        |> List.filter (fun name ->
                                            name <> "this" && fieldNames.Contains name && not (isHiddenLocalName name))
                                yield! localVals |> List.map _.LogicalName |> List.filter (not << isHiddenLocalName)
                            ]

                    match List.distinct names with
                    | [] -> [ "", "()" ]
                    | names -> names |> List.map (fun name -> name, $"``{name}``")

            let! parsed =
                withDiagnostics BuildPhase.Parse (fun () ->
                    let source = querySource (List.map snd queries)

                    ParseOneInputSourceText(
                        tcConfig,
                        lexResourceManager,
                        queryFileName,
                        (true, true),
                        DiagnosticsThreadStatics.DiagnosticsLogger,
                        SourceText.ofString source
                    ))

            // The frame's module and assembly grant private and internal access to the expression.
            let accessPaths =
                [
                    yield CompPath(dllInfo.ILScopeRef, SyntaxAccess.Unknown, [])
                    for e in scopeEntities -> e.CompilationPath.NestedCompPath e.LogicalName (ModuleOrType)
                ]

            // In a generic frame the checker sees the frame's typars as free in `<>m0` until
            // `bindFrameValues` makes it generic over them, so its scope errors (FS3187, FS3188) wait for
            // the post-inference checks rerun on the rewritten tree.
            let deferredErrors = if frameTypars.IsEmpty then [] else [ 3187; 3188 ]

            let! tcState, topAttrs, implFile, denv =
                withDiagnosticsIgnoring deferredErrors BuildPhase.TypeCheck (fun () ->
                    let tcEnv0, openDecls0 =
                        GetInitialTcEnv(QueryTypeName, m, tcConfig, tcImports, tcGlobals)

                    let tcState =
                        GetInitialTcState(m, QueryTypeName, tcConfig, tcGlobals, tcImports, tcEnv0, openDecls0)

                    // The access rights are computed from the paths here, as the opens below need them.
                    let tcEnv =
                        let internalsVisible = accessPaths @ tcEnv0.eInternalsVisibleCompPaths

                        { tcEnv0 with
                            eInternalsVisibleCompPaths = internalsVisible
                            eAccessRights = ComputeAccessRights tcEnv0.eAccessPath internalsVisible tcEnv0.eFamilyType
                        }

                    let tcEnv = AddDeclaredTypars NoCheckForDuplicateTypars frameTypars tcEnv

                    let tcEnv =
                        (tcEnv, opens)
                        ||> List.fold (fun env path ->
                            let longId = path |> List.map (fun name -> Ident(name, m))
                            fst (TcOpenModuleOrNamespaceDecl TcResultsSink.NoSink g amap m env (longId, m)))

                    let tcEnv =
                        (tcEnv, scopedVals)
                        ||> List.fold (fun env v -> AddLocalVal g TcResultsSink.NoSink m v env)

                    let implInput =
                        match parsed with
                        | ParsedInput.ImplFile implInput -> implInput
                        | ParsedInput.SigFile _ -> failwith "unexpected signature file"

                    let topAttrs, implFile, tcEnvAtEnd, _, _ =
                        CheckOneImplFile(
                            g,
                            amap,
                            tcState.Ccu,
                            openDecls0,
                            (fun () -> DiagnosticsThreadStatics.DiagnosticsLogger.ErrorCount > 0),
                            tcConfig.conditionalDefines |> Some,
                            TcResultsSink.NoSink,
                            false,
                            tcEnv,
                            None,
                            implInput,
                            tcConfig.diagnosticsOptions
                        )
                        |> Cancellable.runWithoutCancellation

                    tcState, topAttrs, implFile, tcEnvAtEnd.DisplayEnv)

            let implFile, checkedBodies =
                let (CheckedImplFile(qualifiedNameOfFile,
                                     signature,
                                     contents,
                                     hasExplicitEntryPoint,
                                     isScript,
                                     anonRecdTypes,
                                     namedDebugPoints)) =
                    implFile

                let rewrittenContents, checkedBodies =
                    bindFrameValues
                        g
                        frameTypars
                        frameVals
                        ((fieldVariables |> List.map (fun (v, read, _) -> v, read)) @ aliasVariables)
                        ((fieldVariables |> List.map (fun (v, _, write) -> v, write)) @ frameValueWrites)
                        contents

                CheckedImplFile(
                    qualifiedNameOfFile,
                    signature,
                    rewrittenContents,
                    hasExplicitEntryPoint,
                    isScript,
                    anonRecdTypes,
                    namedDebugPoints
                ),
                checkedBodies

            if not frameTypars.IsEmpty then
                do!
                    withDiagnostics BuildPhase.TypeCheck (fun () ->
                        let (CheckedImplFile(signature = signature; contents = contents)) = implFile

                        PostTypeCheckSemanticChecks.CheckImplFile(
                            g,
                            amap,
                            true,
                            InfoReader.InfoReader(g, amap),
                            accessPaths,
                            tcState.Ccu,
                            LightweightTcValForUsingInBuildMethodCall g traitCtxtNone,
                            denv,
                            signature,
                            contents,
                            [],
                            (false, false),
                            false
                        )
                        |> ignore)

            // Variables an assignment can target: the frame's values, fields of `this`, and the
            // user's Immediate-window variables.
            let assignable =
                [
                    yield! frameVals
                    yield! fieldVariables |> List.map (fun (v, _, _) -> v)
                    for alias, (v, _) in List.zip frame.Aliases aliasVariables do
                        if alias.Kind = AliasKind.Variable then
                            v
                ]
                |> List.map _.Stamp
                |> set

            let compiledQueries =
                queries
                |> List.mapi (fun i (name, _) ->
                    let methodName = queryMethodName i

                    match checkedBodies.TryGetValue methodName with
                    | true, (body, ty) ->
                        {
                            Name = name
                            MethodName = methodName
                            ResultType = NicePrint.prettyStringOfTyNoCx denv ty
                            PotentialSideEffect = mayHaveSideEffects g body
                            ReadOnlyResult =
                                match stripDebugPoints body with
                                | Expr.Val(vref, _, _) -> not (vref.IsMutable || assignable.Contains vref.Stamp)
                                | _ -> true
                            BoolResult = typeEquiv g ty g.bool_ty
                        }
                    | _ -> failwith $"'{methodName}' not found in the checked query module")

            let! assembly =
                withDiagnostics BuildPhase.IlxGen (fun () ->
                    let tcVal = LightweightTcValForUsingInBuildMethodCall g traitCtxtNone
                    let optEnv0 = GetInitialOptimizationEnv(tcImports, tcGlobals)

                    let optimized =
                        let (CheckedAssemblyAfterOptimization files), _, _ =
                            ApplyAllOptimizations(tcConfig, g, tcVal, queryFileName, amap, false, optEnv0, tcState.Ccu, [ implFile ])

                        files
                        |> List.map (fun f ->
                            { f with
                                ImplFile = avoidCollectorLowering g f.ImplFile
                            })
                        |> CheckedAssemblyAfterOptimization

                    let ilxGenerator =
                        CreateIlxAssemblyGenerator(tcConfig, tcImports, g, tcVal, tcState.Ccu)

                    let codegen =
                        // As GenerateIlxCode, for the query type.
                        let options: IlxGenOptions =
                            {
                                generateFilterBlocks = tcConfig.generateFilterBlocks
                                emitConstantArraysUsingStaticDataBlobs = true
                                workAroundReflectionEmitBugs = false
                                generateDebugSymbols = false
                                fragName = QueryTypeName
                                localOptimizationsEnabled = tcConfig.optSettings.LocalOptimizationsEnabled
                                testFlagEmitFeeFeeAs100001 = false
                                mainMethodInfo = None
                                ilxBackend = IlWriteBackend
                                fsiMultiAssemblyEmit = false
                                useReflectionFreeCodeGen = false
                                isInteractive = false
                                isInteractiveItExpr = false
                                alwaysCallVirt = tcConfig.alwaysCallVirt
                                parallelIlxGenEnabled = false
                                alwaysInline = tcConfig.alwaysInline
                            }

                        ilxGenerator.GenerateCode(options, optimized, topAttrs.assemblyAttrs, topAttrs.netModuleAttrs)

                    // The frame's class type parameters move from `<>m0` to `<>x`, as the debugger
                    // instantiates the query class like the frame's: IL numbers class type parameters
                    // before method ones, so the method body's type variables keep their meaning.
                    let classTyparCount = classTypars.Length

                    let rewriteQueryMethod (md: ILMethodDef) =
                        let md =
                            if localsAsParameters || localSlots.IsEmpty then
                                md
                            else
                                parametersToFrameLocals frameLocalSignature localSlots md

                        // The state machine's `this` is a byref to the struct (typed `nativeint` for checking).
                        let md =
                            if isStateMachine then
                                md.With(
                                    parameters =
                                        (md.Parameters
                                         |> List.mapi (fun i p ->
                                             if i = 0 then
                                                 { p with
                                                     Type = ILType.Byref thisILType
                                                 }
                                             else
                                                 p))
                                )
                            else
                                md

                        md.With(genericParams = List.skip classTyparCount md.GenericParams)

                    let queryTypeDefs =
                        codegen.ilTypeDefs
                        |> List.map (replaceFieldPlaceholders fieldPlaceholders)
                        |> List.map (fun td ->
                            if td.Name <> QueryTypeName then
                                td
                            else
                                let queryMethod = td.Methods.FindByName QueryMethodName |> List.exactlyOne

                                if
                                    classTyparCount > 0
                                    && td.Methods.AsList() |> List.exists (fun md -> not (isQueryMethodName md.Name))
                                then
                                    failwith "Expressions that define functions are not supported yet in generic classes"

                                td.With(
                                    genericParams = List.take classTyparCount queryMethod.GenericParams,
                                    methods =
                                        mkILMethods (
                                            td.Methods.AsList()
                                            |> List.map (fun md ->
                                                if isQueryMethodName md.Name then
                                                    rewriteQueryMethod md
                                                else
                                                    md)
                                        )
                                ))

                    let ilModule =
                        let ignoresAccessChecks = ignoresAccessChecksTo g [ frame.AssemblyName ]

                        mkILSimpleModule
                            QueryTypeName
                            (QueryTypeName + ".dll")
                            true
                            (4, 0)
                            false
                            (mkILTypeDefs (fst ignoresAccessChecks :: queryTypeDefs))
                            None
                            None
                            0x0
                            (mkILExportedTypes [])
                            ""
                        |> fun ilModule ->
                            let manifest = ilModule.ManifestOfAssembly

                            { ilModule with
                                Manifest =
                                    Some
                                        { manifest with
                                            CustomAttrsStored = storeILCustomAttrs (mkILCustomAttrs (snd ignoresAccessChecks))
                                        }
                            }

                    let options =
                        {
                            ilg = g.ilg
                            outfile = QueryTypeName + ".dll"
                            pdbfile = None
                            emitTailcalls = false
                            deterministic = true
                            portablePDB = false
                            embeddedPDB = false
                            embedAllSource = false
                            embedSourceList = []
                            allGivenSources = []
                            sourceLink = ""
                            checksumAlgorithm = tcConfig.checksumAlgorithm
                            signer = None
                            dumpDebugInfo = false
                            referenceAssemblyOnly = false
                            referenceAssemblyAttribOpt = None
                            referenceAssemblySignatureHash = None
                            pathMap = tcConfig.pathMap
                            moduleCustomDebugInfoRows = []
                            methodCustomDebugInfoRows = Map.empty
                        }

                    fst (WriteILBinaryInMemory(options, ilModule, id)))

            return
                assembly,
                (match request with
                 | QueryRequest.Expression _ -> compiledQueries
                 | QueryRequest.Locals _ -> compiledQueries |> List.filter (fun q -> q.Name <> ""))
        }
        |> Result.mapError inExpression
