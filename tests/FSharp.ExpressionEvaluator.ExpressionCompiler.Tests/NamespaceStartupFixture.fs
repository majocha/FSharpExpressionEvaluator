/// A file that starts with a namespace: its top-level code is in
/// `<StartupCode$…>.$NamespaceStartupFixture`, named after the file, and sets the values of both
/// modules. `Helpers` sets none, so it is not opened there.
namespace FSharp.ExpressionEvaluator.Tests.NamespaceStartup

module First =
    let mutable firstCounter = 11

    module Nested =
        let mutable nestedCounter = 13

module Second =
    let mutable private secondCounter = 12

    let readSecond () = secondCounter

module Helpers =
    let helper () = 14
