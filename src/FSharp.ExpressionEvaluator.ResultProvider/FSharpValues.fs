/// F# value previews (`[1; 2]`, `Some 42`, `{ X = 1 }`) and children, read from memory only
/// (docs/DESIGN.md §5.2–5.4). No func-eval: everything is field and array element reads.
module internal FSharp.ExpressionEvaluator.FSharpValues

open System
open System.Globalization
open System.Text
open FSharp.ExpressionEvaluator.FSharpTypes

type Options =
    {
        /// Items shown in a list/array/map preview before `...`.
        MaxItems: int
        /// Nesting depth of previews; deeper values show as `...`.
        MaxDepth: int
        /// Preview length; longer previews are cut with `...`.
        MaxLength: int
        /// Items materialized when expanding a list, map or set.
        MaxChildren: int
    }

let defaultOptions = { MaxItems = 10; MaxDepth = 3; MaxLength = 250; MaxChildren = 10000 }

// ---------------------------------------------------------------------------------------------
// Reading shapes

let private isError (v: IValue) = not (isNull v.Error)

/// The case of a union value, without calling the `Tag` property.
let unionCase (u: UnionInfo) (v: IValue) =
    if v.IsNull then
        u.NullCase
    elif u.HasTagField then
        let tag = v.Field "_tag"
        if isError tag then None else u.Cases |> Array.tryFind (fun c -> c.Index = Convert.ToInt32 tag.HostValue)
    elif u.Cases.Length = 1 then
        Some u.Cases[0]
    elif v.Type.DefinitionName <> u.Type.DefinitionName then
        // A nested case subclass: `Circle` for cases with fields, `_Empty` for nullary ones.
        let name = v.Type.Name.TrimStart '_'
        let name = match name.IndexOf '`' with -1 -> name | i -> name.Substring(0, i)
        u.Cases |> Array.tryFind (fun c -> c.Name = name)
    else
        // UseNullAsTrueValue unions (option) are their own type for the non-null case.
        u.Cases |> Array.tryFind (fun c -> Some c <> u.NullCase)

/// Walks an F# list, returning at most `max` items and whether more remain.
let listItems max (list: IValue) =
    let rec loop (node: IValue) acc n =
        let tail = node.Field "tail"

        if isError tail || tail.IsNull then List.rev acc, false
        elif n = max then List.rev acc, true
        else loop tail (node.Field "head" :: acc) (n + 1)

    if list.IsNull then [], false else loop list [] 0

let private treeField (v: IValue) =
    v.Type.Fields |> List.tryFind (fun f -> f.Name.StartsWith "tree" && not f.IsStatic) |> Option.map (fun f -> v.Field f.Name)

/// In-order walk of the AVL tree behind Map (key and value) and Set (key only).
let treeItems max (collection: IValue) withValue =
    let items = Collections.Generic.List<IValue * IValue option>()
    let mutable truncated = false

    let rec walk (node: IValue) =
        if not truncated && not node.IsNull && not (isError node) then
            let isInner = (Types.tryField node.Type "left").IsSome
            if isInner then walk (node.Field "left")

            if items.Count = max then
                truncated <- true
            else
                items.Add(node.Field "k", (if withValue then Some(node.Field "v") else None))

            if isInner then walk (node.Field "right")

    match treeField collection with
    | Some tree -> walk tree
    | None -> ()

    List.ofSeq items, truncated

/// System.Tuple keeps elements in m_ItemN (8+ nested in m_Rest); ValueTuple in ItemN / Rest.
let tupleItems (v: IValue) =
    let rec items (v: IValue) =
        let prefix = if v.Type.DefinitionName.StartsWith "System.ValueTuple" then "" else "m_"
        let arity = v.Type.GenericArguments.Length
        let own = [ for i in 1 .. min arity 7 -> v.Field $"{prefix}Item{i}" ]
        if arity = 8 then own @ items (v.Field(prefix + "Rest")) else own

    items v

/// Captured values of a closure: its instance fields, minus the cached `@_instance` singleton.
let closureFields (t: IType) =
    Types.hierarchy t
    |> Seq.takeWhile (fun t -> not (t.DefinitionName.StartsWith "Microsoft.FSharp.Core."))
    |> Seq.collect (fun t -> t.Fields)
    |> Seq.filter (fun f -> not f.IsStatic && not (f.Name.StartsWith "@"))
    |> Seq.toList

