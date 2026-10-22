# F# Expression Evaluator

An F# expression evaluator for the Visual Studio debugger. It covers F# syntax in
Watch, Immediate and conditions; F#-shaped values and type names; and F# call stack
names. It ships as a VSIX written entirely in F#.

The plan and its rationale are in [docs/DESIGN.md](docs/DESIGN.md). Status: **M0**
(spikes) is done. The components are registered for F# frames, render `'T list`
natively and chain everything else to the C# EE, and log what Concord provides.

## Layout

| Path | What |
|---|---|
| `src/FSharp.ExpressionEvaluator.ResultProvider` | `IDkmClrResultProvider`; `netstandard2.0`, loads in devenv and msvsmon |
| `src/FSharp.ExpressionEvaluator.ExpressionCompiler` | `IDkmClrExpressionCompiler`, frame and instruction decoders; `net48`, IDE only |
| `src/FSharp.ExpressionEvaluator.Vsix` | Packaging only |
| `tests/TestDebuggee` | F# app that hits `Debugger.Break()` in a module function, a closure and a member; net10.0 (x64) and net48 (x86) |
| `eng/` | Deploy to the Exp instance; drive it through DTE |

## Build, install, try

Requires Windows, the .NET 10 SDK and Visual Studio 2026 (18.x, Insiders included).

```powershell
dotnet build FSharp.ExpressionEvaluator.slnx
powershell -File eng\Deploy-Experimental.ps1 -SkipBuild
powershell -File eng\Probe-Debugger.ps1 -Target net10.0
```

`Probe-Debugger.ps1` starts the Exp instance, attaches to the test debuggee, and
prints the frame name, locals and watch values at each break. Use `-Target net48`
for an x86 process debugged through msvsmon. Run it with Windows PowerShell 5.1.

The components log to `%LOCALAPPDATA%\FSharpExpressionEvaluator\ee.log`, or to
`%FSHARP_EE_LOG%` if set. Setting `FSHARP_EE_SPIKE_MARKERS=1` in VS's environment
appends `[F#]` to frame names, which shows that the F# decoder handled them.
