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
let ``union case fields, named by a match on the case`` () =
    let samples = "FSharp.ExpressionEvaluator.Tests.Samples"

    Assert.Equal(
        Some [ "radius", $"(match v with {samples}.Shape.Circle x -> x | _ -> failwith \"not Circle\")", "2" ],
        children (Circle 2.0)
    )

    Assert.Equal(
        Some
            [
                "Item1", $"(match v with {samples}.Five.S (x, _) -> x | _ -> failwith \"not S\")", "x"
                "Item2", $"(match v with {samples}.Five.S (_, x) -> x | _ -> failwith \"not S\")", "2"
            ],
        children (S("x", 2))
    )

    Assert.Equal(Some [], children Empty)
    // A union in a module compiled with a `Module` suffix: the path as source writes it.
    Assert.Equal(
        Some [ "Item", $"(match v with {samples}.Kind.Inner.InA x -> x | _ -> failwith \"not InA\")", "1" ],
        children (Kind.InA 1)
    )

    // Generic case classes too, by the union's path without its arity.
    Assert.Equal(Some [ "Item", $"(match v with {samples}.Gen.GSome x -> x | _ -> failwith \"not GSome\")", "1" ], children (GSome 1))

[<Fact>]
let ``union fields of single-case, struct and FSharp.Core unions`` () =
    let samples = "FSharp.ExpressionEvaluator.Tests.Samples"
    // One case: no other cases to fail on.
    Assert.Equal(Some [ "id", $"(match v with {samples}.Single.Single (x, _) -> x)", "1"; "name", $"(match v with {samples}.Single.Single (_, x) -> x)", "a" ], children (Single(1, "a")))
    Assert.Equal(Some [ "b", $"(match v with {samples}.StructUnion.SB x -> x | _ -> failwith \"not SB\")", "x" ], children (SB "x"))
    // Option's `Value` member; FSharp.Core's other cases qualified by their F# type name.
    Assert.Equal(Some [ "Value", "v.Value", "42" ], children (Some 42))
    Assert.Equal(Some [ "ResultValue", "(match v with Result.Ok x -> x | _ -> failwith \"not Ok\")", "1" ], children (Ok 1: Result<int, string>))

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

    Assert.Equal(Some [ "[1]", "v[1]", "()" ], children (Map.ofList [ 1, () ]))

[<Fact>]
let ``closure captures have no full name, as the closure class is no F# type`` () =
    let items = children (makeClosure ()) |> Option.get |> List.map (fun (n, f, _) -> n, f)
    Assert.Contains(("captured", ""), items)
    Assert.Contains(("counter", ""), items)

[<Fact>]
let ``map entries and set items`` () =
    // By a key that is a literal, with its type's suffix; by position otherwise.
    Assert.Equal(Some [ "[1]", "v[1]", "a"; "[2]", "v[2]", "b" ], children (Map.ofList [ 2, "b"; 1, "a" ]))
    Assert.Equal(Some [ "[\"one\"]", "v[\"one\"]", "1" ], children (Map.ofList [ "one", 1 ]))
    Assert.Equal(Some [ "[1L]", "v[1L]", "a" ], children (Map.ofList [ 1L, "a" ]))
    Assert.Equal(Some [ "[{ X = 1; Y = 2 }]", "(Seq.item 0 v).Value", "a" ], children (Map.ofList [ { X = 1; Y = 2 }, "a" ]))
    Assert.Equal(Some [ "[nan]", "(Seq.item 0 v).Value", "a" ], children (Map.ofList [ nan, "a" ]))
    Assert.Equal(Some [ "[0]", "Seq.item 0 v", "1"; "[1]", "Seq.item 1 v", "2" ], children (Set.ofList [ 2; 1 ]))

    // Map keys are literals in their full names under hex display too.
    let hex = FSharpValues.defaultOptions |> FSharpValues.withRadix 16u
    let names = FSharpValues.tryChildren hex (Some "v") (live (Map.ofList [ 10L, "a" ])) |> Option.get
    Assert.Equal<(string * string option) list>([ "[0x000000000000000a]", Some "v[10L]" ], names |> List.map (fun c -> c.Name, c.FullName))

