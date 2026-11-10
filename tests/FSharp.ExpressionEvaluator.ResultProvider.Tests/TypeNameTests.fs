module FSharp.ExpressionEvaluator.Tests.TypeNameTests

open System.Collections.Generic
open Xunit
open FSharp.ExpressionEvaluator
open FSharp.ExpressionEvaluator.Tests.ReflectionAdapter
open FSharp.ExpressionEvaluator.Tests.Samples

let private name t = FSharpTypes.typeName t

let private ns = "FSharp.ExpressionEvaluator.Tests.Samples"

[<Fact>]
let ``primitive abbreviations`` () =
    Assert.Equal("int", name typeOf<int>)
    Assert.Equal("float", name typeOf<float>)
    Assert.Equal("float32", name typeOf<float32>)
    Assert.Equal("int64", name typeOf<int64>)
    Assert.Equal("string", name typeOf<string>)
    Assert.Equal("obj", name typeOf<obj>)
    Assert.Equal("unit", name typeOf<unit>)
    Assert.Equal("bigint", name typeOf<bigint>)

[<Fact>]
let ``postfix generic types`` () =
    Assert.Equal("int list", name typeOf<int list>)
    Assert.Equal("string option list", name typeOf<string option list>)
    Assert.Equal("int voption", name typeOf<int voption>)
    Assert.Equal("int ref", name typeOf<int ref>)
    Assert.Equal("int array", name typeOf<int[]>)
    Assert.Equal("int[,]", name typeOf<int[,]>)
    Assert.Equal("int array list", name typeOf<int[] list>)

[<Fact>]
let ``tuples and functions`` () =
    Assert.Equal("int * string", name typeOf<int * string>)
    Assert.Equal("struct (int * string)", name typeOf<struct (int * string)>)
    Assert.Equal("(int * int) list", name typeOf<(int * int) list>)
    Assert.Equal("int -> int", name typeOf<int -> int>)
    Assert.Equal("int -> int -> int", name typeOf<int -> int -> int>)
    Assert.Equal("(int -> int) -> int", name typeOf<(int -> int) -> int>)
    Assert.Equal("int * int -> int", name typeOf<int * int -> int>)
    Assert.Equal("(int -> int) option", name typeOf<(int -> int) option>)
    Assert.Equal("int * (int -> int)", name typeOf<int * (int -> int)>)
    Assert.Equal("int * int * int * int * int * int * int * int * int", name typeOf<int * int * int * int * int * int * int * int * int>)

[<Fact>]
let ``closure classes are named by their function type`` () =
    Assert.Equal("int -> int", name (live (makeClosure ())).Type)
    Assert.Equal("int -> int -> int", name (live curried).Type)

[<Fact>]
let ``named generic types`` () =
    Assert.Equal("Map<string, int>", name typeOf<Map<string, int>>)
    Assert.Equal("Set<int>", name typeOf<Set<int>>)
    Assert.Equal("Result<int, string>", name typeOf<Result<int, string>>)
    Assert.Equal("seq<int>", name typeOf<seq<int>>)
    Assert.Equal("System.Collections.Generic.Dictionary<string, int list>", name typeOf<Dictionary<string, int list>>)

[<Fact>]
let ``user types are namespace and module qualified`` () =
    Assert.Equal($"{ns}.Shape", name typeOf<Shape>)
    Assert.Equal($"{ns}.Gen<int>", name typeOf<Gen<int>>)
    Assert.Equal($"{ns}.Point", name typeOf<Point>)
    // In a module named as a type beside it, compiled as `KindModule` with no attribute.
    Assert.Equal($"{ns}.Kind.Inner", name typeOf<Kind.Inner>)

[<Fact>]
let ``union case classes display as their union`` () =
    Assert.Equal($"{ns}.Shape", name (FSharpTypes.displayType (live (Circle 1.0)).Type))
    Assert.Equal($"{ns}.Gen<int>", name (FSharpTypes.displayType (live (GSome 1)).Type))

[<Fact>]
let ``module suffix is dropped`` () =
    let moduleType = typeof<Point>.DeclaringType.GetNestedType("PointModule")
    Assert.Equal($"{ns}.Point", name (ReflectionType.Of moduleType))

[<Fact>]
let ``anonymous records`` () =
    Assert.Equal("{| A: int; B: string |}", name (live {| A = 1; B = "x" |}).Type)

let private column<'T> (value: obj) = FSharpValues.csharpTypeColumn None (Some typeOf<'T>) (live value)

