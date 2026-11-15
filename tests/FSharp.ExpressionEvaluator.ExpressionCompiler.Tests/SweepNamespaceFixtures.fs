/// A class at namespace level, for the sweep of frame shapes: with no module around it, the
/// classes of its closures are nested in it.
namespace FSharp.ExpressionEvaluator.Tests.SweepNamespace

open FSharp.ExpressionEvaluator.Tests

type Counter() =
    static let step = 2

    static member Run() =
        [ 1 ]
        |> List.map (fun x ->
            SweepFixtures.probe "namespaceStaticLambda"
            x * step)
