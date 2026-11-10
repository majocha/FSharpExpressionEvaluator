/// F# shapes and F# type names, recovered from metadata only (docs/DESIGN.md §5.2, §5.3).
module internal FSharp.ExpressionEvaluator.FSharpTypes

open System

[<Literal>]
let private CompilationRepresentation = "Microsoft.FSharp.Core.CompilationRepresentationAttribute"

let private moduleSuffixFlag = 4
let private useNullAsTrueValueFlag = 8

let private representationFlags (t: IType) =
    match Types.attribute CompilationRepresentation t.Attributes with
    | Some { Args = flags :: _ } -> Convert.ToInt32 flags
    | _ -> 0

type UnionCase = { Name: string; Index: int }

type UnionInfo =
    {
        /// The union type itself (case values may have a nested subclass as their runtime type).
        Type: IType
        Cases: UnionCase[]
        /// The case represented by null (UseNullAsTrueValue, e.g. None).
        NullCase: UnionCase option
        /// Unions with a `_tag` field store the case index in it; others are told apart by runtime type.
        HasTagField: bool
    }

/// A field shown for a union case, record or exception: display name, declared type, backing field.
type ShapeField = { Name: string; Type: IType; BackingField: string }

type RecordKind =
    | Record
    | AnonymousRecord
    | RefCell
    | FSharpException

type Shape =
    | UnitShape
    | ListShape of element: IType
    | ArrayShape of element: IType
    | TupleShape of isStruct: bool
    | UnionShape of UnionInfo
    | RecordShape of RecordKind
    | FunctionShape
    | MapShape
    | SetShape
    /// `System.Lazy<'T>`, as F# `lazy` creates it.
    | LazyShape of value: IType
    /// `System.Numerics.BigInteger`, F#'s `bigint`.
    | BigIntShape
    | NotFSharp

let private unionInfoCache = Types.cache ()

let private unionInfo (u: IType) =
    unionInfoCache u (fun u ->
        // Each case has a static `NewX` method (cases with fields) or `get_X` getter (nullary cases)
        // carrying CompilationMapping(UnionCase, index).
        let cases =
            [| for m in u.Methods do
                   match CompilationMapping.tryGet m.Attributes with
                   | Some [ kind; index ] when kind &&& CompilationMapping.KindMask = CompilationMapping.UnionCase ->
                       let name =
                           if m.Name.StartsWith "New" then m.Name.Substring 3
                           elif m.Name.StartsWith "get_" then m.Name.Substring 4
                           else m.Name

                       { Name = name; Index = index }, m.Name.StartsWith "get_"
                   | _ -> () |]
            |> Array.sortBy (fun (c, _) -> c.Index)

        {
            Type = u
            Cases = cases |> Array.map fst
            NullCase =
                if representationFlags u &&& useNullAsTrueValueFlag <> 0 then
                    cases |> Array.tryFind snd |> Option.map fst
                else
                    None
            HasTagField = (Types.tryField u "_tag").IsSome
        })

let private isAnonymousRecord (t: IType) = t.Name.StartsWith "<>f__AnonymousType"

let private shapeCache = Types.cache ()

let private hasDefinition name (t: IType) =
    Types.hierarchy t |> Seq.exists (fun t -> t.DefinitionName = name)

let private tupleArity (prefix: string) (name: string) =
    name.StartsWith prefix && name.Length = prefix.Length + 1 && Char.IsDigit name[prefix.Length]

