/// F# names for call stack frames and breakpoint locations (docs/DESIGN.md §7), from metadata only.
module internal FSharp.ExpressionEvaluator.MethodNames

open System
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Text
open FSharp.ExpressionEvaluator.MetadataTypes

/// What to show for each argument, as requested through DkmVariableInfoFlags.
type ArgumentFormat =
    {
        Names: bool
        Types: bool
        /// Formatted argument values in parameter order, when requested and available.
        Values: string option list option
    }

let noArguments = { Names = false; Types = false; Values = None }

// ---------------------------------------------------------------------------------------------
// Operator names (FSharp.Compiler PrettyNaming conventions)

let private operators =
    dict
        [ "op_Addition", "+"
          "op_Subtraction", "-"
          "op_Multiply", "*"
          "op_Division", "/"
          "op_Modulus", "%"
          "op_Exponentiation", "**"
          "op_Append", "@"
          "op_Concatenate", "^"
          "op_Equality", "="
          "op_Inequality", "<>"
          "op_LessThan", "<"
          "op_GreaterThan", ">"
          "op_LessThanOrEqual", "<="
          "op_GreaterThanOrEqual", ">="
          "op_PipeRight", "|>"
          "op_PipeRight2", "||>"
          "op_PipeRight3", "|||>"
          "op_PipeLeft", "<|"
          "op_ComposeRight", ">>"
          "op_ComposeLeft", "<<"
          "op_UnaryNegation", "~-"
          "op_UnaryPlus", "~+"
          "op_BooleanAnd", "&&"
          "op_BooleanOr", "||"
          "op_BitwiseAnd", "&&&"
          "op_BitwiseOr", "|||"
          "op_ExclusiveOr", "^^^"
          "op_LeftShift", "<<<"
          "op_RightShift", ">>>"
          "op_LogicalNot", "~~~"
          "op_Dereference", "!"
          "op_ColonEquals", ":="
          "op_Range", ".."
          "op_RangeStep", ".. .." ]

/// Symbolic operators defined in F# compile to op_ plus one word per character, e.g. op_PlusDot.
let private operatorChars =
    [ "Greater", ">"; "Less", "<"; "Plus", "+"; "Minus", "-"; "Multiply", "*"; "Equals", "="; "Twiddle", "~"
      "Percent", "%"; "Dot", "."; "Dollar", "$"; "Amp", "&"; "Bar", "|"; "At", "@"; "Hash", "#"; "Hat", "^"
      "Bang", "!"; "Qmark", "?"; "Divide", "/"; "Colon", ":"; "LParen", "("; "Comma", ","; "RParen", ")"
      "LBrack", "["; "RBrack", "]" ]

let operatorName (name: string) =
    match operators.TryGetValue name with
    | true, op -> Some op
    | _ when name.StartsWith "op_" ->
        let rec decode (rest: string) acc =
            if rest = "" then
                Some(String.concat "" (List.rev acc))
            else
                operatorChars
                |> List.tryFind (fun (word, _) -> rest.StartsWith word)
                |> Option.bind (fun (word, op) -> decode (rest.Substring word.Length) (op :: acc))

        decode (name.Substring 3) []
    | _ -> None

// ---------------------------------------------------------------------------------------------
// Frame names

let private stripArity (name: string) =
    match name.IndexOf '`' with
    | -1 -> name
    | i -> name.Substring(0, i)

let private representationFlags (attributes: AttributeData list) =
    match Types.attribute "Microsoft.FSharp.Core.CompilationRepresentationAttribute" attributes with
    | Some { Args = flags :: _ } -> Convert.ToInt32 flags
    | _ -> 0

/// `Ns.Module.Type<'T>` for a type definition, with generic arguments when the frame supplies them.
let rec private qualifiedTypeName (md: MetadataReader) (h: TypeDefinitionHandle) (args: Map<string, SigType>) =
    let t = md.GetTypeDefinition h
    let declaring = t.GetDeclaringType()
    let name = md.GetString t.Name
    let attributes = attributes md (t.GetCustomAttributes())

    let display =
        let n = stripArity name
        // Modules compiled with ModuleSuffix are named `XModule`.
        if representationFlags attributes &&& 4 <> 0 && n.EndsWith "Module" then n.Substring(0, n.Length - 6) else n

    // CLR nested types redeclare the enclosing type's generic parameters first; show only their own.
    let allParameters = genericNames md (t.GetGenericParameters())
    let outerCount = if declaring.IsNil then 0 else (md.GetTypeDefinition declaring).GetGenericParameters().Count
    let own = allParameters |> Array.skip (min outerCount allParameters.Length)

    let generics =
        if own.Length = 0 then
            ""
        else
            let shown = own |> Array.map (fun p -> args |> Map.tryFind p |> Option.map typeName |> Option.defaultValue ("'" + p))
            "<" + String.Join(", ", shown) + ">"

    let prefix =
        if not declaring.IsNil then qualifiedTypeName md declaring args + "."
        else
            match md.GetString t.Namespace with
            | "" -> ""
            | ns -> ns + "."

    prefix + display + generics

