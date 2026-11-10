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
        /// Integers in hexadecimal, as the debugger's hex display asks (radix 16).
        Hex: bool
    }

let defaultOptions = { MaxItems = 10; MaxDepth = 3; MaxLength = 250; MaxChildren = 10000; Hex = false }

/// The options for a radix of the debugger's inspection context (10, or 16 for hex display).
let withRadix (radix: uint32) (options: Options) = { options with Hex = (radix = 16u) }

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
        let name = stripArity (v.Type.Name.TrimStart '_')
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

/// Items of an FSharp.Core integral or char range (`{ 1 .. 3 }`, `{ 'a' .. 'c' }`), at most `max`,
/// computed from its `start`, `step` and `stop` fields. None for other sequences, and for ranges
/// whose fields this FSharp.Core version names differently.
let rangeItems max (v: IValue) : (obj list * bool) option =
    let isRange =
        v.Type.Name.StartsWith "Range"
        && v.Type.DeclaringType |> Option.exists (fun d -> d.FullName = "Microsoft.FSharp.Core.Operators+OperatorIntrinsics")

    let read name =
        let f = v.Field name
        if isError f then None else Option.ofObj f.HostValue

    let number (o: obj) =
        match o with
        | :? char as c -> Some(decimal (int c))
        | o ->
            match Type.GetTypeCode(o.GetType()) with
            | code when code >= TypeCode.SByte && code <= TypeCode.UInt64 -> Some(Convert.ToDecimal o)
            | _ -> None

    let item (sample: obj) (n: decimal) : obj =
        match sample with
        | :? char -> box (char (int n))
        | _ -> Convert.ChangeType(n, sample.GetType(), CultureInfo.InvariantCulture)

    if not isRange then
        None
    else
        match read "start", read "stop" with
        | Some start, Some stop ->
            let step = match read "step" with Some s -> number s | None -> Some 1M

            match number start, number stop, step with
            | Some first, Some last, Some step when step <> 0M ->
                let count = if (last - first) * decimal (sign step) < 0M then 0M else floor ((last - first) / step) + 1M
                let shown = int (min count (decimal max))
                Some([ for i in 0 .. shown - 1 -> item start (first + decimal i * step) ], count > decimal max)
            | _ -> None
        | _ -> None

/// The state of a `Lazy<'T>`, read from its fields without forcing it.
type LazyState =
    | Created of IValue
    | NotCreated
    | Faulted

/// The state of a Lazy, or None for a field layout this runtime doesn't have.
let lazyState (v: IValue) =
    let state = v.Field "_state"

    if not (isError state) then
        // .NET Core: `_state` is null once the value is in `_value`. A faulted Lazy keeps its
        // exception in the state.
        if state.IsNull then
            Some(Created(v.Field "_value"))
        else
            let error = state.Field "_exceptionDispatch"
            Some(if not (isError error) && not error.IsNull then Faulted else NotCreated)
    else
        // .NET Framework: `m_boxed` is null until the value is created, then a `Boxed` holding
        // `m_value`, or a `LazyInternalExceptionHolder`.
        let boxed = v.Field "m_boxed"

        if isError boxed then None
        elif boxed.IsNull then Some NotCreated
        else
            let value = boxed.Field "m_value"
            Some(if isError value then Faulted else Created value)

/// Longest `bigint` read from memory, in 32-bit words (about 2,500 decimal digits).
let private maxBigIntWords = 256

/// The value of a `bigint` from its fields, on .NET Core and .NET Framework alike: `_sign` alone
/// for values that fit in an int, otherwise the sign and the magnitude's 32-bit words in `_bits`,
/// least significant first. None for another layout, and for very long values.
let bigintValue (v: IValue) : Numerics.BigInteger option =
    let sign = v.Field "_sign"
    let bits = v.Field "_bits"

    if isError sign || isError bits || isNull sign.HostValue then
        None
    elif bits.IsNull then
        Some(Numerics.BigInteger(Convert.ToInt32 sign.HostValue))
    elif bits.ArrayLength > maxBigIntWords then
        None
    else
        let words = [ for i in 0 .. bits.ArrayLength - 1 -> bits.Element i ]

        if words |> List.exists (fun w -> isError w || isNull w.HostValue) then
            None
        else
            // Little-endian bytes, with a zero byte last so that the magnitude reads as positive.
            let bytes =
                [| for w in words do yield! BitConverter.GetBytes(Convert.ToUInt32 w.HostValue)
                   yield 0uy |]

            let magnitude = Numerics.BigInteger bytes
            Some(if Convert.ToInt32 sign.HostValue < 0 then -magnitude else magnitude)

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