/// Classifies a type by its F# representation.
let shape (t: IType) : Shape =
    shapeCache t (fun t ->
        let definition = t.DefinitionName

        let mappingKind (t: IType) = CompilationMapping.kind t.Attributes

        if isNull definition then NotFSharp
        elif definition = "Microsoft.FSharp.Core.Unit" then UnitShape
        elif definition = "Microsoft.FSharp.Collections.FSharpList`1" then ListShape t.GenericArguments[0]
        elif t.IsArray && t.ArrayRank = 1 then ArrayShape t.ElementType
        elif tupleArity "System.Tuple`" definition then TupleShape false
        elif tupleArity "System.ValueTuple`" definition then TupleShape true
        elif definition = "Microsoft.FSharp.Collections.FSharpMap`2" then MapShape
        elif definition = "Microsoft.FSharp.Collections.FSharpSet`1" then SetShape
        elif definition = "System.Lazy`1" then LazyShape t.GenericArguments[0]
        elif definition = "System.Numerics.BigInteger" then BigIntShape
        elif hasDefinition "Microsoft.FSharp.Core.FSharpFunc`2" t then FunctionShape
        else
            match mappingKind t with
            | Some k when k = CompilationMapping.SumType -> UnionShape(unionInfo t)
            | Some k when k = CompilationMapping.RecordType ->
                if definition = "Microsoft.FSharp.Core.FSharpRef`1" then RecordShape RefCell
                elif isAnonymousRecord t then RecordShape AnonymousRecord
                else RecordShape Record
            | Some k when k = CompilationMapping.Exception -> RecordShape FSharpException
            | _ ->
                // A union case subclass (e.g. Shape+Circle) carries no mapping itself.
                match t.BaseType with
                | Some b when mappingKind b = Some CompilationMapping.SumType -> UnionShape(unionInfo b)
                | _ -> NotFSharp)

/// The backing field of an F# property: `_name` / `item` (union fields), `Name@` (records).
let private backingField (owner: IType) (propertyName: string) =
    let lowerFirst = string (Char.ToLowerInvariant propertyName[0]) + propertyName.Substring 1

    [ "_" + propertyName; lowerFirst; propertyName + "@"; propertyName ]
    |> List.tryFind (fun name -> (Types.tryField owner name).IsSome)

let private fieldsOf (owner: IType) (matches: int list -> int option) =
    Types.hierarchy owner
    |> Seq.collect (fun t -> t.Properties)
    |> Seq.choose (fun p ->
        match CompilationMapping.tryGet p.Attributes with
        | Some args when args.Head &&& CompilationMapping.KindMask = CompilationMapping.Field ->
            matches args.Tail
            |> Option.bind (fun order ->
                backingField owner p.Name
                |> Option.map (fun backing -> order, { Name = p.Name; Type = p.PropertyType; BackingField = backing }))
        | _ -> None)
    |> Seq.distinctBy (fun (_, f) -> f.Name)
    |> Seq.sortBy fst
    |> Seq.map snd
    |> Seq.toList

let private caseFieldCache = Types.cache ()

/// Fields of a union case, found from the runtime type of a value of that case.
let unionCaseFields (runtimeType: IType) (case: UnionCase) =
    let byCase = caseFieldCache runtimeType (fun t -> Collections.Generic.Dictionary<int, ShapeField list>())

    lock byCase (fun () ->
        match byCase.TryGetValue case.Index with
        | true, fields -> fields
        | _ ->
            let fields =
                fieldsOf runtimeType (function
                    | [ c; i ] when c = case.Index -> Some i
                    | _ -> None)

            byCase[case.Index] <- fields
            fields)

let private recordFieldCache = Types.cache ()

/// Fields of a record, anonymous record, ref cell or F# exception, in declaration order.
let recordFields (t: IType) =
    recordFieldCache t (fun t ->
        fieldsOf t (function
            | [ i ] -> Some i
            | _ -> None))

// ---------------------------------------------------------------------------------------------
// Type names

let private abbreviations =
    dict
        [ "System.Int32", "int"
          "System.Int64", "int64"
          "System.Int16", "int16"
          "System.SByte", "sbyte"
          "System.Byte", "byte"
          "System.UInt16", "uint16"
          "System.UInt32", "uint32"
          "System.UInt64", "uint64"
          "System.IntPtr", "nativeint"
          "System.UIntPtr", "unativeint"
          "System.Double", "float"
          "System.Single", "float32"
          "System.Decimal", "decimal"
          "System.Boolean", "bool"
          "System.Char", "char"
          "System.String", "string"
          "System.Object", "obj"
          "System.Void", "unit"
          "System.Exception", "exn"
          "System.Numerics.BigInteger", "bigint"
          "Microsoft.FSharp.Core.Unit", "unit" ]

let private postfix =
    dict
        [ "Microsoft.FSharp.Collections.FSharpList`1", "list"
          "Microsoft.FSharp.Core.FSharpOption`1", "option"
          "Microsoft.FSharp.Core.FSharpValueOption`1", "voption"
          "Microsoft.FSharp.Core.FSharpRef`1", "ref" ]

