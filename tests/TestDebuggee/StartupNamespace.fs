/// Top-level code of a file that starts with a namespace: it is in <StartupCode$TestDebuggee>.$StartupNamespace,
/// named after the file, and sets the values of Defaults and Checks, so it should see theirs. It runs
/// when main first uses Checks.ready; with --startup, break S2 stops there.
namespace TestDebuggee.Settings

open System.Diagnostics

module Defaults =
    let mutable level = 2

    let mutable private label = "defaults"

    let describe () = $"{label} {level}"

module Checks =
    /// Break S2 (--startup): a namespace file's top-level code.
    let ready =
        if TestDebuggee.Startup.startupBreaks then
            Debugger.Break()

        Defaults.level > 0
