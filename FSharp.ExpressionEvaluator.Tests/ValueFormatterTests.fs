module FSharp.ExpressionEvaluator.Tests.ValueFormatterTests

open Xunit
open FSharp.ExpressionEvaluator.ValueFormatter

// ---------------------------------------------------------------------------
// unit
// ---------------------------------------------------------------------------

[<Fact>]
let ``unit always formats as ()`` () =
    Assert.Equal("()", formatValue "unit" "{}")

[<Fact>]
let ``unit ignores the raw string`` () =
    Assert.Equal("()", formatValue "unit" "null")

[<Fact>]
let ``unit ignores empty raw string`` () =
    Assert.Equal("()", formatValue "unit" "")

// ---------------------------------------------------------------------------
// char — strip numeric code-point prefix inserted by C# EE
// ---------------------------------------------------------------------------

[<Fact>]
let ``char strips numeric prefix`` () =
    Assert.Equal("'A'", formatValue "char" "65 'A'")

[<Fact>]
let ``char strips numeric prefix for non-ASCII`` () =
    Assert.Equal("'é'", formatValue "char" "233 'é'")

[<Fact>]
let ``char already in F# style is returned as-is`` () =
    Assert.Equal("'A'", formatValue "char" "'A'")

[<Fact>]
let ``char with zero code point`` () =
    Assert.Equal("'\000'", formatValue "char" "0 '\000'")

// ---------------------------------------------------------------------------
// Other types — pass through unchanged
// ---------------------------------------------------------------------------

[<Fact>]
let ``int raw value is unchanged`` () =
    Assert.Equal("42", formatValue "int" "42")

[<Fact>]
let ``string raw value is unchanged`` () =
    Assert.Equal("\"hello\"", formatValue "string" "\"hello\"")

[<Fact>]
let ``bool raw value is unchanged`` () =
    Assert.Equal("true", formatValue "bool" "true")

[<Fact>]
let ``float raw value is unchanged`` () =
    Assert.Equal("3.14", formatValue "float" "3.14")

[<Fact>]
let ``unknown type raw value is unchanged`` () =
    Assert.Equal("{x = 1}", formatValue "MyRecord" "{x = 1}")

[<Fact>]
let ``option raw value is unchanged`` () =
    Assert.Equal("Some(42)", formatValue "int option" "Some(42)")