let private renamed =
    dict
        [ "System.Collections.Generic.IEnumerable`1", "seq"
          "Microsoft.FSharp.Collections.FSharpMap`2", "Map"
          "Microsoft.FSharp.Collections.FSharpSet`1", "Set"
          "Microsoft.FSharp.Core.FSharpResult`2", "Result"
          "Microsoft.FSharp.Control.FSharpAsync`1", "Async"
          "System.Lazy`1", "Lazy" ]

/// F#'s keywords, including the reserved ones, which a name must be in double backticks to use. A
/// name in backticks that doesn't need them is still valid F#, so the list errs on the long side.
let private keywords =
    set
        [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate"; "do"; "done"
          "downcast"; "downto"; "elif"; "else"; "end"; "exception"; "extern"; "false"; "finally"; "fixed"
          "for"; "fun"; "function"; "global"; "if"; "in"; "inherit"; "inline"; "interface"; "internal"
          "lazy"; "let"; "match"; "member"; "module"; "mutable"; "namespace"; "new"; "not"; "null"; "of"
          "open"; "or"; "override"; "private"; "public"; "rec"; "return"; "select"; "sig"; "static"
          "struct"; "then"; "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while"
          "with"; "yield"; "const"; "break"; "checked"; "component"; "constraint"; "continue"; "event"
          "external"; "include"; "mixin"; "parallel"; "process"; "protected"; "pure"; "sealed"
          "tailcall"; "trait"; "virtual"; "asr"; "land"; "lor"; "lsl"; "lsr"; "lxor"; "mod" ]

/// A name as F# source writes it: as it is when it is an identifier, otherwise in double
/// backticks (``my field``, ``type``, ``x@5``).
let fsharpIdent (name: string) =
    let isIdentifier =
        name.Length > 0
        && (Char.IsLetter name[0] || name[0] = '_')
        && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '\'')
        && not (keywords.Contains name)

    if isIdentifier then name else $"``{name}``"

/// A name as the Type column and the call stack show it: as `fsharpIdent` does, except that
/// compiler-generated names (`f@12`, `work@24`) are shown as they are, since no source writes them.
let displayIdent (name: string) =
    if name.Contains "@" then name else fsharpIdent name

/// Precedence of a rendered type, to decide where parentheses are needed.
type private Precedence =
    | Arrow = 0
    | Star = 1
    | Atom = 2

/// A type name without its generic arity: `List` for ``List`1``.
let stripArity (name: string) =
    match name.IndexOf '`' with
    | -1 -> name
    | i -> name.Substring(0, i)

let private ownArity (name: string) =
    match name.IndexOf '`' with
    | -1 -> 0
    | i -> int (name.Substring(i + 1))

/// A type's own name as source writes it: no generic arity, and a module compiled with a `Module`
/// suffix without it. The compiler adds the suffix for `[<CompilationRepresentation(ModuleSuffix)>]`,
/// and also, with no attribute, to a module named as a type beside it (`type Shape` and
/// `module Shape`), so that is checked for in the module's assembly.
let private sourceName (t: IType) =
    let n = stripArity t.Name

    let isSuffixed () =
        let baseName = n.Substring(0, n.Length - "Module".Length)

        let scope =
            match t.DeclaringType with
            | Some d -> d.FullName + "+"
            | None when String.IsNullOrEmpty t.Namespace -> ""
            | None -> t.Namespace + "."

        representationFlags t &&& moduleSuffixFlag <> 0
        || (CompilationMapping.kind t.Attributes = Some CompilationMapping.Module
            && [ 0..4 ]
               |> List.exists (fun arity -> t.TypeExists(scope + baseName + (if arity = 0 then "" else $"`{arity}"))))

    if n.EndsWith "Module" && n.Length > "Module".Length && isSuffixed () then
        n.Substring(0, n.Length - "Module".Length)
    else
        n

/// F# type name of a function type `a -> b`, from FSharpFunc or OptimizedClosures.FSharpFunc arguments.
let private functionTypeArgs (t: IType) =
    Types.hierarchy t
    |> Seq.tryPick (fun t ->
        let d = t.DefinitionName

        if d = "Microsoft.FSharp.Core.FSharpFunc`2"
           || (not (isNull d) && d.StartsWith "Microsoft.FSharp.Core.OptimizedClosures+FSharpFunc`") then
            Some t.GenericArguments
        else
            None)