/// An integer in hex as the C# provider shows it beside our rows: `0x` and every digit of its
/// width, two's complement for negative values (`0xffffffff`), and no F# suffix (docs/DESIGN.md §5.3).
let private formatHex (value: obj) =
    let inv = CultureInfo.InvariantCulture

    match value with
    | :? int as x -> Some(x.ToString("x8", inv))
    | :? int64 as x -> Some(x.ToString("x16", inv))
    | :? int16 as x -> Some(x.ToString("x4", inv))
    | :? sbyte as x -> Some(x.ToString("x2", inv))
    | :? byte as x -> Some(x.ToString("x2", inv))
    | :? uint16 as x -> Some(x.ToString("x4", inv))
    | :? uint32 as x -> Some(x.ToString("x8", inv))
    | :? uint64 as x -> Some(x.ToString("x16", inv))
    | :? nativeint as x -> Some((int64 x).ToString("x16", inv))
    | :? unativeint as x -> Some((uint64 x).ToString("x16", inv))
    | _ -> None
    |> Option.map (fun digits -> "0x" + digits)

/// F# literal syntax for primitives (`%A` conventions), or None for other values. With `hex`,
/// integers are in hex; other primitives keep their literals.
let formatPrimitive (hex: bool) (value: obj) =
    let inv = CultureInfo.InvariantCulture

    let hexText = if hex then formatHex value else None

    match value with
    | _ when hexText.IsSome -> hexText
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

/// The F# suffix of an integer literal (`""` for `int`), or None for values that aren't integers.
let private integerSuffix (value: obj) =
    match value with
    | :? int -> Some ""
    | :? int64 -> Some "L"
    | :? int16 -> Some "s"
    | :? sbyte -> Some "y"
    | :? byte -> Some "uy"
    | :? uint16 -> Some "us"
    | :? uint32 -> Some "u"
    | :? uint64 -> Some "UL"
    | :? nativeint -> Some "n"
    | :? unativeint -> Some "un"
    | _ -> None

/// The Value column and the editable value of a primitive row that the C# provider renders: F#
/// literals, as previews print them (docs/DESIGN.md §5.3). The Value column has no integer suffix,
/// since the Type column is beside it; the editable value is the F# literal that an edit compiles
/// (`1L`, `0x0000002aL` with hex display). None for anything but a primitive or a string (null
/// included, which both providers show as `null`; enums, which keep C#'s names).
let tryPrimitiveColumns (hex: bool) (v: IValue) =
    if v.IsNull || isError v || isNull (box v.Type) || v.Type.IsEnum then
        None
    else
        let value = v.HostValue

        match formatPrimitive false value, integerSuffix value with
        | None, _ -> None
        | Some literal, None -> Some(literal, literal)
        | Some literal, Some suffix ->
            match (if hex then formatHex value else None) with
            | Some hexText -> Some(hexText, hexText + suffix)
            | None -> Some(literal.Substring(0, literal.Length - suffix.Length), literal)

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

/// `seq [1; 2; 3]` for an F# sequence. Ranges are computed from their bounds; other sequences
/// would have to run (func-eval) to produce items, so they show as `seq [...]` and the C#
/// Results View enumerates them on request. None for values that are not F# sequences.
let trySeqPreview (options: Options) (v: IValue) =
    if v.IsNull || isError v || (seqType v.Type).IsNone then
        None
    else
        match rangeItems options.MaxItems v with
        | Some(items, truncated) ->
            let text (item: obj) = formatPrimitive options.Hex item |> Option.defaultValue (string item)
            Some("seq [" + join "; " (items |> List.map text |> withMore truncated) + "]")
        | None -> Some "seq [...]"

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
            match formatPrimitive options.Hex v.HostValue, trySeqPreview options v with
            | Some s, _ when not v.Type.IsEnum -> s
            | _, Some s -> s
            | _ -> v.FallbackDisplay()
        | UnitShape -> "()"
        // The closure's compiler-generated class name, e.g. `<fun:f@34>`.
        | FunctionShape -> $"<fun:{v.Type.Name}>"
        | BigIntShape ->
            match bigintValue v with
            | Some n -> n.ToString(CultureInfo.InvariantCulture) + "I"
            | None -> v.FallbackDisplay()
        | LazyShape valueType ->
            // As fsi shows it: the value once created, without a level of nesting of its own.
            match lazyState v with
            | Some(Created value) -> preview options depth (Some valueType) value
            | Some NotCreated -> "<unevaluated>"
            | Some Faulted -> "<faulted>"
            | None -> v.FallbackDisplay()
        | _ when depth >= options.MaxDepth -> "..."
        | ListShape element ->
            let items, truncated = listItems options.MaxItems v
            "[" + join "; " (items |> List.map (recurse (Some element)) |> withMore truncated) + "]"
        | ArrayShape element ->
            let shown = min v.ArrayLength options.MaxItems
            let items = [ for i in 0 .. shown - 1 -> recurse (Some element) (v.Element i) ]
            "[|" + join "; " (withMore (v.ArrayLength > shown) items) + "|]"
        | TupleShape isStruct ->
            // Element types tell a null `()` or `None` from a null reference.
            let types = tupleTypes v.Type
            let elements = tupleItems v |> List.mapi (fun i item -> recurse (List.tryItem i types) item) |> join ", "
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
        elif isError v then
            false
        else
            // A Lazy or bigint whose fields this runtime names differently goes to C#.
            match shape v.Type with
            | NotFSharp -> false
            | LazyShape _ -> (lazyState v).IsSome
            | BigIntShape -> (bigintValue v).IsSome
            | _ -> true

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

