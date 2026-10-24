# F# Expression Evaluator

An F# expression evaluator for the Visual Studio debugger, shipped as a VSIX written
entirely in F#. When you stop in F# code, it shows values, types and call stack names the
way F# writes them, and it compiles F# expressions in Watch, Immediate and DataTips.

Today, Visual Studio has no F# evaluator. F# frames fall back to the C# one: C# syntax,
C# type names, and F# values rendered by calling `sprintf "%A"` inside the debugged process.
That is slow, needs func-eval, fails in dumps and sometimes blocks the IDE with a modal
dialog.

## What works today

**Values (Locals, Watch, Autos, DataTips) in F# frames.** Values are read from memory
without running code in the debuggee:

| Value | Type |
|---|---|
| `{ X = 1; Y = 2 }` | `Program.Point` |
| `Rect (1.0, 2.5)` | `Program.Shape` |
| `Some (Circle 1.5)` | `Program.Shape option` |
| `Error "failed"` | `Result<int, string>` |
| `[[1; 2]; [3]]` | `int list list` |
| `map [("one", 1); ("two", 2)]` | `Map<string, int>` |
| `struct (1.5, 'c')` | `struct (float * char)` |
| `{\| Count = 3; Name = "anon" \|}` | `{\| Count: int; Name: string \|}` |
| `<fun:add@53>` | `int -> int -> int` |

This covers:
- records, anonymous records and ref cells;
- unions in every compiled representation, including `option`, `voption` and `Result`;
- lists, arrays, tuples, `Map` and `Set`;
- F# exceptions, unit and closures.

Expanding a value shows its F# fields, list items, map entries or captured values. A
**Raw View** row shows the raw .NET members. Everything else is rendered by the C# result
provider.

**Call stack and breakpoint locations** in F# frames:

```
Program.inspect (xs: int list) (name: string)
Program.generic<int list> (value: int list) (pair: int * string)
Program.f@66 (x: int)
Program.Counter.Increment (by: int)
```

Names show curried argument groups, tuple parameters, `()`, operators and source names. They
also show closures without `.Invoke`, the frame's actual generic arguments and, optionally,
argument values.

**F# expressions in Watch, Immediate and DataTips** (M4, in progress). They are compiled by
the F# compiler, from a `dotnet/fsharp` branch built privately into the VSIX:

```
xs |> List.sum                      6
List.map (fun i -> i * 2) xs        [2; 4; 6]
captured + x                        6        (in a closure)
count + by                          14       (a class let field)
this.Increment 0                    12
name.Length, h                      0x00000004
```

The compiler sees what the debuggee has loaded, read from its memory. That includes
FSharp.Core's inline functions, private members and module values, F# errors (`FS0039`),
aliases (`$exception`), format specifiers and assignment. The Locals window still
comes from the C# compiler.

**Building.** The private compiler comes from the
[expression-evaluator branch](https://github.com/majocha/fsharp/tree/expression-evaluator)
of `dotnet/fsharp`. Build that checkout once, then run `eng\Build-Compiler.ps1`. It expects the
checkout at `..\fsharp.worktrees\expression-evaluator`, or set `FSHARP_EE_FSHARP_REPO`.

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | F#-only VSIX layout; spikes confirming the Concord assumptions | done |
| M1 | Result provider: F# values and type names | done |
| M2 | Frame and instruction decoder: F# call stack names | done |
| M3 | F# expression compiler core, in a `dotnet/fsharp` branch | done |
| M4 | Concord integration: Watch, Immediate, conditions, Locals, assignment | in progress |
| M5 | Closures, state machines and compiler temporaries in Locals; custom type info | |
| M6 | Upstream PDB improvements in `dotnet/fsharp` | |

The design, the reasoning behind it, and the results of each milestone are in
[docs/DESIGN.md](docs/DESIGN.md).

## How it fits into the debugger

The evaluator plugs into Concord, the VS debug engine. It registers only for frames whose PDB
says F#; C# and VB frames are untouched.

| Component | Concord interfaces | Runs in |
|---|---|---|
| `FSharp.ExpressionEvaluator.ResultProvider` (`netstandard2.0`) | `IDkmClrResultProvider` | devenv, and msvsmon for remote or x86 targets |
| `FSharp.ExpressionEvaluator.ExpressionCompiler` (`net48`) | `IDkmClrExpressionCompiler` (still passes everything to C#), `IDkmLanguageFrameDecoder`, `IDkmLanguageInstructionDecoder` | devenv |
| `FSharp.ExpressionEvaluator.Vsix` | packaging only | |

When a value is not an F# shape, or a frame can't be named, the call is passed to the next
implementer: the C# evaluator.

## Repository layout

| Path | What |
|---|---|
| `src/FSharp.ExpressionEvaluator.ResultProvider` | Value display |
| `src/FSharp.ExpressionEvaluator.ExpressionCompiler` | Frame names now; the expression compiler later |
| `src/FSharp.ExpressionEvaluator.Vsix` | The VSIX |
| `tests/FSharp.ExpressionEvaluator.ResultProvider.Tests` | Value display against live F# values |
| `tests/FSharp.ExpressionEvaluator.ExpressionCompiler.Tests` | Frame names from F# fixtures read back through System.Reflection.Metadata |
| `tests/TestDebuggee` | F# app that stops in a module function, a closure, a member, a generic function and a task; net10.0 (x64) and net48 (x86) |
| `eng/` | Install into the Exp instance; drive it through DTE |
| `docs/DESIGN.md` | Design and milestone results |

## Build, test, try

Requires Windows, the .NET 10 SDK and Visual Studio 2026 (18.x, Insiders included).

```powershell
dotnet build FSharp.ExpressionEvaluator.slnx
dotnet test FSharp.ExpressionEvaluator.slnx
```

To install into the Exp instance and see it in action:

```powershell
powershell -File eng\Deploy-Experimental.ps1 -SkipBuild
powershell -File eng\Probe-Debugger.ps1 -Target net10.0
```

`Probe-Debugger.ps1` starts the Exp instance and attaches to the test debuggee. At each
break it prints the call stack, the locals and some watch values. Use `-Target net48` for an
x86 process debugged through msvsmon. Run the scripts with Windows PowerShell 5.1. You can
also open `tests\TestDebuggee` in the Exp instance and debug it by hand.

The components log to `%LOCALAPPDATA%\FSharpExpressionEvaluator\ee.log`, or to
`%FSHARP_EE_LOG%` if set.