// ---------------------------------------------------------------------------------------------
// Previews

let private escapeChar (quote: char) (c: char) =
    match c with
    | '\n' -> "\\n"
    | '\t' -> "\\t"
    | '\r' -> "\\r"
    | '\b' -> "\\b"
    | '\\' -> "\\\\"
    | '\000' -> "\\000"
    | c when c = quote -> "\\" + string c
    | c when Char.IsControl c -> $"\\u{int c:X4}"
    | c -> string c

let formatString (s: string) =
    let sb = StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore
    for c in s do sb.Append(escapeChar '"' c) |> ignore
    sb.Append('"').ToString()

let private formatFloat (text: string) =
    // F# always shows a decimal point or exponent: 1.0, 1.5, 1e+20.
    if text.IndexOfAny [| '.'; 'E'; 'e'; 'n'; 'N'; 'i' |] >= 0 then text else text + ".0"

/// F# literal syntax for primitives (`%A` conventions), or None for other values.
let formatPrimitive (value: obj) =
    let inv = CultureInfo.InvariantCulture

    match value with
    | :? int as x -> Some(x.ToString inv)
    | :? int64 as x -> Some(x.ToString inv + "L")
    | :? int16 as x -> Some(x.ToString inv + "s")
    | :? sbyte as x -> Some(x.ToString inv + "y")
    | :? byte as x -> Some(x.ToString inv + "uy")
    | :? uint16 as x -> Some(x.ToString inv + "us")
    | :? uint32 as x -> Some(x.ToString inv + "u")
    | :? uint64 as x -> Some(x.ToString inv + "UL")
    | :? nativeint as x -> Some(x.ToString() + "n")
    | :? unativeint as x -> Some(x.ToString() + "un")
    | :? float as x when Double.IsNaN x -> Some "nan"
    | :? float as x when Double.IsPositiveInfinity x -> Some "infinity"
    | :? float as x when Double.IsNegativeInfinity x -> Some "-infinity"
    | :? float as x -> Some(formatFloat (x.ToString("R", inv)))
    | :? float32 as x when Single.IsNaN x -> Some "nanf"
    | :? float32 as x when Single.IsPositiveInfinity x -> Some "infinityf"
    | :? float32 as x when Single.IsNegativeInfinity x -> Some "-infinityf"
    | :? float32 as x -> Some(formatFloat (x.ToString("R", inv)) + "f")
    | :? decimal as x -> Some(x.ToString inv + "M")
    | :? bool as x -> Some(if x then "true" else "false")
    | :? char as c -> Some("'" + escapeChar ''' c + "'")
    | :? string as s -> Some(formatString s)
    | _ -> None

let private join (separator: string) (parts: string seq) = String.Join(separator, parts)

let private withMore truncated (items: string list) =
    if truncated then items @ [ "..." ] else items

/// Whether a union argument needs parentheses: `Some (Some 1)`, `Some (Circle 2.0)`.
let private isUnionWithFields (v: IValue) =
    not v.IsNull
    && not (isError v)
    && match shape v.Type with
       | UnionShape u ->
           match unionCase u v with
           | Some c -> not (unionCaseFields v.Type c).IsEmpty
           | None -> false
       | RecordShape FSharpException -> not (recordFields v.Type).IsEmpty
       | _ -> false

/// Name of a closure for `<fun:...>`: the compiler-generated class name, e.g. `f@34`.
let closureName (t: IType) = t.Name

let rec private preview (options: Options) depth (declared: IType option) (v: IValue) : string =
    let recurse declared v = preview options (depth + 1) declared v

    if isError v then
        $"<{v.Error}>"
    elif v.IsNull then
        match declared |> Option.map shape with
        | Some(UnionShape { NullCase = Some c }) -> c.Name
        | Some UnitShape -> "()"
        | _ -> "null"
    else
        match shape v.Type with
        | NotFSharp ->
            match formatPrimitive v.HostValue with
            | Some s when not v.Type.IsEnum -> s
            | _ -> v.FallbackDisplay()
        | UnitShape -> "()"
        | FunctionShape -> $"<fun:{closureName v.Type}>"
        | _ when depth >= options.MaxDepth -> "..."
        | ListShape element ->
            let items, truncated = listItems options.MaxItems v
            "[" + join "; " (items |> List.map (recurse (Some element)) |> withMore truncated) + "]"
        | ArrayShape element ->
            let shown = min v.ArrayLength options.MaxItems
            let items = [ for i in 0 .. shown - 1 -> recurse (Some element) (v.Element i) ]
            "[|" + join "; " (withMore (v.ArrayLength > shown) items) + "|]"
        | TupleShape isStruct ->
            let elements = tupleItems v |> List.map (recurse None) |> join ", "
            if isStruct then $"struct ({elements})" else $"({elements})"
        | UnionShape u ->
            match unionCase u v with
            | None -> v.FallbackDisplay()
            | Some case ->
                match unionCaseFields v.Type case with
                | [] -> case.Name
                | [ f ] ->
                    let arg = v.Field f.BackingField
                    let text = recurse (Some f.Type) arg
                    if isUnionWithFields arg then $"{case.Name} ({text})" else $"{case.Name} {text}"
                | fields ->
                    let args = fields |> List.map (fun f -> recurse (Some f.Type) (v.Field f.BackingField)) |> join ", "
                    $"{case.Name} ({args})"
        | RecordShape kind ->
            let fields = recordFields v.Type
            let values = fields |> List.map (fun f -> f, recurse (Some f.Type) (v.Field f.BackingField))

            match kind with
            | FSharpException ->
                match values with
                | [] -> v.Type.Name
                | _ -> $"""{v.Type.Name} ({values |> List.map snd |> join ", "})"""
            | AnonymousRecord -> "{| " + (values |> List.map (fun (f, s) -> $"{f.Name} = {s}") |> join "; ") + " |}"
            | Record
            | RefCell -> "{ " + (values |> List.map (fun (f, s) -> $"{f.Name} = {s}") |> join "; ") + " }"
        | MapShape ->
            let items, truncated = treeItems options.MaxItems v true
            let args = v.Type.GenericArguments

            let entries =
                items |> List.map (fun (k, value) -> $"({recurse (Some args[0]) k}, {recurse (Some args[1]) value.Value})")

            "map [" + join "; " (withMore truncated entries) + "]"
        | SetShape ->
            let items, truncated = treeItems options.MaxItems v false
            let element = v.Type.GenericArguments[0]
            "set [" + join "; " (items |> List.map (fst >> recurse (Some element)) |> withMore truncated) + "]"

/// One-line F# preview of a value, or None when the value is not an F# shape (render it elsewhere).
let tryPreview (options: Options) (declared: IType option) (v: IValue) =
    let fsharp =
        if v.IsNull then
            match declared |> Option.map shape with
            | Some(UnionShape { NullCase = Some _ })
            | Some UnitShape -> true
            | _ -> false
        else
            isError v |> not && shape v.Type <> NotFSharp

    if fsharp then
        let text = preview options 0 declared v
        Some(if text.Length > options.MaxLength then text.Substring(0, options.MaxLength) + "..." else text)
    else
        None

/// F# type name for the Type column: the declared type when known (a union rather than its case
/// class, `int -> int` rather than the closure class).
let typeNameOf (declared: IType option) (v: IValue) =
    match declared with
    | Some d -> typeName d
    | None -> typeName (displayType v.Type)

// ---------------------------------------------------------------------------------------------
// Children and full names

/// Full names for "Add Watch" and re-evaluation. They target the active expression compiler, which
/// is still C# until the F# compiler lands (M4); member access and indexers read the same in both.
module FullNames =

    let private isSimple (expression: string) =
        expression |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '@' || c = '$' || c = '[' || c = ']')

    let private parenthesize (expression: string) =
        if isSimple expression then expression else $"({expression})"

    let memberAccess (parent: string) (name: string) = $"{parenthesize parent}.{name}"

    let index (parent: string) (i: int) = $"{parenthesize parent}[{i}]"

    /// C# cast to a non-generic union case class, so its fields can be named.
    let caseMember (parent: string) (caseClass: IType) (name: string) =
        if caseClass.GenericArguments.IsEmpty && not (isNull caseClass.FullName) then
            Some $"(({caseClass.FullName.Replace('+', '.')}){parent}).{name}"
        else
            None

type Child =
    {
        Name: string
        /// Read lazily: arrays and long lists are paged.
        Value: System.Lazy<IValue>
        DeclaredType: IType option
        FullName: string option
    }

let private child name declared fullName value =
    { Name = name; Value = value; DeclaredType = declared; FullName = fullName }

/// Children of an F# value, or None when it is not an F# shape. `parentFullName` is the full name
/// of the value itself, if it has one.
let tryChildren (options: Options) (parentFullName: string option) (v: IValue) : Child list option =
    let memberName name = parentFullName |> Option.map (fun p -> FullNames.memberAccess p name)
    let indexName i = parentFullName |> Option.map (fun p -> FullNames.index p i)

    let fieldChildren (fields: ShapeField list) fullName =
        fields |> List.map (fun f -> child f.Name (Some f.Type) (fullName f) (lazy (v.Field f.BackingField)))

    if v.IsNull || isError v then
        None
    else
        match shape v.Type with
        | NotFSharp -> None
        | UnitShape -> Some []
        | FunctionShape ->
            closureFields v.Type
            |> List.map (fun f -> child f.Name (Some f.FieldType) (memberName f.Name) (lazy (v.Field f.Name)))
            |> Some
        | ListShape element ->
            let items, _ = listItems options.MaxChildren v
            items |> List.mapi (fun i item -> child $"[{i}]" (Some element) (indexName i) (Lazy<IValue>.CreateFromValue item)) |> Some
        | ArrayShape element ->
            [ for i in 0 .. v.ArrayLength - 1 -> child $"[{i}]" (Some element) (indexName i) (lazy (v.Element i)) ] |> Some
        | TupleShape _ ->
            let types = tupleTypes v.Type
            let rec memberPath i = if i < 7 then $"Item{i + 1}" else "Rest." + memberPath (i - 7)

            tupleItems v
            |> List.mapi (fun i item -> child $"Item{i + 1}" (List.tryItem i types) (memberName (memberPath i)) (Lazy<IValue>.CreateFromValue item))
            |> Some
        | UnionShape u ->
            match unionCase u v with
            | None -> None
            | Some case ->
                let caseClass = v.Type
                let isCaseClass = caseClass.DefinitionName <> u.Type.DefinitionName

                fieldChildren (unionCaseFields v.Type case) (fun f ->
                    if isCaseClass then
                        parentFullName |> Option.bind (fun p -> FullNames.caseMember p caseClass f.Name)
                    else
                        memberName f.Name)
                |> Some
        | RecordShape _ -> fieldChildren (recordFields v.Type) (fun f -> memberName f.Name) |> Some
        | MapShape ->
            let items, _ = treeItems options.MaxChildren v true
            let args = v.Type.GenericArguments

            items
            |> List.map (fun (k, value) ->
                let key = preview options 1 (Some args[0]) k
                child $"[{key}]" (Some args[1]) None (Lazy<IValue>.CreateFromValue value.Value))
            |> Some
        | SetShape ->
            let items, _ = treeItems options.MaxChildren v false
            let element = v.Type.GenericArguments[0]
            items |> List.mapi (fun i (k, _) -> child $"[{i}]" (Some element) None (Lazy<IValue>.CreateFromValue k)) |> Some

/// Whether an F# value has F# children, without materializing them (for the Expandable flag).
let hasChildren (v: IValue) =
    not v.IsNull
    && not (isError v)
    && match shape v.Type with
       | ListShape _ -> not (v.Field "tail").IsNull
       | ArrayShape _ -> v.ArrayLength > 0
       | TupleShape _ -> true
       | UnionShape u ->
           match unionCase u v with
           | Some case -> not (unionCaseFields v.Type case).IsEmpty
           | None -> false
       | RecordShape _ -> not (recordFields v.Type).IsEmpty
       | FunctionShape -> not (closureFields v.Type).IsEmpty
       | MapShape
       | SetShape -> treeField v |> Option.exists (fun tree -> not tree.IsNull)
       | UnitShape
       | NotFSharp -> false

/// Whether a Raw View row (the C# provider's view with the `raw` specifier) is worth adding.
let hasRawView (v: IValue) =
    not v.IsNull
    && match shape v.Type with
       | UnionShape _
       | RecordShape _
       | FunctionShape
       | MapShape
       | SetShape -> true
       | _ -> false
