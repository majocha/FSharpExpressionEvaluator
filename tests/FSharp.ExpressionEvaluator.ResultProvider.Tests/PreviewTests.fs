module FSharp.ExpressionEvaluator.Tests.PreviewTests

open Xunit
open FSharp.ExpressionEvaluator
open FSharp.ExpressionEvaluator.Tests.ReflectionAdapter
open FSharp.ExpressionEvaluator.Tests.Samples

let private preview<'T> (value: 'T) =
    FSharpValues.tryPreview FSharpValues.defaultOptions (Some typeOf<'T>) (live (box value))

let private shows<'T> (expected: string) (value: 'T) =
    Assert.Equal(Some expected, preview value)

[<Fact>]
let ``lists`` () =
    shows "[1; 2; 3]" [ 1; 2; 3 ]
    shows "[]" ([]: int list)
    shows "[[1; 2]; [3]]" [ [ 1; 2 ]; [ 3 ] ]
    shows "[1; 2; 3; 4; 5; 6; 7; 8; 9; 10; ...]" [ 1..200 ]
    shows "[\"a\"; \"b\"]" [ "a"; "b" ]

[<Fact>]
let ``arrays`` () =
    shows "[|1; 2|]" [| 1; 2 |]
    shows "[||]" ([||]: int[])

[<Fact>]
let ``options`` () =
    shows "Some 42" (Some 42)
    shows "None" (None: int option)
    shows "Some (Some 1)" (Some(Some 1))
    shows "Some (Circle 2.0)" (Some(Circle 2.0))
    shows "Some [1]" (Some [ 1 ])
    shows "ValueSome 1" (ValueSome 1)
    shows "ValueNone" (ValueNone: int voption)

[<Fact>]
let ``unions in each representation`` () =
    shows "Circle 2.0" (Circle 2.0)
    shows "Rect (1.0, 2.5)" (Rect(1.0, 2.5))
    shows "Empty" Empty
    shows "B" B
    shows "P" P
    shows "Q 1" (Q 1)
    shows "S (\"x\", 2)" (S("x", 2))
    shows "T" T
    shows "Single (1, \"a\")" (Single(1, "a"))
    shows "SB \"x\"" (SB "x")
    shows "GSome 1" (GSome 1)
    shows "GNone" (GNone: Gen<int>)
    shows "Ok 1" (Ok 1: Result<int, string>)
    shows "Error \"e\"" (Error "e": Result<int, string>)

[<Fact>]
let ``records`` () =
    shows "{ X = 1; Y = 2 }" { X = 1; Y = 2 }
    shows "{ SX = 1; SY = 2 }" { SX = 1; SY = 2 }
    shows "{| A = 1; B = \"x\" |}" {| A = 1; B = "x" |}
    shows "{ contents = 3 }" (ref 3)
    shows "MyError (1, \"x\")" (MyError(1, "x"))

[<Fact>]
let ``tuples`` () =
    shows "(1, \"a\")" (1, "a")
    shows "struct (1, \"a\")" (struct (1, "a"))
    shows "(1, 2, 3, 4, 5, 6, 7, 8, 9)" (1, 2, 3, 4, 5, 6, 7, 8, 9)
    shows "(\"string\", ())" ("string", ())
    shows "(None, 1)" ((None: int option), 1)
    shows "(1, 2, 3, 4, 5, 6, 7, ())" (1, 2, 3, 4, 5, 6, 7, ())

[<Fact>]
let ``null-represented values inside containers`` () =
    shows "[(); ()]" [ (); () ]
    shows "[None; Some 1]" [ None; Some 1 ]
    shows "[|(); ()|]" [| (); () |]
    shows "[|None|]" [| (None: int option) |]
    shows "{ U = (); O = None; G = () }" { U = (); O = None; G = () }
    shows "{ U = (); O = None; G = None }" { U = (); O = None; G = (None: string option) }
    shows "{| A = (); B = None |}" {| A = (); B = (None: int option) |}
    shows "Some ()" (Some())
    shows "Ok ()" (Ok(): Result<unit, string>)
    shows "GSome None" (GSome(None: int option))
    shows "map [(1, ()); (2, ())]" (Map.ofList [ 1, (); 2, () ])
    shows "set [None; Some 1]" (Set.ofList [ Some 1; None ])
    shows "struct ((), None)" (struct ((), (None: int option)))
    shows "[[(); ()]]" [ [ (); () ] ]