let rec private mentionsFSharpCore (t: IType) =
    if t.IsGenericParameter then false
    elif t.IsArray then mentionsFSharpCore t.ElementType
    else
        (not (isNull t.Namespace) && t.Namespace.StartsWith "Microsoft.FSharp")
        || List.exists mentionsFSharpCore t.GenericArguments

let private primitiveTypes =
    set
        [ "System.Int32"; "System.Int64"; "System.Int16"; "System.SByte"; "System.Byte"; "System.UInt16"; "System.UInt32"
          "System.UInt64"; "System.IntPtr"; "System.UIntPtr"; "System.Double"; "System.Single"; "System.Decimal"
          "System.Boolean"; "System.Char"; "System.String" ]

/// A primitive or string type, or an array of them, which C# names `double`, `long[]`, F# `float`,
/// `int64 array`.
let rec private isPrimitiveType (t: IType) =
    if t.IsGenericParameter then false
    elif t.IsArray then isPrimitiveType t.ElementType
    else primitiveTypes.Contains t.FullName

/// F# Type column for a value the C# provider renders, when its type mentions FSharp.Core types,
/// which C# spells `Task<Microsoft.FSharp.Core.Unit>`, or it is a primitive (`float`, not `double`),
/// or an F# sequence (`seq<int>`, not its closure class), or the expression compiler gave its F#
/// type (`fsharpName`, which replaces the declared type's name). Laid out as C# does: the declared
/// type, then the runtime type in braces when it differs (never for `Nullable`, whose values box as
/// the inner type).
let csharpTypeColumn (fsharpName: string option) (declared: IType option) (v: IValue) =
    let isSeq = not v.IsNull && (seqType v.Type).IsSome
    let runtime = if v.IsNull then None else Some(displayType v.Type)
    let declaredName d = fsharpName |> Option.defaultWith (fun () -> typeName d)
    let isFSharpNamed (t: IType) = mentionsFSharpCore t || isPrimitiveType t

    match declared, runtime with
    | Some d, Some r when fsharpName.IsSome || isSeq || isFSharpNamed d || isFSharpNamed r ->
        if d.Native.Equals r.Native || typeName d = typeName r || d.DefinitionName = "System.Nullable`1" then
            Some(declaredName d)
        else
            Some $"{declaredName d} {{{typeName r}}}"
    | Some d, None when fsharpName.IsSome || isFSharpNamed d -> Some(declaredName d)
    | None, _ when fsharpName.IsSome -> fsharpName
    | None, Some r when isSeq || isFSharpNamed r -> Some(typeName r)
    | _ -> None

// ---------------------------------------------------------------------------------------------
// Children and full names

