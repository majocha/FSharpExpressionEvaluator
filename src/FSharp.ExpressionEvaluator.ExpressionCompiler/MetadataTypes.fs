/// Types and attributes read from a module's metadata with System.Reflection.Metadata, adapted to
/// IType so FSharpTypes.typeName renders signatures the same way the result provider renders values.
module internal FSharp.ExpressionEvaluator.MetadataTypes

open System
open System.Collections.Immutable
open System.Reflection.Metadata

/// A type as written in a signature.
type SigType =
    /// A named type; `Declaring` is the enclosing type for nested types.
    | Named of TypeRef * args: SigType list
    | ArrayOf of element: SigType * rank: int
    | ByRef of SigType
    | Pointer of SigType
    | GenericParam of name: string

and TypeRef =
    {
        Namespace: string
        Name: string
        Declaring: TypeRef option
        /// Attributes, known only for types defined in the module being read.
        Attributes: AttributeData list
    }

let private named ns name = Named({ Namespace = ns; Name = name; Declaring = None; Attributes = [] }, [])

// ---------------------------------------------------------------------------------------------
// Custom attributes: only the F# ones with simple arguments are decoded.

let private fsharpCore = "Microsoft.FSharp.Core"

let private attributeTypeName (md: MetadataReader) (attribute: CustomAttribute) =
    let ofTypeRef (h: TypeReferenceHandle) =
        let t = md.GetTypeReference h
        md.GetString t.Namespace + "." + md.GetString t.Name

    let ofTypeDef (h: TypeDefinitionHandle) =
        let t = md.GetTypeDefinition h
        md.GetString t.Namespace + "." + md.GetString t.Name

    match attribute.Constructor.Kind with
    | HandleKind.MemberReference ->
        let parent = (md.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)).Parent

        match parent.Kind with
        | HandleKind.TypeReference -> Some(ofTypeRef (TypeReferenceHandle.op_Explicit parent))
        | HandleKind.TypeDefinition -> Some(ofTypeDef (TypeDefinitionHandle.op_Explicit parent))
        | _ -> None
    | HandleKind.MethodDefinition ->
        let m = md.GetMethodDefinition(MethodDefinitionHandle.op_Explicit attribute.Constructor)
        Some(ofTypeDef (m.GetDeclaringType()))
    | _ -> None

/// Decodes attribute arguments; the F# attributes we read take ints, int enums, int arrays or strings.
type private AttributeTypes() =
    interface ICustomAttributeTypeProvider<obj> with
        member _.GetPrimitiveType code = box code
        member _.GetSystemType() = box "System.Type"
        member _.GetSZArrayType element = box ("array", element)
        member _.GetTypeFromDefinition(_, _, _) = box "enum"
        member _.GetTypeFromReference(_, _, _) = box "enum"
        member _.GetTypeFromSerializedName name = box name
        member _.GetUnderlyingEnumType _ = PrimitiveTypeCode.Int32
        member _.IsSystemType t = (t :? string) && (t :?> string) = "System.Type"

let private attributeTypes = AttributeTypes()

let private knownAttributes =
    set
        [ $"{fsharpCore}.CompilationMappingAttribute"
          $"{fsharpCore}.CompilationRepresentationAttribute"
          $"{fsharpCore}.CompilationArgumentCountsAttribute"
          $"{fsharpCore}.CompilationSourceNameAttribute" ]

/// The F# attributes on a metadata entity, with decoded constructor arguments.
let attributes (md: MetadataReader) (handles: CustomAttributeHandleCollection) =
    [ for h in handles do
          let attribute = md.GetCustomAttribute h

          match attributeTypeName md attribute with
          | Some name when knownAttributes.Contains name ->
              let args =
                  try
                      [ for a in attribute.DecodeValue(attributeTypes).FixedArguments ->
                            match a.Value with
                            | :? ImmutableArray<CustomAttributeTypedArgument<obj>> as items -> box [ for i in items -> i.Value ]
                            | v -> v ]
                  with _ ->
                      []

              { TypeName = name; Args = args }
          | _ -> () ]

// ---------------------------------------------------------------------------------------------
// Signatures

/// Generic parameter names in scope: the declaring type's (which include enclosing types') and the method's.
type GenericContext = { TypeParameters: string[]; MethodParameters: string[] }

let rec typeDefRef (md: MetadataReader) (h: TypeDefinitionHandle) =
    let t = md.GetTypeDefinition h
    let declaring = t.GetDeclaringType()

    {
        Namespace = md.GetString t.Namespace
        Name = md.GetString t.Name
        Declaring = if declaring.IsNil then None else Some(typeDefRef md declaring)
        Attributes = attributes md (t.GetCustomAttributes())
    }