[<Fact>]
let ``struct tuple items and F# exception fields are bound by a pattern`` () =
    Assert.Equal(Some [ "Item1", "(let struct (x, _) = v in x)", "1.5"; "Item2", "(let struct (_, x) = v in x)", "c" ], children (struct (1.5, 'c')))
    let samples = "FSharp.ExpressionEvaluator.Tests.Samples"

    Assert.Equal(
        Some
            [ "code", $"(match (v :> exn) with {samples}.MyError (x, _) -> x | _ -> failwith \"not MyError\")", "404"
              "Data1", $"(match (v :> exn) with {samples}.MyError (_, x) -> x | _ -> failwith \"not MyError\")", "not found" ],
        children (MyError(404, "not found"))
    )

[<Fact>]
let ``non-F# values have no F# children`` () =
    Assert.Equal(None, children (System.Collections.Generic.List<int>()))

[<Fact>]
let ``a created lazy value has its value as a child`` () =
    let pending = lazy 5
    Assert.Equal(Some [], children pending)
    Assert.True(FSharpValues.hasChildren (live pending))
    Assert.True(FSharpValues.hasRawView (live pending))
    pending.Force() |> ignore
    Assert.Equal(Some [ "Value", "v.Value", "5" ], children pending)

[<Fact>]
let ``a bigint has no children`` () =
    Assert.Equal(Some [], children 5I)
    Assert.False(FSharpValues.hasChildren (live 5I))

[<Fact>]
let ``full names put names in double backticks where F# needs them`` () =
    Assert.Equal(Some [ "my field", "v.``my field``", "1"; "type", "v.``type``", "t" ], children { ``my field`` = 1; ``type`` = "t" })

    let samples = "FSharp.ExpressionEvaluator.Tests.Samples"

    Assert.Equal(
        Some [ "the value", $"(match v with {samples}.OddUnion.``My Case`` x -> x | _ -> failwith \"not My Case\")", "7" ],
        children (``My Case`` 7)
    )

    Assert.Equal(
        Some [ "Item", $"(match v with {samples}.``Odd Module``.Inner.InA x -> x | _ -> failwith \"not InA\")", "2" ],
        children (``Odd Module``.InA 2)
    )

    // A parent in backticks is parenthesized, which F# reads the same.
    Assert.Equal("(``my value``).X", FSharpValues.FullNames.memberAccess "``my value``" "X")

[<Fact>]
let ``identifiers are left as they are`` () =
    for name in [ "x"; "_arg1"; "x'"; "Item1"; "éclair" ] do
        Assert.Equal(name, FSharpTypes.fsharpIdent name)

    for name in [ "my field"; "type"; "x@5"; "1st"; ""; "base" ] do
        Assert.Equal($"``{name}``", FSharpTypes.fsharpIdent name)

[<Fact>]
let ``only array elements and record fields are editable`` () =
    let editable (value: obj) =
        FSharpValues.tryChildren FSharpValues.defaultOptions (Some "v") (live value)
        |> Option.get
        |> List.map (fun c -> c.Name, c.Editable)

    Assert.Equal<(string * bool) list>([ "[0]", true ], editable [| 1 |])
    Assert.Equal<(string * bool) list>([ "X", true; "Y", true ], editable { X = 1; Y = 2 })
    Assert.Equal<(string * bool) list>([ "contents", true ], editable (ref 1))
    // No assignment in F# to these.
    Assert.Equal<(string * bool) list>([ "[0]", false ], editable [ 1 ])
    Assert.Equal<(string * bool) list>([ "Item1", false; "Item2", false ], editable (1, 2))
    Assert.Equal<(string * bool) list>([ "Item1", false; "Item2", false ], editable (struct (1, 2)))
    Assert.Equal<(string * bool) list>([ "A", false ], editable {| A = 1 |})
    Assert.Equal<(string * bool) list>([ "radius", false ], editable (Circle 2.0))
    Assert.Equal<(string * bool) list>([ "[1]", false ], editable (Map.ofList [ 1, "a" ]))
    Assert.Equal<(string * bool) list>([ "[0]", false ], editable (Set.ofList [ 1 ]))
    // Without a full name, nothing to assign to.
    Assert.Equal<(string * bool) list>([ "[0]", false ], FSharpValues.tryChildren FSharpValues.defaultOptions None (live [| 1 |]) |> Option.get |> List.map (fun c -> c.Name, c.Editable))