let private isClosure (md: MetadataReader) (t: TypeDefinition) =
    // Closures are compiler-generated `name@line` classes deriving from (OptimizedClosures.)FSharpFunc.
    let name = md.GetString t.Name
    name.Contains "@" && not t.BaseType.IsNil

let private implementsAsyncStateMachine (md: MetadataReader) (t: TypeDefinition) =
    t.GetInterfaceImplementations()
    |> Seq.exists (fun h ->
        let i = (md.GetInterfaceImplementation h).Interface

        i.Kind = HandleKind.TypeReference
        && md.GetString (md.GetTypeReference(TypeReferenceHandle.op_Explicit i)).Name = "IAsyncStateMachine")

let private baseTypeName (md: MetadataReader) (t: TypeDefinition) =
    match t.BaseType.Kind with
    | HandleKind.TypeReference -> md.GetString (md.GetTypeReference(TypeReferenceHandle.op_Explicit t.BaseType)).Name
    | HandleKind.TypeSpecification ->
        let blob = md.GetBlobReader((md.GetTypeSpecification(TypeSpecificationHandle.op_Explicit t.BaseType)).Signature)
        // GENERICINST (CLASS|VALUETYPE) TypeDefOrRef ...
        if blob.ReadSignatureTypeCode() = SignatureTypeCode.GenericTypeInstance then
            blob.ReadSignatureTypeCode() |> ignore
            let h = blob.ReadTypeHandle()
            if h.Kind = HandleKind.TypeReference then md.GetString (md.GetTypeReference(TypeReferenceHandle.op_Explicit h)).Name else ""
        else
            ""
    | _ -> ""

/// The member part of a frame name, or None when the method name says it all (state machines).
let private memberName (md: MetadataReader) (m: MethodDefinition) (declaring: TypeDefinition) =
    let name = md.GetString m.Name
    let special = m.Attributes.HasFlag MethodAttributes.SpecialName

    let sourceName =
        match Types.attribute "Microsoft.FSharp.Core.CompilationSourceNameAttribute" (attributes md (m.GetCustomAttributes())) with
        | Some { Args = [ :? string as n ] } -> Some n
        | _ -> None

    match name with
    | ".ctor" -> "new"
    | ".cctor" -> "(static initializer)"
    | _ when special && (name.StartsWith "get_" || name.StartsWith "set_") ->
        let kind = if name.StartsWith "get_" then "get" else "set"
        $"{name.Substring 4} ({kind})"
    | _ ->
        match sourceName, operatorName name with
        | Some n, _ -> n
        | None, Some op -> $"({op})"
        | None, None -> name

type private Parameter = { Name: string; Type: SigType }

let private formatArgument (format: ArgumentFormat) (p: Parameter) (value: string option) =
    let text =
        match format.Names, format.Types with
        | true, true -> $"{p.Name}: {typeName p.Type}"
        | true, false -> p.Name
        | false, true -> typeName p.Type
        | false, false -> ""

    match value with
    | Some v when text = "" -> v
    | Some v -> $"{text} = {v}"
    | None -> text

let private isUnit (t: SigType) =
    match t with
    | Named({ Namespace = "Microsoft.FSharp.Core"; Name = "Unit" }, _) -> true
    | _ -> false

/// A curried group whose IL parameters are `p_0`, `p_1`, ... is one tuple parameter `p` in source.
let private tupleParameter (group: Parameter list) =
    match group with
    | first :: _ :: _ when first.Name.EndsWith "_0" ->
        let prefix = first.Name.Substring(0, first.Name.Length - 2)

        if group |> List.mapi (fun i p -> p.Name = $"{prefix}_{i}") |> List.forall id then
            let tupleType =
                Named({ Namespace = "System"; Name = $"Tuple`{group.Length}"; Declaring = None; Attributes = [] }, group |> List.map _.Type)

            Some { Name = prefix; Type = tupleType }
        else
            None
    | _ -> None

/// Curried groups `(x: int) (y: int)` or one tupled group `(a: int, b: string)`.
let private formatArguments (format: ArgumentFormat) (groups: int list) (parameters: Parameter list) =
    let values = format.Values |> Option.map Array.ofList

    let valueAt i =
        values |> Option.bind (fun vs -> if i < vs.Length then vs[i] else None)

    if not format.Names && not format.Types && format.Values.IsNone then
        ""
    elif parameters.IsEmpty then
        " ()"
    else
        let sb = StringBuilder()
        let mutable index = 0

        for size in groups do
            let group = parameters[index .. index + size - 1]

            let items =
                match tupleParameter group with
                | Some tuple ->
                    let value =
                        if format.Values.IsSome then
                            let parts = [ for i in index .. index + size - 1 -> defaultArg (valueAt i) "?" ]
                            Some("(" + String.Join(", ", parts) + ")")
                        else
                            None

                    [ formatArgument format tuple value ]
                | None -> [ for i in index .. index + size - 1 -> formatArgument format parameters[i] (valueAt i) ]

            // A unit argument is written `()` in F#, whatever its compiled name (`_arg1`, `unitVar0`).
            let items = if size = 1 && isUnit group.Head.Type then [ "" ] else items

            index <- index + size
            // A lone argument shown only by name or value needs no parentheses: `add x y`, `add 1 2`.
            let bare = items.Length = 1 && not format.Types && not (format.Names && format.Values.IsSome)
            sb.Append(if bare && items.Head <> "" then " " + items.Head else " (" + String.Join(", ", items) + ")") |> ignore

        sb.ToString()

