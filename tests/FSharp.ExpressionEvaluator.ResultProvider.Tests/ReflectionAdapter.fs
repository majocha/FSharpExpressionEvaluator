/// IType / IValue over System.Reflection and live objects, standing in for LMR and DkmClrValue.
module internal FSharp.ExpressionEvaluator.Tests.ReflectionAdapter

open System
open System.Collections.Concurrent
open System.Reflection
open FSharp.ExpressionEvaluator

let private declared =
    BindingFlags.DeclaredOnly ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic

let private attributes (data: Collections.Generic.IList<CustomAttributeData>) =
    [ for a in data -> { TypeName = a.AttributeType.FullName; Args = [ for x in a.ConstructorArguments -> x.Value ] } ]

type ReflectionType private (t: Type) =
    static let cache = ConcurrentDictionary<Type, ReflectionType>()

    static member Of(t: Type) : IType = cache.GetOrAdd(t, fun t -> ReflectionType t) :> IType

    interface IType with
        member _.FullName = t.FullName
        member _.Name = t.Name
        member _.Namespace = t.Namespace
        member _.DeclaringType = if t.IsGenericParameter then None else Option.ofObj t.DeclaringType |> Option.map ReflectionType.Of
        member _.BaseType = Option.ofObj t.BaseType |> Option.map ReflectionType.Of
        member _.IsGenericParameter = t.IsGenericParameter
        member _.DefinitionName = if t.IsGenericType then t.GetGenericTypeDefinition().FullName else t.FullName
        member _.GenericArguments = t.GetGenericArguments() |> Array.map ReflectionType.Of |> List.ofArray
        member _.IsArray = t.IsArray
        member _.ArrayRank = if t.IsArray then t.GetArrayRank() else 0
        member _.ElementType = if t.HasElementType then ReflectionType.Of(t.GetElementType()) else Unchecked.defaultof<_>
        member _.IsValueType = t.IsValueType
        member _.IsEnum = t.IsEnum
        member _.Attributes = attributes (t.GetCustomAttributesData())
        member _.Fields = [ for f in t.GetFields declared -> { Name = f.Name; FieldType = ReflectionType.Of f.FieldType; IsStatic = f.IsStatic } ]

        member _.Properties =
            [ for p in t.GetProperties declared ->
                  { Name = p.Name; PropertyType = ReflectionType.Of p.PropertyType; Attributes = attributes (p.GetCustomAttributesData()) } ]

        member _.Methods =
            [ for m in t.GetMethods declared -> { Name = m.Name; IsStatic = m.IsStatic; Attributes = attributes (m.GetCustomAttributesData()) } ]

        member _.Native = box t

let rec private findField (t: Type) name =
    match t with
    | null -> None
    | t ->
        match t.GetField(name, declared) with
        | null -> findField t.BaseType name
        | f -> Some f

/// A live object as an IValue. Fallback display mimics the C# provider's `{Type}` for objects.
type LiveValue(o: obj) =
    static member Error message =
        { new IValue with
            member _.Type = Unchecked.defaultof<_>
            member _.IsNull = false
            member _.Error = message
            member _.HostValue = null
            member _.Field _ = failwith "error value"
            member _.ArrayLength = 0
            member _.Element _ = failwith "error value"
            member _.FallbackDisplay() = message }

    interface IValue with
        member _.Type = if isNull o then Unchecked.defaultof<_> else ReflectionType.Of(o.GetType())
        member _.IsNull = isNull o
        member _.Error = null

        member _.HostValue =
            let isHostValue (t: Type) = t.IsPrimitive || t.IsEnum || t = typeof<string> || t = typeof<decimal>
            if not (isNull o) && isHostValue (o.GetType()) then o else null

        member _.Field name =
            match findField (o.GetType()) name with
            | Some f -> LiveValue(f.GetValue o) :> IValue
            | None -> LiveValue.Error $"no field '{name}'"

        member _.ArrayLength = (o :?> Array).Length
        member _.Element i = LiveValue((o :?> Array).GetValue i) :> IValue
        member _.FallbackDisplay() = $"{{{o.GetType().FullName}}}"

let live (o: obj) = LiveValue o :> IValue
let typeOf<'T> = ReflectionType.Of typeof<'T>
