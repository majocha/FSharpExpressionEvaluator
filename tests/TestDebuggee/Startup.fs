/// Top-level code of a module file, in <StartupCode$TestDebuggee>.$TestDebuggee.Startup. It runs when
/// main first uses one of the file's values; with --startup, break S1 stops there, where the module's
/// mutable and private values should be in scope.
module TestDebuggee.Startup

open System
open System.Diagnostics

let startupBreaks = Array.contains "--startup" (Environment.GetCommandLineArgs())

let mutable startupCount = 1

let mutable private startupHidden = 20

/// Break S1 (--startup): a module file's top-level code.
let startupCheck =
    if startupBreaks then
        Debugger.Break()

    startupCount + startupHidden