/// How a method's parameters group into curried arguments.
let private argumentGroups (md: MetadataReader) (m: MethodDefinition) (declaring: TypeDefinition) parameterCount =
    let counts =
        match Types.attribute "Microsoft.FSharp.Core.CompilationArgumentCountsAttribute" (attributes md (m.GetCustomAttributes())) with
        | Some { Args = [ :? (obj list) as counts ] } -> Some(counts |> List.map Convert.ToInt32)
        | _ -> None

    match counts with
    | Some counts when List.sum counts = parameterCount -> counts
    | _ when isClosure md declaring && md.GetString m.Name = "Invoke" ->
        // FSharpFunc<_,_>.Invoke takes one argument; OptimizedClosures.FSharpFunc<_,_,_>.Invoke takes them curried.
        List.replicate parameterCount 1
    | _ -> [ parameterCount ]

/// The frame name of a method, F#-style. `typeArguments` are the frame's actual generic arguments
/// (type parameters first, then method parameters), when Concord provides them.
let frameName (md: MetadataReader) (methodToken: int) (format: ArgumentFormat) (typeArguments: SigType list option) =
    let handle = MetadataTokens.MethodDefinitionHandle(methodToken &&& 0xFFFFFF)
    let m = md.GetMethodDefinition handle
    let declaringHandle = m.GetDeclaringType()
    let declaring = md.GetTypeDefinition declaringHandle
    let typeParameters = genericNames md (declaring.GetGenericParameters())
    let methodParameters = genericNames md (m.GetGenericParameters())

    let substitution =
        match typeArguments with
        | Some args when args.Length = typeParameters.Length + methodParameters.Length ->
            Seq.zip (Array.append typeParameters methodParameters) args |> Map.ofSeq
        | _ -> Map.empty

    let typeName = qualifiedTypeName md declaringHandle substitution
    let name = md.GetString m.Name
    let isStateMachine = name = "MoveNext" && implementsAsyncStateMachine md declaring
    let isSequence = name = "GenerateNext" && baseTypeName md declaring = "GeneratedSequenceBase`1"

    if isStateMachine then
        $"{typeName} (state machine)"
    elif isSequence then
        $"{typeName} (seq)"
    else
        let signature = m.DecodeSignature(signatureTypes md, { TypeParameters = typeParameters; MethodParameters = methodParameters })

        let names =
            [ for h in m.GetParameters() do
                  let p = md.GetParameter h
                  if p.SequenceNumber > 0 then p.SequenceNumber, md.GetString p.Name ]
            |> Map.ofList

        let parameters =
            signature.ParameterTypes
            |> Seq.mapi (fun i t ->
                { Name = names |> Map.tryFind (i + 1) |> Option.defaultValue $"arg{i}"; Type = substitute substitution t })
            |> List.ofSeq

        let generics =
            if methodParameters.Length = 0 then
                ""
            else
                let shown =
                    methodParameters
                    |> Array.map (fun p -> substitution |> Map.tryFind p |> Option.map MetadataTypes.typeName |> Option.defaultValue ("'" + p))

                "<" + String.Join(", ", shown) + ">"

        let groups = argumentGroups md m declaring parameters.Length

        // Closures: `Program.f@66 (x: int)` rather than `Program.f@66.Invoke(...)`.
        let member' =
            if isClosure md declaring && name = "Invoke" then "" else "." + memberName md m declaring + generics

        typeName + member' + formatArguments format groups parameters

/// The F# name of a method's return type (`unit` for void).
let returnTypeName (md: MetadataReader) (methodToken: int) (typeArguments: SigType list option) =
    let m = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(methodToken &&& 0xFFFFFF))
    let declaring = md.GetTypeDefinition(m.GetDeclaringType())
    let typeParameters = genericNames md (declaring.GetGenericParameters())
    let methodParameters = genericNames md (m.GetGenericParameters())

    let substitution =
        match typeArguments with
        | Some args when args.Length = typeParameters.Length + methodParameters.Length ->
            Seq.zip (Array.append typeParameters methodParameters) args |> Map.ofSeq
        | _ -> Map.empty

    let signature = m.DecodeSignature(signatureTypes md, { TypeParameters = typeParameters; MethodParameters = methodParameters })
    MetadataTypes.typeName (substitute substitution signature.ReturnType)
