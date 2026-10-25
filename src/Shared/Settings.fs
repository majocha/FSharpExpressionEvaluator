/// The "Use the F# expression evaluator" switch (Tools > Options > Debugging > F# Expression
/// Evaluator), linked into each assembly. The option lives in VS's settings store, which msvsmon
/// cannot read, so the VSIX package mirrors it to a marker file every component checks
/// (docs/DESIGN.md §19). FSHARP_EE_CSHARP=1 also turns the F# EE off.
module internal FSharp.ExpressionEvaluator.Settings

open System
open System.IO

let private disabledFile =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
        "FSharpExpressionEvaluator",
        "disabled"
    )

let private forcedOff = Environment.GetEnvironmentVariable "FSHARP_EE_CSHARP" = "1"

/// How long a read of the marker file is reused: values are formatted one by one.
let private ttl = 500

[<AllowNullLiteral>]
type private Cached(enabled: bool, tick: int) =
    member _.Enabled = enabled
    member _.Tick = tick

let mutable private cached: Cached = null

/// Whether the F# components handle F# frames; when off, they pass everything to the next
/// implementer (C#), as if the VSIX were not installed. Takes effect within `ttl` ms.
let enabled () =
    if forcedOff then
        false
    else
        let now = Environment.TickCount

        match cached with
        | c when not (isNull c) && now - c.Tick < ttl -> c.Enabled
        | _ ->
            let value = not (File.Exists disabledFile)
            cached <- Cached(value, now)
            value

/// Called by the VSIX package when the option changes.
let setEnabled (value: bool) =
    if value then
        if File.Exists disabledFile then
            File.Delete disabledFile
    else
        Directory.CreateDirectory(Path.GetDirectoryName disabledFile) |> ignore
        File.WriteAllText(disabledFile, "")

    cached <- null