[<Fact>]
let ``unit, functions, maps and sets`` () =
    shows "()" ()
    Assert.StartsWith("<fun:", (preview (makeClosure ())).Value)
    shows "map [(1, \"a\"); (2, \"b\"); (3, \"c\")]" (Map.ofList [ 3, "c"; 1, "a"; 2, "b" ])
    shows "map []" (Map.empty: Map<int, string>)
    shows "set [1; 2; 3]" (Set.ofList [ 3; 1; 2 ])

[<Fact>]
let ``primitive literals inside F# values`` () =
    shows "[1.0; 1.5; nan; infinity]" [ 1.0; 1.5; nan; infinity ]
    shows "[1.0f]" [ 1.0f ]
    shows "[1L; 2uy; 3s]" [ box 1L; box 2uy; box 3s ]
    shows "[1.5M]" [ 1.5M ]
    shows "['a'; '\\n'; '\\'']" [ 'a'; '\n'; '\'' ]
    shows "[\"a\\\"b\\n\"]" [ "a\"b\n" ]
    shows "[true; false]" [ true; false ]

let private seqShows (expected: string) (value: obj) =
    Assert.Equal(Some expected, FSharpValues.trySeqPreview FSharpValues.defaultOptions (live value))

[<Fact>]
let ``ranges are computed from their bounds`` () =
    seqShows "seq [1; 2; 3]" (seq { 1..3 })
    seqShows "seq [1; 3; 5; 7; 9]" (seq { 1..2..10 })
    seqShows "seq [9; 7; 5]" (seq { 9..-2..5 })
    seqShows "seq []" (seq { 3..1 })
    seqShows "seq [1; 2; 3; 4; 5; 6; 7; 8; 9; 10; ...]" (seq { 1..1000000 })
    seqShows "seq [1L; 2L]" (seq { 1L..2L })
    seqShows "seq [18446744073709551614UL; 18446744073709551615UL]" (seq { System.UInt64.MaxValue - 1UL .. System.UInt64.MaxValue })
    seqShows "seq [1uy; 2uy]" (seq { 1uy..2uy })
    seqShows "seq ['a'; 'b'; 'c']" (seq { 'a'..'c' })

[<Fact>]
let ``other F# sequences are not enumerated`` () =
    seqShows "seq [...]" (seq { 1.0..3.0 })
    seqShows "seq [...]" (seq {
        yield 1
        yield 2
    })
    seqShows "seq [...]" (Seq.map id [ 1; 2 ])
    seqShows "seq [...]" (Seq.cache [ 1 ])

[<Fact>]
let ``non-F# sequences and F# collections are not sequences`` () =
    let none (value: obj) = Assert.Equal(None, FSharpValues.trySeqPreview FSharpValues.defaultOptions (live value))
    none (System.Collections.Generic.List<int>())
    none [ 1 ]
    none [| 1 |]
    none (Set.ofList [ 1 ])
    none "text"

[<Fact>]
let ``sequences inside F# values`` () =
    shows "{| S = seq [1; 2] |}" {| S = seq { 1..2 } |}
    shows "[seq [...]]" [ Seq.map id [ 1 ] ]

[<Fact>]
let ``non-F# values are left to the next provider`` () =
    Assert.Equal(None, preview 42)
    Assert.Equal(None, preview "text")
    Assert.Equal(None, preview (System.Collections.Generic.List<int>()))
    Assert.Equal(None, preview (null: string))

[<Fact>]
let ``non-F# values nested in F# values use the fallback display`` () =
    let coreLib = typeof<obj>.Assembly.GetName().Version
    shows $"[{{System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib, Version={coreLib}, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]}}]" [ System.Collections.Generic.List<int>() ]

[<Fact>]
let ``deep nesting is cut`` () =
    shows "[[[...]]]" [ [ [ [ 1 ] ] ] ]

/// A Lazy whose value is created.
let private forced value =
    let l = lazy value
    l.Force() |> ignore
    l

