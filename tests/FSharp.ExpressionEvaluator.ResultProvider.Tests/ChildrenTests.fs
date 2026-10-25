module FSharp.ExpressionEvaluator.Tests.ChildrenTests

open Xunit
open FSharp.ExpressionEvaluator
open FSharp.ExpressionEvaluator.Tests.ReflectionAdapter
open FSharp.ExpressionEvaluator.Tests.Samples

/// (name, full name, preview of the child) for each child of a value with full name "v".
let private children (value: obj) =
    FSharpValues.tryChildren FSharpValues.defaultOptions (Some "v") (live value)
    |> Option.map (
        List.map (fun c ->
            let text =
                FSharpValues.tryPreview FSharpValues.defaultOptions c.DeclaredType c.Value.Value
                |> Option.defaultWith (fun () -> string c.Value.Value.HostValue)

            c.Name, defaultArg c.FullName "", text)
    )

[<Fact>]
let ``record fields`` () =
    Assert.Equal(Some [ "X", "v.X", "1"; "Y", "v.Y", "2" ], children { X = 1; Y = 2 })

[<Fact>]
let ``union case fields, cast to the case class`` () =
    let caseClass = "FSharp.ExpressionEvaluator.Tests.Samples.Shape.Circle"
    Assert.Equal(Some [ "radius", $"(({caseClass})v).radius", "2" ], children (Circle 2.0))
    let fiveS = "FSharp.ExpressionEvaluator.Tests.Samples.Five.S"
    Assert.Equal(Some [ "Item1", $"(({fiveS})v).Item1", "x"; "Item2", $"(({fiveS})v).Item2", "2" ], children (S("x", 2)))
    Assert.Equal(Some [], children Empty)
    // Generic case classes have no C# cast that names them; the fields get no full name.
    Assert.Equal(Some [ "Item", "", "1" ], children (GSome 1))

[<Fact>]
let ``union fields of single-case, struct and option unions`` () =
    Assert.Equal(Some [ "id", "v.id", "1"; "name", "v.name", "a" ], children (Single(1, "a")))
    Assert.Equal(Some [ "b", "v.b", "x" ], children (SB "x"))
    Assert.Equal(Some [ "Value", "v.Value", "42" ], children (Some 42))
    Assert.Equal(Some [ "ResultValue", "v.ResultValue", "1" ], children (Ok 1: Result<int, string>))

[<Fact>]
let ``list and array items`` () =
    Assert.Equal(Some [ "[0]", "v[0]", "10"; "[1]", "v[1]", "20" ], children [ 10; 20 ])
    Assert.Equal(Some [ "[0]", "v[0]", "[1]" ], children [ [ 1 ] ])
    Assert.Equal(Some [ "[0]", "v[0]", "7" ], children [| 7 |])

[<Fact>]
let ``tuple items, flattening Rest`` () =
    let items = children (1, 2, 3, 4, 5, 6, 7, 8, 9) |> Option.get
    Assert.Equal(9, items.Length)
    Assert.Equal(("Item8", "v.Rest.Item1", "8"), items[7])
    Assert.Equal(("Item1", "v.Item1", "1"), items[0])

[<Fact>]
let ``null-represented children keep their declared type`` () =
    Assert.Equal(Some [ "[0]", "v[0]", "()"; "[1]", "v[1]", "()" ], children [ (); () ])
    Assert.Equal(Some [ "[0]", "v[0]", "None" ], children [| (None: int option) |])
    Assert.Equal(Some [ "Item1", "v.Item1", "s"; "Item2", "v.Item2", "()" ], children ("s", ()))

    Assert.Equal(
        Some [ "U", "v.U", "()"; "O", "v.O", "None"; "G", "v.G", "()" ],
        children { U = (); O = None; G = () }
    )

    Assert.Equal(Some [ "[1]", "", "()" ], children (Map.ofList [ 1, () ]))

[<Fact>]
let ``closure captures`` () =
    let items = children (makeClosure ()) |> Option.get |> List.map (fun (n, f, _) -> n, f)
    Assert.Contains(("captured", "v.captured"), items)
    Assert.Contains(("counter", "v.counter"), items)

[<Fact>]
let ``map entries and set items`` () =
    Assert.Equal(Some [ "[1]", "", "a"; "[2]", "", "b" ], children (Map.ofList [ 2, "b"; 1, "a" ]))
    Assert.Equal(Some [ "[0]", "", "1"; "[1]", "", "2" ], children (Set.ofList [ 2; 1 ]))

[<Fact>]
let ``non-F# values have no F# children`` () =
    Assert.Equal(None, children (System.Collections.Generic.List<int>()))