let rec private typeRefRef (md: MetadataReader) (h: TypeReferenceHandle) =
    let t = md.GetTypeReference h

    {
        Namespace = md.GetString t.Namespace
        Name = md.GetString t.Name
        Declaring =
            if t.ResolutionScope.Kind = HandleKind.TypeReference then
                Some(typeRefRef md (TypeReferenceHandle.op_Explicit t.ResolutionScope))
            else
                None
        Attributes = []
    }

type private SignatureTypes(md: MetadataReader) =
    interface ISignatureTypeProvider<SigType, GenericContext> with
        member _.GetPrimitiveType code =
            match code with
            | PrimitiveTypeCode.Void -> named "System" "Void"
            | PrimitiveTypeCode.Boolean -> named "System" "Boolean"
            | PrimitiveTypeCode.Char -> named "System" "Char"
            | PrimitiveTypeCode.SByte -> named "System" "SByte"
            | PrimitiveTypeCode.Byte -> named "System" "Byte"
            | PrimitiveTypeCode.Int16 -> named "System" "Int16"
            | PrimitiveTypeCode.UInt16 -> named "System" "UInt16"
            | PrimitiveTypeCode.Int32 -> named "System" "Int32"
            | PrimitiveTypeCode.UInt32 -> named "System" "UInt32"
            | PrimitiveTypeCode.Int64 -> named "System" "Int64"
            | PrimitiveTypeCode.UInt64 -> named "System" "UInt64"
            | PrimitiveTypeCode.Single -> named "System" "Single"
            | PrimitiveTypeCode.Double -> named "System" "Double"
            | PrimitiveTypeCode.String -> named "System" "String"
            | PrimitiveTypeCode.IntPtr -> named "System" "IntPtr"
            | PrimitiveTypeCode.UIntPtr -> named "System" "UIntPtr"
            | PrimitiveTypeCode.TypedReference -> named "System" "TypedReference"
            | _ -> named "System" "Object"

        member _.GetTypeFromDefinition(_, h, _) = Named(typeDefRef md h, [])
        member _.GetTypeFromReference(_, h, _) = Named(typeRefRef md h, [])

        member this.GetTypeFromSpecification(_, context, h, _) =
            (md.GetTypeSpecification h).DecodeSignature(this, context)

        member _.GetGenericInstantiation(generic, args) =
            match generic with
            | Named(t, _) -> Named(t, List.ofSeq args)
            | other -> other

        member _.GetSZArrayType element = ArrayOf(element, 1)
        member _.GetArrayType(element, shape) = ArrayOf(element, shape.Rank)
        member _.GetByReferenceType element = ByRef element
        member _.GetPointerType element = Pointer element

        member _.GetGenericTypeParameter(context, index) =
            GenericParam(if index < context.TypeParameters.Length then context.TypeParameters[index] else $"T{index}")

        member _.GetGenericMethodParameter(context, index) =
            GenericParam(if index < context.MethodParameters.Length then context.MethodParameters[index] else $"M{index}")

        member _.GetFunctionPointerType _ = named "System" "IntPtr"
        member _.GetModifiedType(_, unmodified, _) = unmodified
        member _.GetPinnedType element = element

let signatureTypes md = SignatureTypes md :> ISignatureTypeProvider<SigType, GenericContext>

let genericNames (md: MetadataReader) (handles: GenericParameterHandleCollection) =
    [| for h in handles -> md.GetString (md.GetGenericParameter h).Name |]

/// Replaces generic parameters by the frame's actual type arguments, when known.
let rec substitute (args: Map<string, SigType>) (t: SigType) =
    match t with
    | GenericParam name -> args |> Map.tryFind name |> Option.defaultValue t
    | Named(r, xs) -> Named(r, xs |> List.map (substitute args))
    | ArrayOf(e, rank) -> ArrayOf(substitute args e, rank)
    | ByRef e -> ByRef(substitute args e)
    | Pointer e -> Pointer(substitute args e)

// ---------------------------------------------------------------------------------------------
// IType over signature types, so the result provider's type-name rules apply.

let rec private fullName (r: TypeRef) =
    match r.Declaring with
    | Some d -> fullName d + "+" + r.Name
    | None when String.IsNullOrEmpty r.Namespace -> r.Name
    | None -> r.Namespace + "." + r.Name