/// Full names for "Add Watch" and re-evaluation, as F# expressions.
module FullNames =

    let private isSimple (expression: string) =
        expression |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '@' || c = '$' || c = '[' || c = ']')

    let private parenthesize (expression: string) =
        if isSimple expression then expression else $"({expression})"

    let memberAccess (parent: string) (name: string) = $"{parenthesize parent}.{fsharpIdent name}"

    let index (parent: string) (i: int) = $"{parenthesize parent}[{i}]"

    /// A field of a union case value. F# code can't name a union case field as a member, so this
    /// is a match on the case that binds the field by position. The case is qualified, so that a
    /// case of the same name in the frame's scope doesn't take its place: by the union's source path
    /// (`Program.Shape.Circle`), and FSharp.Core's by their F# type name (`Result.Ok`).
    let caseField (parent: string) (union: UnionInfo) (case: UnionCase) (index: int) (count: int) =
        let unionPath =
            if union.Type.DefinitionName.StartsWith "Microsoft.FSharp." then
                let name = typeName union.Type
                match name.IndexOf '<' with
                | -1 -> name
                | i -> name.Substring(0, i)
            else
                sourcePathExpression union.Type

        let casePath = $"{unionPath}.{fsharpIdent case.Name}"

        let pattern =
            if count = 1 then
                "x"
            else
                "(" + String.Join(", ", [ for i in 0 .. count - 1 -> if i = index then "x" else "_" ]) + ")"

        let otherCases =
            if union.Cases.Length > 1 then $" | _ -> failwith \"not {case.Name}\"" else ""

        $"(match {parent} with {casePath} {pattern} -> x{otherCases})"

    /// The pattern that binds element `index` of `count` by position: `x`, `(_, x)`.
    let private positional (index: int) (count: int) =
        if count = 1 then
            "x"
        else
            "(" + String.Join(", ", [ for i in 0 .. count - 1 -> if i = index then "x" else "_" ]) + ")"

    /// A field of an F# exception: a match on the exception's pattern, as for union cases, since a
    /// value typed `exn` has no such member. The pattern takes an `exn`, and the value may be typed
    /// as the exception itself (`let e = MyError (1, "a")`), so it is upcast.
    let exceptionField (parent: string) (exceptionType: IType) (index: int) (count: int) =
        let path = sourcePathExpression exceptionType
        $"(match ({parent} :> exn) with {path} {positional index count} -> x | _ -> failwith \"not {exceptionType.Name}\")"

    /// An element of a struct tuple: F# has no `Item1` member on struct tuples, so a pattern binds it.
    let structTupleItem (parent: string) (index: int) (count: int) =
        $"(let struct {positional index count} = {parent} in x)"

    /// The entry of a map by its key, written as a literal: `m["one"]`.
    let mapEntry (parent: string) (key: string) = $"{parenthesize parent}[{key}]"

    /// The `index`th item of a collection in its order (a Map's entries and a Set's items are in key
    /// order, as the tree walk lists them): `Seq.item 1 s`.
    let item (parent: string) (index: int) = $"Seq.item {index} {parenthesize parent}"

type Child =
    {
        Name: string
        /// Read lazily: arrays and long lists are paged.
        Value: System.Lazy<IValue>
        DeclaredType: IType option
        FullName: string option
        /// Whether an edit of the row can compile an assignment to its full name: an array element, or
        /// a field of a record or ref cell (immutable ones too, docs/DESIGN.md §17, item 23). Other
        /// rows are read-only: F# has no assignment to a tuple item, a list item, a map entry, a union
        /// case or exception field, or an anonymous record field (FS0799).
        Editable: bool
    }

let private child name declared fullName value =
    { Name = name; Value = value; DeclaredType = declared; FullName = fullName; Editable = false }

let private editable (children: Child list) =
    children |> List.map (fun c -> { c with Editable = c.FullName.IsSome })

