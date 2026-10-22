/// IType over LMR (Microsoft.VisualStudio.Debugger.Metadata) and IValue over DkmClrValue.
namespace FSharp.ExpressionEvaluator

open System
open System.Collections.Concurrent
open System.Collections.ObjectModel
open System.Reflection
open Microsoft.VisualStudio.Debugger.Evaluation
open Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation

type private LmrTypeInfo = Microsoft.VisualStudio.Debugger.Metadata.Type
type private LmrAttribute = Microsoft.VisualStudio.Debugger.Metadata.CustomAttributeData
type private LmrBindingFlags = Microsoft.VisualStudio.Debugger.Metadata.BindingFlags

module private Lmr =
    let declared =
        LmrBindingFlags.DeclaredOnly
        ||| LmrBindingFlags.Instance
        ||| LmrBindingFlags.Static
        ||| LmrBindingFlags.Public
        ||| LmrBindingFlags.NonPublic

    /// LMR throws when metadata cannot be resolved (e.g. a type in an assembly that is not loaded);
    /// such members are simply left out.
    let safe fallback f =
        try
            f ()
        with e ->
            Log.write "LMR" $"{e.GetType().Name}: {e.Message}"
            fallback

    let attributes (data: Collections.Generic.IList<LmrAttribute>) =
        [ for a in data ->
              { TypeName = a.Constructor.DeclaringType.FullName
                Args = [ for x in a.ConstructorArguments -> x.Value ] } ]

type internal LmrType private (t: LmrTypeInfo) =
    static let cache = ConcurrentDictionary<LmrTypeInfo, IType>()

    let attributes = lazy (Lmr.safe [] (fun () -> Lmr.attributes (t.GetCustomAttributesData())))

    let fields =
        lazy
            (Lmr.safe [] (fun () ->
                [ for f in t.GetFields Lmr.declared -> { Name = f.Name; FieldType = LmrType.Of f.FieldType; IsStatic = f.IsStatic } ]))

    let properties =
        lazy
            (Lmr.safe [] (fun () ->
                [ for p in t.GetProperties Lmr.declared ->
                      { Name = p.Name
                        PropertyType = LmrType.Of p.PropertyType
                        Attributes = Lmr.safe [] (fun () -> Lmr.attributes (p.GetCustomAttributesData())) } ]))

    let methods =
        lazy
            (Lmr.safe [] (fun () ->
                [ for m in t.GetMethods Lmr.declared ->
                      { Name = m.Name
                        IsStatic = m.IsStatic
                        Attributes = Lmr.safe [] (fun () -> Lmr.attributes (m.GetCustomAttributesData())) } ]))

    static member Of(t: LmrTypeInfo) : IType =
        if isNull t then Unchecked.defaultof<_> else cache.GetOrAdd(t, fun t -> LmrType t :> IType)

    interface IType with
        member _.FullName = t.FullName
        member _.Name = t.Name
        member _.Namespace = t.Namespace
        member _.DeclaringType = if t.IsGenericParameter then None else Option.ofObj t.DeclaringType |> Option.map LmrType.Of
        member _.BaseType = Lmr.safe None (fun () -> Option.ofObj t.BaseType |> Option.map LmrType.Of)
        member _.IsGenericParameter = t.IsGenericParameter
        member _.DefinitionName = if t.IsGenericType then t.GetGenericTypeDefinition().FullName else t.FullName
        member _.GenericArguments = if t.IsGenericType then [ for a in t.GetGenericArguments() -> LmrType.Of a ] else []
        member _.IsArray = t.IsArray
        member _.ArrayRank = if t.IsArray then t.GetArrayRank() else 0
        member _.ElementType = if t.HasElementType then LmrType.Of(t.GetElementType()) else Unchecked.defaultof<_>
        member _.IsValueType = t.IsValueType
        member _.IsEnum = t.IsEnum
        member _.Attributes = attributes.Value
        member _.Fields = fields.Value
        member _.Properties = properties.Value
        member _.Methods = methods.Value
        member _.Native = box t

/// A debuggee value. Field and element reads go through Concord without func-eval.
type internal ConcordValue(value: DkmClrValue, context: DkmInspectionContext) =
    static let noFormatSpecifiers = ReadOnlyCollection<string>([||])

    static member Failed(message: string) =
        { new IValue with
            member _.Type = Unchecked.defaultof<_>
            member _.IsNull = false
            member _.Error = message
            member _.HostValue = null
            member _.Field _ = invalidOp message
            member _.ArrayLength = 0
            member _.Element _ = invalidOp message
            member _.FallbackDisplay() = message }

    member _.Value = value

    interface IValue with
        member _.Type = LmrType.Of(value.Type.GetLmrType())
        member _.IsNull = value.IsNull

        member _.Error =
            if value.ValueFlags.HasFlag DkmClrValueFlags.Error then string value.HostObjectValue else null

        member _.HostValue = value.HostObjectValue

        member _.Field name =
            try
                ConcordValue(value.GetMemberValue(name, int MemberTypes.Field, null, context), context) :> IValue
            with e ->
                ConcordValue.Failed $"{name}: {e.Message}"

        member _.ArrayLength =
            match value.ArrayDimensions with
            | null -> 0
            | dimensions -> dimensions[0]

        member _.Element index =
            try
                ConcordValue(value.GetArrayElement([| index |], context), context) :> IValue
            with e ->
                ConcordValue.Failed $"[{index}]: {e.Message}"

        member _.FallbackDisplay() = value.GetValueString(context, noFormatSpecifiers)