type private SigIType(t: SigType) =
    static member Of(t: SigType) : IType =
        match t with
        // F# spells these byref<'T> and nativeptr<'T>.
        | ByRef e -> SigIType(Named({ Namespace = ""; Name = "byref`1"; Declaring = None; Attributes = [] }, [ e ])) :> IType
        | Pointer e -> SigIType(Named({ Namespace = ""; Name = "nativeptr`1"; Declaring = None; Attributes = [] }, [ e ])) :> IType
        | t -> SigIType t :> IType

    interface IType with
        member _.FullName =
            match t with
            | Named(r, _) -> fullName r
            | _ -> null

        member _.Name =
            match t with
            | Named(r, _) -> r.Name
            | GenericParam name -> name
            | _ -> ""

        member _.Namespace =
            match t with
            | Named(r, _) -> r.Namespace
            | _ -> ""

        member _.DeclaringType =
            match t with
            | Named({ Declaring = Some d }, _) -> Some(SigIType.Of(Named(d, [])))
            | _ -> None

        member _.BaseType = None

        member _.IsGenericParameter =
            match t with
            | GenericParam _ -> true
            | _ -> false

        member _.DefinitionName =
            match t with
            | Named(r, _) -> fullName r
            | _ -> null

        member _.GenericArguments =
            match t with
            | Named(_, args) -> args |> List.map SigIType.Of
            | _ -> []

        member _.IsArray =
            match t with
            | ArrayOf _ -> true
            | _ -> false

        member _.ArrayRank =
            match t with
            | ArrayOf(_, rank) -> rank
            | _ -> 0

        member _.ElementType =
            match t with
            | ArrayOf(e, _) -> SigIType.Of e
            | _ -> Unchecked.defaultof<_>

        member _.IsValueType = false
        member _.IsEnum = false

        member _.Attributes =
            match t with
            | Named(r, _) -> r.Attributes
            | _ -> []

        member _.Fields = []
        member _.Properties = []
        member _.Methods = []
        member _.Native = box t

/// The F# name of a signature type: `int list`, `int -> int`, `byref<int>`.
let typeName (t: SigType) = FSharpTypes.typeName (SigIType.Of t)

// ---------------------------------------------------------------------------------------------
// Serialized type names, as Concord reports a frame's generic arguments.

/// Parses a reflection type name such as
/// `System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib, ...]]` or `System.Int32[]`.
let parseTypeName (text: string) : SigType option =
    let mutable i = 0

    let peek () = if i < text.Length then text[i] else '\000'

    let rec parseType () : SigType =
        let start = i

        while i < text.Length && not ("[],".Contains(string text[i])) do
            i <- i + 1

        let name = text.Substring(start, i - start).Trim()
        let parts = name.Split '+'

        let rec build (ns: string) (names: string list) declaring =
            match names with
            | [] -> declaring |> Option.get
            | n :: rest -> build ns rest (Some { Namespace = (if declaring.IsNone then ns else ""); Name = n; Declaring = declaring; Attributes = [] })

        let outer = parts[0]
        let dot = outer.LastIndexOf '.'
        let ns, first = if dot < 0 then "", outer else outer.Substring(0, dot), outer.Substring(dot + 1)
        let typeRef = build ns (first :: List.ofArray parts[1..]) None

        // Generic arguments: [[arg, assembly], [arg, assembly]]
        let args =
            if peek () = '[' && i + 1 < text.Length && text[i + 1] = '[' then
                i <- i + 1
                let args = Collections.Generic.List<SigType>()

                while peek () = '[' do
                    i <- i + 1
                    args.Add(parseType ())
                    skipAssembly ()
                    if peek () = ']' then i <- i + 1
                    if peek () = ',' then i <- i + 1

                if peek () = ']' then i <- i + 1
                List.ofSeq args
            else
                []

        let mutable t = Named(typeRef, args)

        // Array suffixes: [] or [,]
        while peek () = '[' && i + 1 < text.Length && (text[i + 1] = ']' || text[i + 1] = ',') do
            let close = text.IndexOf(']', i)
            let rank = close - i
            t <- ArrayOf(t, rank)
            i <- close + 1

        t

    and skipAssembly () =
        // Skips ", Assembly, Version=..., ..." up to the closing ']' of a generic argument.
        let mutable depth = 0

        while i < text.Length && not (text[i] = ']' && depth = 0) do
            if text[i] = '[' then depth <- depth + 1
            elif text[i] = ']' then depth <- depth - 1
            i <- i + 1

    try
        Some(parseType ())
    with _ ->
        None