let rec private render (t: IType) : string * Precedence =
    if t.IsGenericParameter then
        "'" + t.Name, Precedence.Atom
    elif t.IsArray then
        let element = atom (render t.ElementType)

        if t.ArrayRank = 1 then
            element + " array", Precedence.Atom
        else
            element + "[" + String(',', t.ArrayRank - 1) + "]", Precedence.Atom
    else
        let definition = t.DefinitionName
        let args = t.GenericArguments

        match definition with
        | null -> t.Name, Precedence.Atom
        | d when abbreviations.ContainsKey d -> abbreviations[d], Precedence.Atom
        | d when postfix.ContainsKey d -> atom (render args[0]) + " " + postfix[d], Precedence.Atom
        | d when tupleArity "System.Tuple`" d || tupleArity "System.ValueTuple`" d ->
            let isStruct = d.StartsWith "System.ValueTuple"
            let elements = tupleTypes t |> List.map (render >> atom) |> String.concat " * "
            (if isStruct then $"struct ({elements})", Precedence.Atom else elements, Precedence.Star)
        | d when renamed.ContainsKey d -> renamed[d] + genericArgs args, Precedence.Atom
        | _ ->
            match functionTypeArgs t with
            | Some fargs ->
                // a -> b -> c: every argument but the last is a domain.
                let domains = fargs |> List.take (fargs.Length - 1) |> List.map (render >> arrowDomain)
                String.concat " -> " (domains @ [ fst (render (List.last fargs)) ]), Precedence.Arrow
            | None when t.Name.StartsWith "<>f__AnonymousType" ->
                let fields =
                    recordFields t |> List.map (fun f -> $"{displayIdent f.Name}: {fst (render f.Type)}") |> String.concat "; "

                $"{{| {fields} |}}", Precedence.Atom
            | None -> nominal t args, Precedence.Atom

and private atom (name, precedence) =
    if precedence = Precedence.Atom then name else $"({name})"

and private arrowDomain (name, precedence) =
    if precedence = Precedence.Arrow then $"({name})" else name

and private genericArgs (args: IType list) =
    if args.IsEmpty then "" else "<" + (args |> List.map (render >> fst) |> String.concat ", ") + ">"

/// Element types of a tuple type: System.Tuple`8 nests elements 8+ in its Rest argument.
and tupleTypes (t: IType) =
    let args = t.GenericArguments

    if args.Length = 8 then
        List.take 7 args @ tupleTypes args[7]
    else
        args

/// Namespace- and module-qualified name with F#-style generic arguments. For nested generic types
/// the CLR repeats the enclosing type's arguments first; each level takes its own arity.
and private nominal (t: IType) (args: IType list) =
    let name = displayIdent (sourceName t)

    let arity = ownArity t.Name
    let outerArgs = List.take (args.Length - arity) args
    let ownArgs = List.skip (args.Length - arity) args

    let prefix =
        match t.DeclaringType with
        | Some d -> nominal d outerArgs + "."
        | None when String.IsNullOrEmpty t.Namespace -> ""
        | None -> String.Join(".", t.Namespace.Split '.' |> Array.map displayIdent) + "."

    prefix + name + genericArgs ownArgs

/// The parts of a type's F# path as source names it: its namespace's, then its enclosing modules
/// and types, and its own name.
let rec private sourcePathParts (t: IType) =
    match t.DeclaringType with
    | Some d -> sourcePathParts d @ [ sourceName t ]
    | None when String.IsNullOrEmpty t.Namespace -> [ sourceName t ]
    | None -> List.ofArray (t.Namespace.Split '.') @ [ sourceName t ]

/// The F# path of a type as source names it, without generic arguments (`Ns.Shape.Kind` for
/// `Ns.ShapeModule+Kind`).
let sourcePath (t: IType) = String.Join(".", sourcePathParts t)

/// The path of `sourcePath` as an expression names it: each part in double backticks where F#
/// needs them.
let sourcePathExpression (t: IType) = String.Join(".", sourcePathParts t |> List.map fsharpIdent)

