// Lists what the expression evaluator uses from the private F# compiler that FCS doesn't make public:
// the types and members FSharp.ExpressionEvaluator.ExpressionCompiler.dll references in
// FSharp.ExpressionEvaluator.Compiler.dll, with their visibility there. Inline functions are compiled
// into the evaluator, so only what they call shows up.
//
//   dotnet fsi eng/List-CompilerInternals.fsx [evaluator.dll] [compiler.dll] > docs/FCS-INTERNALS.md

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let args = fsi.CommandLineArgs |> Array.skip 1

let evaluatorPath =
    if args.Length > 0 then args[0]
    else Path.Combine(root, @"src\FSharp.ExpressionEvaluator.ExpressionCompiler\bin\Debug\net48\FSharp.ExpressionEvaluator.ExpressionCompiler.dll")

let compilerPath =
    if args.Length > 1 then args[1] else Path.Combine(root, @"artifacts\compiler\FSharp.ExpressionEvaluator.Compiler.dll")

let compilerName = "FSharp.ExpressionEvaluator.Compiler"

let openMetadata (path: string) =
    let pe = new PEReader(File.OpenRead path)
    pe.GetMetadataReader()

let ev = openMetadata evaluatorPath
let fcs = openMetadata compilerPath

// The compiler's types by full name (nested types as `Outer+Inner`), with whether they are visible
// outside the assembly (public, and nested in public types).
let compilerTypes =
    let rec name (h: TypeDefinitionHandle) =
        let td = fcs.GetTypeDefinition h
        let declaring = td.GetDeclaringType()

        if declaring.IsNil then
            let ns = fcs.GetString td.Namespace
            let n = fcs.GetString td.Name
            if ns = "" then n else ns + "." + n
        else
            name declaring + "+" + fcs.GetString td.Name

    let rec isVisible (h: TypeDefinitionHandle) =
        let td = fcs.GetTypeDefinition h
        let visibility = td.Attributes &&& TypeAttributes.VisibilityMask
        let declaring = td.GetDeclaringType()

        if declaring.IsNil then
            visibility = TypeAttributes.Public
        else
            visibility = TypeAttributes.NestedPublic && isVisible declaring

    fcs.TypeDefinitions |> Seq.map (fun h -> name h, (h, isVisible h)) |> dict

// Type references of the evaluator into the compiler, by handle, with their full names.
let referencedTypes =
    let rec scopeAndName (h: TypeReferenceHandle) =
        let tr = ev.GetTypeReference h
        let n = ev.GetString tr.Name

        match tr.ResolutionScope.Kind with
        | HandleKind.TypeReference ->
            let scope, outer = scopeAndName (TypeReferenceHandle.op_Explicit tr.ResolutionScope)
            scope, outer + "+" + n
        | HandleKind.AssemblyReference ->
            let a = ev.GetAssemblyReference(AssemblyReferenceHandle.op_Explicit tr.ResolutionScope)
            let ns = ev.GetString tr.Namespace
            ev.GetString a.Name, (if ns = "" then n else ns + "." + n)
        | _ -> "", n

    ev.TypeReferences
    |> Seq.choose (fun h ->
        match scopeAndName h with
        | scope, name when scope = compilerName -> Some(h, name)
        | _ -> None)
    |> dict

// The generic type a TypeSpec instantiates (its first type reference), if any.
let typeSpecHead (h: TypeSpecificationHandle) =
    let blob = ev.GetBlobReader((ev.GetTypeSpecification h).Signature)

    match blob.ReadSignatureTypeCode() with
    | SignatureTypeCode.GenericTypeInstance ->
        blob.ReadCompressedInteger() |> ignore
        let handle = blob.ReadTypeHandle()

        if handle.Kind = HandleKind.TypeReference then
            Some(TypeReferenceHandle.op_Explicit handle)
        else
            None
    | _ -> None

// Members of compiler types the evaluator references: type full name -> member names.
let referencedMembers =
    ev.MemberReferences
    |> Seq.choose (fun h ->
        let mr = ev.GetMemberReference h

        let parent =
            match mr.Parent.Kind with
            | HandleKind.TypeReference -> Some(TypeReferenceHandle.op_Explicit mr.Parent)
            | HandleKind.TypeSpecification -> typeSpecHead (TypeSpecificationHandle.op_Explicit mr.Parent)
            | _ -> None

        parent
        |> Option.bind (fun p ->
            match referencedTypes.TryGetValue p with
            | true, typeName -> Some(typeName, ev.GetString mr.Name, mr.GetKind())
            | _ -> None))
    |> Seq.distinct
    |> Seq.toList

let isPublicMember (typeName: string) (name: string) (kind: MemberReferenceKind) =
    match compilerTypes.TryGetValue typeName with
    | true, (h, _) ->
        let td = fcs.GetTypeDefinition h

        if kind = MemberReferenceKind.Field then
            td.GetFields()
            |> Seq.map fcs.GetFieldDefinition
            |> Seq.filter (fun f -> fcs.GetString f.Name = name)
            |> Seq.exists (fun f -> f.Attributes &&& FieldAttributes.FieldAccessMask = FieldAttributes.Public)
        else
            td.GetMethods()
            |> Seq.map fcs.GetMethodDefinition
            |> Seq.filter (fun m -> fcs.GetString m.Name = name)
            |> Seq.exists (fun m -> m.Attributes &&& MethodAttributes.MemberAccessMask = MethodAttributes.Public)
    | _ -> false

