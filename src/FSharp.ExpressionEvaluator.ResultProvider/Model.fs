/// The result provider's view of types and values. The debugger exposes types through LMR
/// (Microsoft.VisualStudio.Debugger.Metadata, a hierarchy parallel to System.Reflection) and values
/// through DkmClrValue; tests use System.Reflection and live objects. Both are adapted to these
/// interfaces so the F# shape logic is written and tested once (docs/DESIGN.md §5.2).
namespace FSharp.ExpressionEvaluator

open System.Collections.Generic

/// A custom attribute: full type name and constructor arguments (enums as their underlying value).
type internal AttributeData = { TypeName: string; Args: obj list }

type internal IType =
    /// Null for generic parameters, as in reflection.
    abstract FullName: string
    abstract Name: string
    abstract Namespace: string
    abstract DeclaringType: IType option
    abstract BaseType: IType option
    abstract IsGenericParameter: bool
    /// FullName of the generic type definition, or FullName for non-generic types.
    abstract DefinitionName: string
    abstract GenericArguments: IType list
    abstract IsArray: bool
    abstract ArrayRank: int
    abstract ElementType: IType
    abstract IsValueType: bool
    abstract IsEnum: bool
    abstract Attributes: AttributeData list
    /// Fields declared on this type itself (static and instance).
    abstract Fields: Field list
    /// Properties declared on this type itself (static and instance).
    abstract Properties: Property list
    /// Methods declared on this type itself (static and instance).
    abstract Methods: Method list
    /// The underlying LMR or reflection type, for identity and for creating Concord types.
    abstract Native: obj

and internal Field = { Name: string; FieldType: IType; IsStatic: bool }

and internal Property = { Name: string; PropertyType: IType; Attributes: AttributeData list }

and internal Method = { Name: string; IsStatic: bool; Attributes: AttributeData list }

/// A value in the debuggee (or, in tests, a live object).
type internal IValue =
    /// Runtime type; meaningless when IsNull.
    abstract Type: IType
    abstract IsNull: bool
    /// Error text when the value could not be read, otherwise null.
    abstract Error: string
    /// The value itself for primitives, strings and enums; null otherwise.
    abstract HostValue: obj
    /// An instance field by name, searching base types. Reads memory only (no func-eval).
    abstract Field: name: string -> IValue
    abstract ArrayLength: int
    abstract Element: index: int -> IValue
    /// Display string from the next result provider (C#), used for values F# does not render.
    abstract FallbackDisplay: unit -> string

module internal Types =

    let rec hierarchy (t: IType) =
        seq {
            yield t

            match t.BaseType with
            | Some b -> yield! hierarchy b
            | None -> ()
        }

    let attribute (name: string) (attributes: AttributeData list) =
        attributes |> List.tryFind (fun a -> a.TypeName = name)

    /// Finds an instance field declared on the type or one of its base types.
    let tryField (t: IType) (name: string) =
        hierarchy t
        |> Seq.tryPick (fun t -> t.Fields |> List.tryFind (fun f -> f.Name = name && not f.IsStatic))

    let cache () =
        let d = Dictionary<obj, _>(HashIdentity.Structural)

        fun (t: IType) (create: IType -> 'T) ->
            lock d (fun () ->
                match d.TryGetValue t.Native with
                | true, v -> v
                | _ ->
                    let v = create t
                    d[t.Native] <- v
                    v)

/// FSharp.Core's CompilationMappingAttribute(SourceConstructFlags, [variantNumber], [sequenceNumber]).
module internal CompilationMapping =

    [<Literal>]
    let AttributeName = "Microsoft.FSharp.Core.CompilationMappingAttribute"

    let SumType = 1
    let RecordType = 2
    let ObjectType = 3
    let Field = 4
    let Exception = 5
    let Closure = 6
    let Module = 7
    let UnionCase = 8
    let Value = 9
    let KindMask = 31

    /// The CompilationMapping arguments (kind and the optional numbers) of a member, if any.
    let tryGet (attributes: AttributeData list) =
        Types.attribute AttributeName attributes
        |> Option.map (fun a -> a.Args |> List.map System.Convert.ToInt32)

    let kind (attributes: AttributeData list) =
        match tryGet attributes with
        | Some(flags :: _) -> Some(flags &&& KindMask)
        | _ -> None
