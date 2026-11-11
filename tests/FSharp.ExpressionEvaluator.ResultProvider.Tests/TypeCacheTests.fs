module FSharp.ExpressionEvaluator.Tests.TypeCacheTests

open System
open System.Runtime.CompilerServices
open Xunit
open FSharp.ExpressionEvaluator

/// A type with only a native object, as an LMR type of a debug session stands for one.
let private typeOver (native: obj) =
    { new IType with
        member _.FullName = "T"
        member _.Name = "T"
        member _.Namespace = ""
        member _.DeclaringType = None
        member _.BaseType = None
        member _.Interfaces = []
        member _.IsGenericParameter = false
        member _.DefinitionName = "T"
        member _.GenericArguments = []
        member _.IsArray = false
        member _.ArrayRank = 0
        member _.ElementType = Unchecked.defaultof<_>
        member _.IsValueType = false
        member _.IsEnum = false
        member _.Attributes = []
        member _.Fields = []
        member _.Properties = []
        member _.Methods = []
        member _.TypeExists _ = false
        member _.Native = native }

/// Caches a value for a new native object, the value refers back to the type, and returns only a
/// weak reference to the native object.
[<MethodImpl(MethodImplOptions.NoInlining)>]
let private cacheValueOfNewType (cache: IType -> (IType -> IType * obj) -> IType * obj) =
    let native = obj ()
    let t = typeOver native
    cache t (fun t -> t, obj ()) |> ignore
    WeakReference native

[<Fact>]
let ``a type cache does not keep its types alive`` () =
    let cache = Types.cache ()
    let weak = cacheValueOfNewType cache

    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

    Assert.False(weak.IsAlive, "The cache keeps the native type alive.")
    GC.KeepAlive cache

[<Fact>]
let ``a type cache computes a value once for a native type`` () =
    let cache = Types.cache ()
    let native = obj ()
    let mutable created = 0

    let get () =
        cache (typeOver native) (fun _ ->
            created <- created + 1
            ref 0)

    // Another IType over the same native object finds the same value.
    Assert.Same(get (), get ())
    Assert.Equal(1, created)
    // Another native object has its own value.
    let other = cache (typeOver (obj ())) (fun _ -> ref 0)
    Assert.NotSame(get (), other)