// The type a compiler type derives from, by full name, when it is a type of the compiler too (also
// through a generic instantiation): a union case's class derives from its union.
let baseTypeName =
    let names = compilerTypes |> Seq.map (fun kv -> fst kv.Value, kv.Key) |> dict

    fun (typeName: string) ->
        match compilerTypes.TryGetValue typeName with
        | true, (h, _) ->
            let baseType = (fcs.GetTypeDefinition h).BaseType

            let definition =
                match baseType.Kind with
                | HandleKind.TypeDefinition -> Some(TypeDefinitionHandle.op_Explicit baseType)
                | HandleKind.TypeSpecification ->
                    let blob = fcs.GetBlobReader((fcs.GetTypeSpecification(TypeSpecificationHandle.op_Explicit baseType)).Signature)

                    if blob.ReadSignatureTypeCode() = SignatureTypeCode.GenericTypeInstance then
                        blob.ReadCompressedInteger() |> ignore
                        let head = blob.ReadTypeHandle()
                        if head.Kind = HandleKind.TypeDefinition then Some(TypeDefinitionHandle.op_Explicit head) else None
                    else
                        None
                | _ -> None

            definition |> Option.bind (fun d -> match names.TryGetValue d with | true, n -> Some n | _ -> None)
        | _ -> None

// A union case's class: nested in its union, and derived from it.
let isUnionCase (typeName: string) =
    let i = typeName.LastIndexOf '+'
    i > 0 && baseTypeName typeName = Some(typeName.Substring(0, i))

// Members F# generates for a union's cases, shown as the cases instead.
let isCaseMember (name: string) =
    name.StartsWith "New" || name.StartsWith "get_Is" || name = "get_Tag" || name.StartsWith "Tags"

let memberName (name: string) =
    if name.StartsWith "get_" then name.Substring 4 + " (property)"
    elif name.StartsWith "set_" then name.Substring 4 + " (setter)"
    elif name = ".ctor" then "(constructor)"
    else name

let rows =
    referencedTypes.Values
    |> Seq.distinct
    |> Seq.sort
    |> Seq.map (fun typeName ->
        let typeVisible =
            match compilerTypes.TryGetValue typeName with
            | true, (_, visible) -> visible
            | _ -> false

        let members =
            referencedMembers
            |> List.filter (fun (t, _, _) -> t = typeName)
            |> List.map (fun (_, name, kind) -> name, isPublicMember typeName name kind)
            |> List.distinctBy fst
            |> List.sortBy fst

        typeName, typeVisible, members)
    |> Seq.toList

let isInternal (_, typeVisible, members) =
    not typeVisible || members |> List.exists (fun (_, isPublic) -> not isPublic)

let internalRows = rows |> List.filter isInternal

// The F# module (or top-level type) a type belongs to: modules compile to classes, and their types
// to nested classes (`FSharp.Compiler.CheckBasics+TcEnv`).
let moduleOf (typeName: string) = typeName.Split('+').[0]

let internalModules = internalRows |> List.map (fun (t, _, _) -> moduleOf t) |> List.distinct

printfn "# FCS internals the evaluator uses"
printfn ""
printfn "Generated by `eng/List-CompilerInternals.fsx` from `%s` against `%s`." (Path.GetFileName evaluatorPath) (Path.GetFileName compilerPath)
printfn ""
printfn "What `Evaluation.fs` uses of the F# compiler that FCS does not make public, by F# module (a"
printfn "module compiles to a class, and its types to nested classes). *internal* marks a module or"
printfn "type that is not visible at all; otherwise only the members listed are internal. Inline"
printfn "functions are compiled into the evaluator, so only what they call appears. A union's cases"
printfn "are listed with it; record fields and properties show as `(property)`, mutable ones also as"
printfn "`(setter)`, a module's let-bound values as properties too."
printfn ""
printfn "%d modules; %d of the %d compiler types referenced need internal access (the rest are used only" internalModules.Length internalRows.Length rows.Length
printfn "through public members)."
printfn ""

for moduleName, moduleRows in internalRows |> List.groupBy (fun (t, _, _) -> moduleOf t) |> List.sortBy fst do
    let moduleVisible =
        match compilerTypes.TryGetValue moduleName with
        | true, (_, visible) -> visible
        | _ -> false

    printfn "## `%s`%s" moduleName (if moduleVisible then "" else " *(internal)*")
    printfn ""

    let cases =
        moduleRows
        |> List.choose (fun (t, _, _) -> if isUnionCase t then Some t else None)
        |> List.groupBy (fun t -> t.Substring(0, t.LastIndexOf '+'))
        |> dict

    for typeName, typeVisible, members in moduleRows do
        if not (isUnionCase typeName) then
            let typeCases =
                match cases.TryGetValue typeName with
                | true, cs -> cs |> List.map (fun c -> c.Substring(c.LastIndexOf '+' + 1))
                | _ -> []

            let internalMembers =
                members
                |> List.filter (fun (name, isPublic) -> (not isPublic || not typeVisible) && not (typeCases <> [] && isCaseMember name))
                |> List.map (fst >> memberName >> sprintf "`%s`")

            let parts =
                [
                    if not typeCases.IsEmpty then
                        "cases " + (typeCases |> List.map (sprintf "`%s`") |> String.concat ", ")
                    if not internalMembers.IsEmpty then
                        String.concat ", " internalMembers
                ]

            let label =
                if typeName = moduleName then "module functions and values"
                else sprintf "`%s`" (typeName.Substring(moduleName.Length + 1).Replace("+", "."))

            let visibility = if typeVisible || typeName = moduleName then "" else " *(internal)*"

            match parts with
            // A type of its own, not a module, referenced but none of its members.
            | [] when typeName = moduleName && moduleRows.Length = 1 -> printfn "- the type itself"
            // The module only encloses the types below.
            | [] when typeName = moduleName -> ()
            | [] -> printfn "- %s%s" label visibility
            | _ -> printfn "- %s%s: %s" label visibility (String.concat "; " parts)

    printfn ""
