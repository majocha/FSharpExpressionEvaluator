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

let rec private hasDefinition name (t: IType) =
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

/// Precedence of a rendered type, to decide where parentheses are needed.
type private Precedence =
    | Arrow = 0
    | Star = 1
    | Atom = 2

let private stripArity (name: string) =
    match name.IndexOf '`' with
    | -1 -> name
    | i -> name.Substring(0, i)

let private ownArity (name: string) =
    match name.IndexOf '`' with
    | -1 -> 0
    | i -> int (name.Substring(i + 1))

/// F# type name of a function type `a -> b`, from FSharpFunc or OptimizedClosures.FSharpFunc arguments.
let private functionTypeArgs (t: IType) =
    Types.hierarchy t
    |> Seq.tryPick (fun t ->
        if t.DefinitionName = "Microsoft.FSharp.Core.FSharpFunc`2" then
            Some t.GenericArguments
        elif t.DefinitionName <> null && t.DefinitionName.StartsWith "Microsoft.FSharp.Core.OptimizedClosures+FSharpFunc`" then
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
            let elements = tupleElementTypes t |> List.map (render >> starOperand) |> String.concat " * "
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
                    recordFields t |> List.map (fun f -> $"{f.Name}: {fst (render f.Type)}") |> String.concat "; "

                $"{{| {fields} |}}", Precedence.Atom
            | None -> nominal t args, Precedence.Atom

and private atom (name, precedence) =
    if precedence = Precedence.Atom then name else $"({name})"

and private starOperand (name, precedence) =
    if precedence = Precedence.Atom then name else $"({name})"

and private arrowDomain (name, precedence) =
    if precedence = Precedence.Arrow then $"({name})" else name

and private genericArgs (args: IType list) =
    if args.IsEmpty then "" else "<" + (args |> List.map (render >> fst) |> String.concat ", ") + ">"

/// System.Tuple`8 nests elements 8+ in its Rest argument.
and private tupleElementTypes (t: IType) =
    let args = t.GenericArguments

    if args.Length = 8 then
        List.take 7 args @ tupleElementTypes args[7]
    else
        args

/// Namespace- and module-qualified name with F#-style generic arguments. For nested generic types
/// the CLR repeats the enclosing type's arguments first; each level takes its own arity.
and private nominal (t: IType) (args: IType list) =
    let name =
        let n = stripArity t.Name

        if representationFlags t &&& moduleSuffixFlag <> 0 && n.EndsWith "Module" then
            n.Substring(0, n.Length - "Module".Length)
        else
            n

    let arity = ownArity t.Name
    let outerArgs = List.take (args.Length - arity) args
    let ownArgs = List.skip (args.Length - arity) args

    let prefix =
        match t.DeclaringType with
        | Some d -> nominal d outerArgs + "."
        | None when String.IsNullOrEmpty t.Namespace -> ""
        | None -> t.Namespace + "."

    prefix + name + genericArgs ownArgs

/// Element types of a tuple type, with System.Tuple`8's Rest flattened.
let tupleTypes (t: IType) = tupleElementTypes t

/// The F# name of a type: `int list`, `int * string`, `int -> int`, `Shape`, `{| A: int |}`.
let typeName (t: IType) = fst (render t)

/// For union values: the union type rather than its case subclass.
let displayType (t: IType) =
    match shape t with
    | UnionShape u -> u.Type
    | _ -> t