[<Fact>]
let ``lazy values show their value once created, without forcing it`` () =
    let pending = lazy (1 + 1)
    shows "<unevaluated>" pending
    Assert.False pending.IsValueCreated
    pending.Force() |> ignore
    shows "2" pending
    shows "[1]" (forced [ 1 ])
    shows "[<unevaluated>; Some 3]" [ lazy (Some 1); forced (Some 3) ]

    let faulted = lazy (failwith "no": int)
    (try faulted.Force() |> ignore with _ -> ())
    shows "<faulted>" faulted

[<Fact>]
let ``bigint literals`` () =
    shows "0I" 0I
    shows "-42I" -42I
    shows "12345678901234567890123456789I" 12345678901234567890123456789I
    shows "-18446744073709551616I" -18446744073709551616I
    shows "[1I; 2I]" [ 1I; 2I ]

let private showsHex<'T> (expected: string) (value: 'T) =
    let options = FSharpValues.defaultOptions |> FSharpValues.withRadix 16u
    Assert.Equal(Some expected, FSharpValues.tryPreview options (Some typeOf<'T>) (live (box value)))

[<Fact>]
let ``hex display applies to integers inside previews`` () =
    showsHex "[0x0000000a; 0xffffffff]" [ 10; -1 ]
    showsHex "[0x000000000000000a; 0xff; 0x0010]" [ box 10L; box 255uy; box 16s ]
    showsHex "[1.5; 'a'; \"s\"; true; 2.5M]" [ box 1.5; box 'a'; box "s"; box true; box 2.5M ]
    showsHex "Some 0x0000002a" (Some 42)
    showsHex "[42I]" [ 42I ]
    Assert.Equal(Some "seq [0x01; 0x02]", FSharpValues.trySeqPreview (FSharpValues.defaultOptions |> FSharpValues.withRadix 16u) (live (seq { 1uy..2uy })))
    Assert.Equal(Some "[0x00000001]", FSharpValues.tryPreview (FSharpValues.withRadix 16u FSharpValues.defaultOptions) None (live (forced [ 1 ])))

let private columns hex (value: obj) =
    FSharpValues.tryPrimitiveColumns hex (live value)

[<Fact>]
let ``primitive rows show F# literals, without integer suffixes in the value column`` () =
    Assert.Equal(Some("2.0", "2.0"), columns false 2.0)
    Assert.Equal(Some("nan", "nan"), columns false nan)
    Assert.Equal(Some("-infinity", "-infinity"), columns false -infinity)
    Assert.Equal(Some("1.5f", "1.5f"), columns false 1.5f)
    Assert.Equal(Some("1.5M", "1.5M"), columns false 1.5M)
    // The value column has no integer suffix; an edit starts from the F# literal.
    Assert.Equal(Some("42", "42"), columns false 42)
    Assert.Equal(Some("-1", "-1L"), columns false -1L)
    Assert.Equal(Some("255", "255uy"), columns false 255uy)
    Assert.Equal(Some("7", "7UL"), columns false 7UL)
    Assert.Equal(Some("'a'", "'a'"), columns false 'a')
    Assert.Equal(Some(@"'\''", @"'\''"), columns false '\'')
    Assert.Equal(Some(@"""a\tb\000""", @"""a\tb\000"""), columns false "a\tb\000")
    Assert.Equal(Some("true", "true"), columns false true)

[<Fact>]
let ``primitive rows in hex keep their F# suffix for edits`` () =
    Assert.Equal(Some("0x0000002a", "0x0000002a"), columns true 42)
    Assert.Equal(Some("0xffffffffffffffff", "0xffffffffffffffffL"), columns true -1L)
    Assert.Equal(Some("0x10", "0x10uy"), columns true 16uy)
    // Floats, chars and strings keep their literals.
    Assert.Equal(Some("2.5", "2.5"), columns true 2.5)
    Assert.Equal(Some("'a'", "'a'"), columns true 'a')

[<Fact>]
let ``nulls, enums and other values are not primitive rows`` () =
    Assert.Equal(None, columns false null)
    Assert.Equal(None, columns false System.DayOfWeek.Monday)
    Assert.Equal(None, columns false (System.Guid.Empty))
    Assert.Equal(None, columns false [ 1 ])