/// The F# name of a type: `int list`, `int * string`, `int -> int`, `Shape`, `{| A: int |}`.
let typeName (t: IType) = fst (render t)

let private seqTypeCache = Types.cache ()

/// `seq<'T>` for an F# sequence object: FSharp.Core's implementations (ranges, `Seq` module
/// results) and compiled `seq { }` state machines. None for other types, including F# shapes
/// and collections like List<'T> that have a display of their own.
let seqType (t: IType) =
    seqTypeCache t (fun t ->
        let isFSharpSeq =
            (not (isNull t.Namespace) && t.Namespace.StartsWith "Microsoft.FSharp")
            || hasDefinition "Microsoft.FSharp.Core.CompilerServices.GeneratedSequenceBase`1" t

        if isFSharpSeq && shape t = NotFSharp then
            t.Interfaces |> List.tryFind (fun i -> i.DefinitionName = "System.Collections.Generic.IEnumerable`1")
        else
            None)

/// For union values: the union type rather than its case subclass; for F# sequences, `seq<'T>`
/// rather than their closure class.
let displayType (t: IType) =
    match shape t with
    | UnionShape u -> u.Type
    | _ -> seqType t |> Option.defaultValue t

/// Custom type info payloads from the expression compiler: a version byte, then UTF-8 text.
module private Payload =

    let private version = 1uy

    let encode (text: string) =
        Array.append [| version |] (Text.Encoding.UTF8.GetBytes text)

    /// The text of a payload with the expected id, or None for another id or an unknown version.
    let tryDecode (expected: Guid) (id: Guid) (payload: byte[]) =
        if id = expected && not (isNull payload) && payload.Length > 1 && payload[0] = version then
            Some(Text.Encoding.UTF8.GetString(payload, 1, payload.Length - 1))
        else
            None

/// The F# type of an expression's result, when it says more than the CLR type: units of measure
/// (`float<m>`) and the user's type abbreviations (docs/DESIGN.md §6.8). The expression compiler
/// sends it as Concord custom type info with this payload id; other providers ignore the id.
module TypeInfo =

    let payloadId = Guid "6C1B7F2E-3D5A-4E8B-9F0C-2A7D4E6B8C13"

    /// The payload: a version byte, then the type name in UTF-8.
    let encode (typeName: string) = Payload.encode typeName

    /// The type name of a payload, or None for another id or an unknown version.
    let tryDecode (id: Guid) (payload: byte[]) = Payload.tryDecode payloadId id payload

/// The compiler temporaries under the Locals window's `[compiler generated]` row (docs/DESIGN.md
/// §6.4), as expressions name them. The expression compiler sends them as Concord custom type info
/// of that row, with this payload id.
module CompilerGenerated =

    let payloadId = Guid "EDC0AEE5-090B-4926-AC47-0804F46C5132"

    /// The payload: a version byte, then the names in UTF-8, one per line (no F# name has a line break).
    let encode (names: string list) = Payload.encode (String.concat "\n" names)

    /// The names of a payload, or None for another id or an unknown version.
    let tryDecode (id: Guid) (payload: byte[]) =
        Payload.tryDecode payloadId id payload |> Option.map (fun text -> text.Split '\n' |> List.ofArray)

/// A variable declared in the Immediate window (`let mutable x = ...`): whether it is mutable, and
/// its F# type. The expression compiler passes it to the debugger's `CreateVariable` as the
/// variable's custom type info, and reads it back from the debugger's aliases.
module Variable =

    let payloadId = Guid "3F0B6E1D-8C2A-4B57-A9E4-71D5C0F2B6A8"

    /// The payload: a version byte, then `mutable` or nothing, a line break and the F# type, in UTF-8.
    let encode (isMutable: bool) (fsharpType: string) =
        Payload.encode ((if isMutable then "mutable" else "") + "\n" + fsharpType)

    /// Whether the variable is mutable and its F# type, or None for another id or an unknown version.
    let tryDecode (id: Guid) (payload: byte[]) =
        Payload.tryDecode payloadId id payload
        |> Option.bind (fun text ->
            match text.IndexOf '\n' with
            | -1 -> None
            | i -> Some(text.Substring(0, i) = "mutable", text.Substring(i + 1)))