/// Children of an F# value, or None when it is not an F# shape. `parentFullName` is the full name
/// of the value itself, if it has one.
let tryChildren (options: Options) (parentFullName: string option) (v: IValue) : Child list option =
    let memberName name = parentFullName |> Option.map (fun p -> FullNames.memberAccess p name)
    let indexName i = parentFullName |> Option.map (fun p -> FullNames.index p i)

    let fieldChildren (fields: ShapeField list) fullName =
        fields |> List.mapi (fun i f -> child f.Name (Some f.Type) (fullName i f) (lazy (v.Field f.BackingField)))

    if v.IsNull || isError v then
        None
    else
        match shape v.Type with
        | NotFSharp -> None
        | UnitShape -> Some []
        | FunctionShape ->
            // A closure's class is not an F# type, so its captured values have no member to name them
            // by (in the closure's own frame they are variables).
            closureFields v.Type
            |> List.map (fun f -> child f.Name (Some f.FieldType) None (lazy (v.Field f.Name)))
            |> Some
        | ListShape element ->
            let items, _ = listItems options.MaxChildren v
            items |> List.mapi (fun i item -> child $"[{i}]" (Some element) (indexName i) (Lazy<IValue>.CreateFromValue item)) |> Some
        | ArrayShape element ->
            [ for i in 0 .. v.ArrayLength - 1 -> child $"[{i}]" (Some element) (indexName i) (lazy (v.Element i)) ] |> editable |> Some
        | TupleShape isStruct ->
            let types = tupleTypes v.Type
            let items = tupleItems v
            // Items past the seventh are in `Rest`: `v.Rest.Item1`.
            let rec memberPath (parent: string) i =
                if i < 7 then FullNames.memberAccess parent $"Item{i + 1}"
                else memberPath (FullNames.memberAccess parent "Rest") (i - 7)

            let fullName p i =
                if isStruct then FullNames.structTupleItem p i items.Length else memberPath p i

            items
            |> List.mapi (fun i item ->
                child $"Item{i + 1}" (List.tryItem i types) (parentFullName |> Option.map (fun p -> fullName p i)) (Lazy<IValue>.CreateFromValue item))
            |> Some
        | UnionShape u ->
            match unionCase u v with
            | None -> None
            | Some case ->
                let fields = unionCaseFields v.Type case

                // Option and voption have a `Value` member.
                let hasValueMember =
                    u.Type.DefinitionName = "Microsoft.FSharp.Core.FSharpOption`1"
                    || u.Type.DefinitionName = "Microsoft.FSharp.Core.FSharpValueOption`1"

                fieldChildren fields (fun index _ ->
                    if hasValueMember then
                        memberName "Value"
                    else
                        parentFullName |> Option.map (fun p -> FullNames.caseField p u case index fields.Length))
                |> Some
        | RecordShape FSharpException ->
            let fields = recordFields v.Type
            fieldChildren fields (fun i _ -> parentFullName |> Option.map (fun p -> FullNames.exceptionField p v.Type i fields.Length)) |> Some
        | RecordShape AnonymousRecord -> fieldChildren (recordFields v.Type) (fun _ f -> memberName f.Name) |> Some
        | RecordShape _ -> fieldChildren (recordFields v.Type) (fun _ f -> memberName f.Name) |> editable |> Some
        | BigIntShape -> Some []
        | LazyShape valueType ->
            // The value read from its field once created: never `.Value`, which would force it. The
            // Raw View has the rest (the factory, the thread safety mode).
            match lazyState v with
            | Some(Created value) -> Some [ child "Value" (Some valueType) (memberName "Value") (Lazy<IValue>.CreateFromValue value) ]
            | Some NotCreated
            | Some Faulted -> Some []
            | None -> None
        | MapShape ->
            let items, _ = treeItems options.MaxChildren v true
            let args = v.Type.GenericArguments

            // By the key when it is a literal (never in hex: the literal keeps its type's suffix), and
            // by position otherwise (a record key, a NaN that no lookup finds).
            let keyLiteral (k: IValue) =
                if isError k || k.IsNull || k.Type.IsEnum then
                    None
                else
                    match k.HostValue with
                    | :? float as x when Double.IsNaN x -> None
                    | :? float32 as x when Single.IsNaN x -> None
                    | key -> formatPrimitive false key

            items
            |> List.mapi (fun i (k, value) ->
                let key = preview options 1 (Some args[0]) k

                let fullName =
                    parentFullName
                    |> Option.map (fun p ->
                        match keyLiteral k with
                        | Some literal -> FullNames.mapEntry p literal
                        | None -> FullNames.memberAccess (FullNames.item p i) "Value")

                child $"[{key}]" (Some args[1]) fullName (Lazy<IValue>.CreateFromValue value.Value))
            |> Some
        | SetShape ->
            let items, _ = treeItems options.MaxChildren v false
            let element = v.Type.GenericArguments[0]

            items
            |> List.mapi (fun i (k, _) ->
                child $"[{i}]" (Some element) (parentFullName |> Option.map (fun p -> FullNames.item p i)) (Lazy<IValue>.CreateFromValue k))
            |> Some

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
       // The Raw View, at least.
       | LazyShape _ -> true
       | UnitShape
       | BigIntShape
       | NotFSharp -> false

/// Whether a Raw View row (the C# provider's view with the `raw` specifier) is worth adding.
let hasRawView (v: IValue) =
    not v.IsNull
    && match shape v.Type with
       | UnionShape _
       | RecordShape _
       | FunctionShape
       | MapShape
       | SetShape
       | LazyShape _ -> true
       | _ -> false