[<Fact>]
let ``type column of values C# renders names FSharp.Core types in F#`` () =
    Assert.Equal(Some "System.Threading.Tasks.Task<unit>", column<System.Threading.Tasks.Task<unit>> (System.Threading.Tasks.Task.FromResult()))
    Assert.Equal(Some "System.Threading.Tasks.Task<unit>", column<System.Threading.Tasks.Task<unit>> null)
    Assert.Equal(Some "System.Collections.Generic.List<int option>", column<List<int option>> (List<int option>()))
    Assert.Equal(Some "Async<int> array", column<Async<int>[]> [| async { return 1 } |])
    Assert.Equal(Some "obj {System.Threading.Tasks.Task<unit>}", column<obj> (System.Threading.Tasks.Task.FromResult()))
    // F# sequences show as seq<_>, not as their closure class.
    Assert.Equal(Some "seq<int>", column<seq<int>> (seq { 1..3 }))
    Assert.Equal(Some "obj {seq<int>}", column<obj> (seq { 1..3 }))
    Assert.Equal(Some "seq<int>", column<seq<int>> (seq { yield 1 }))
    Assert.Equal(Some "seq<int>", FSharpValues.csharpTypeColumn None None (live (seq { yield 1 })))
    // Types without FSharp.Core in them keep C#'s names.
    Assert.Equal(None, column<seq<int>> (List<int>()))
    Assert.Equal(None, column<List<int>> (List<int>()))
    Assert.Equal(None, column<obj> (List<int>()))

[<Fact>]
let ``type column of primitives C# renders uses F# names`` () =
    Assert.Equal(Some "float", column<float> 2.0)
    Assert.Equal(Some "int64", column<int64> 1L)
    Assert.Equal(Some "string", column<string> "s")
    Assert.Equal(Some "string", column<string> null)
    Assert.Equal(Some "obj {string}", column<obj> "s")
    Assert.Equal(Some "obj {float32}", column<obj> 1.5f)
    Assert.Equal(Some "float array", column<float[]> [| 1.0 |])
    Assert.Equal(Some "System.IComparable {decimal}", column<System.IComparable> 1.5M)
    Assert.Equal(None, column<System.DayOfWeek> System.DayOfWeek.Monday)

let private columnAs<'T> fsharpName (value: obj) =
    FSharpValues.csharpTypeColumn (Some fsharpName) (Some typeOf<'T>) (live value)

[<Fact>]
let ``the expression compiler's F# type names the declared type`` () =
    Assert.Equal(Some "float<m>", columnAs<float> "float<m>" 1.5)
    Assert.Equal(Some "Ids", columnAs<List<int>> "Ids" (List<int>()))
    Assert.Equal(Some "Ids", columnAs<List<int>> "Ids" null)
    Assert.Equal(Some "Thing {string}", columnAs<obj> "Thing" "s")

[<Fact>]
let ``F# type info round-trips, and other payloads are ignored`` () =
    let payload = FSharpTypes.TypeInfo.encode "float<m/s> list"
    Assert.Equal(Some "float<m/s> list", FSharpTypes.TypeInfo.tryDecode FSharpTypes.TypeInfo.payloadId payload)
    Assert.Equal(None, FSharpTypes.TypeInfo.tryDecode (System.Guid.NewGuid()) payload)
    Assert.Equal(None, FSharpTypes.TypeInfo.tryDecode FSharpTypes.TypeInfo.payloadId (Array.append [| 2uy |] payload[1..]))
    Assert.Equal(None, FSharpTypes.TypeInfo.tryDecode FSharpTypes.TypeInfo.payloadId null)

[<Fact>]
let ``compiler generated names round-trip, and the F# type's payload is not them`` () =
    let names = [ "_arg1"; "``x@5``" ]
    let payload = FSharpTypes.CompilerGenerated.encode names
    Assert.Equal(Some names, FSharpTypes.CompilerGenerated.tryDecode FSharpTypes.CompilerGenerated.payloadId payload)
    Assert.Equal(None, FSharpTypes.CompilerGenerated.tryDecode FSharpTypes.TypeInfo.payloadId payload)
    Assert.Equal(None, FSharpTypes.TypeInfo.tryDecode FSharpTypes.CompilerGenerated.payloadId payload)

[<Fact>]
let ``type names in double backticks where F# needs them`` () =
    Assert.Equal($"{ns}.Odd", name typeOf<Odd>)
    Assert.Equal($"{ns}.``Odd Module``.Inner", name typeOf<``Odd Module``.Inner>)
    Assert.Equal($"{ns}.``Odd Generic``<int>", name typeOf<``Odd Generic``<int>>)
    Assert.Equal($"{ns}.``Odd Module``.Inner list", name typeOf<``Odd Module``.Inner list>)
    // F# sorts the fields of an anonymous record by name.
    Assert.Equal("{| Plain: string; ``my field``: int |}", name (ReflectionType.Of(({| ``my field`` = 1; Plain = "p" |}).GetType())))
