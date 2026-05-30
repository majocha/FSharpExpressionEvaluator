module FSharp.ExpressionEvaluator.Tests.ExpressionLoweringTests

open Xunit
open FSharp.ExpressionEvaluator.ExpressionLowering

[<Fact>]
let ``identifier stays unchanged`` () =
    Assert.Equal(Some "value", tryLowerExpression "value")

[<Fact>]
let ``member access stays unchanged`` () =
    Assert.Equal(Some "record.Field", tryLowerExpression "record.Field")

[<Fact>]
let ``indexer lowers to CSharp syntax`` () =
    Assert.Equal(Some "items[0]", tryLowerExpression "items.[0]")

[<Fact>]
let ``tuple elements are lowered recursively`` () =
    Assert.Equal(Some "(left, items[0])", tryLowerExpression "(left, items.[0])")

[<Fact>]
let ``Seq module call is fully qualified`` () =
    Assert.Equal(
        Some "Microsoft.FSharp.Collections.SeqModule.Length(values)",
        tryLowerExpression "Seq.length values")

[<Fact>]
let ``List module call lowers multiple arguments`` () =
    Assert.Equal(
        Some "Microsoft.FSharp.Collections.ListModule.Map(mapping, values)",
        tryLowerExpression "List.map mapping values")

[<Fact>]
let ``camelCase function names are promoted to CLR casing`` () =
    Assert.Equal(
        Some "Microsoft.FSharp.Collections.ListModule.TryFind(predicate, values)",
        tryLowerExpression "List.tryFind predicate values")

[<Fact>]
let ``nested module calls lower recursively`` () =
    Assert.Equal(
        Some "Microsoft.FSharp.Collections.SeqModule.Length((Microsoft.FSharp.Collections.ListModule.Map(mapping, values)))",
        tryLowerExpression "Seq.length (List.map mapping values)")

[<Fact>]
let ``unsupported pipeline returns None`` () =
    Assert.Equal(None, tryLowerExpression "values |> Seq.length")

[<Fact>]
let ``unsupported generic application returns None`` () =
    Assert.Equal(None, tryLowerExpression "transform values")
