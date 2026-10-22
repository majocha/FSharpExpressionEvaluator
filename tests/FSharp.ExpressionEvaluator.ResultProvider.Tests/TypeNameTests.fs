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
