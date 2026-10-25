/// A top-level module with mutable values: its top-level code (the static initializer of
/// `<StartupCode$…>.$FSharp.ExpressionEvaluator.Tests.StartupFixture`) is a frame of its own.
module FSharp.ExpressionEvaluator.Tests.StartupFixture

let mutable startupCounter = 9

let mutable private hiddenCounter = 10

let readHidden () = hiddenCounter
