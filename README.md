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
| `[1; 2]` or `<unevaluated>` | `Lazy<int list>` |
| `12345678901234567890123I` | `bigint` |

This covers:
- records, anonymous records and ref cells;
- unions in every compiled representation, including `option`, `voption` and `Result`;
- lists, arrays, tuples, `Map` and `Set`;
- F# exceptions, unit and closures;
- `Lazy` values, read without forcing them, and `bigint`.

With the debugger's **Hexadecimal Display** on, integers inside F# values are in hex
(`Some 0x0000002a`).

Expanding a value shows its F# fields, list items, map entries or captured values. A
**Raw View** row shows the raw .NET members. Everything else is rendered by the C# result
provider.

**Call stack and breakpoint locations** in F# frames:

```
Program.inspect (xs: int list) (name: string) (trip: float<m>)
Program.generic<int list> (value: int list) (pair: int * string)
Program.f@66 (x: int)
Program.Counter.Increment (by: int)
```

Names show curried argument groups, tuple parameters, `()`, operators and source names. They
also show closures without `.Invoke`, the frame's actual generic arguments and, optionally,
argument values. When the F# compiler has loaded the debuggee's assemblies (from the first
break on), units of measure and type abbreviations come from the F# signature.

Names that F# writes in double backticks (``` ``my value`` ```, ``` ``type`` ```) get them in type
names, call stacks and the expressions that Add Watch and edits use.

**F# expressions in Watch, Immediate and DataTips** (M4, in progress). They are compiled by
the F# compiler, an unmodified `dotnet/fsharp` built privately into the VSIX:

```
xs |> List.sum                      6
List.map (fun i -> i * 2) xs        [2; 4; 6]
captured + x                        6        (in a closure)
count + by                          14       (a class let field)
this.Increment 0                    12
```

The compiler sees what the debuggee has loaded, read from its memory. That includes
FSharp.Core's inline functions, private members and module values, F# errors (`FS0039`),
aliases (`$exception`) and assignment. The Locals window comes from the F# compiler too.

Expressions are F# only. The C# debugger's format specifiers (`x, h`, `s, nq`) are not
accepted, so `x, h` is a tuple. Format with F# instead (`$"%x{x}"`), and use Hexadecimal
Display for hex. The only specifiers split off are the ones Visual Studio writes into the
names of Raw View and Results View rows itself (`, raw`, `, results`).

**Locals and types in F# frames** (M5). Locals shows the variables as the source has them:

| In Locals | Why |
|---|---|
| `captured`, `counter` in a closure | a closure's captured values, as variables of the frame |
| `doubled` in `task { }` or `seq { }` | the hoisted variables of the state machine, without its plumbing fields |
| `counter = 1 : int` | a captured `let mutable` reads and writes as its value, not as a ref cell |
| `name (shadowed)`, `name` | a variable that an inner one of the same name hides |
| `trip : float<m>`, `ids : Ids` | units of measure and type abbreviations, from the F# signature |
| `[compiler generated]` | one collapsed row for the compiler's own variables, such as `function`'s `_arg1` |

The Type column shows units of measure and abbreviations for expressions too
(`distance / 1.5<s>` is `float<m/s>`). For locals other than parameters, the PDB records
only IL types, so these show the CLR type.

**Building.** The private compiler is FSharp.Compiler.Service from `dotnet/fsharp`, with no
changes to it: the evaluator's code that needs the compiler's internals lives here
(`Evaluation.fs`; [docs/FCS-INTERNALS.md](docs/FCS-INTERNALS.md) lists what it uses). Run
`eng\Build-Compiler.ps1` before the first build. It fetches the commit pinned in
`eng\fsharp.commit` and builds it once, about 4 minutes, into `artifacts\fsharp-ee` (untracked, like the rest of
`artifacts\`; `FSHARP_EE_CACHE` moves it). Then it copies the compiler to `artifacts\compiler`, built under its
own name and with this repo's assemblies as friends (`eng\PrivateCompiler.targets`), and the same
commit's `fsc` to `artifacts\fsc`, which compiles the tests' fixtures and TestDebuggee. To try another checkout,
pass `-FSharpRepo <checkout>`, built once with `.\build.cmd -c Release` (or with `-c Debug`, then
pass `-Configuration Debug` too). If the build then can't find the checkout's own outputs (such as
`fslex.deps.json`), build them once there in the layout `dotnet build` expects:
`dotnet build src\Compiler\FSharp.Compiler.Service.fsproj -c <configuration> -f netstandard2.0`,
with `BUILDING_USING_DOTNET=true`.

The released SDKs' F# compiler has Debug-build regressions (resumable state machines,
inlining). To compile the evaluator itself with the private `fsc` as well, pass
`-p:DotnetFscCompilerPath="<repo>\artifacts\fsc\fsc.dll"` to `dotnet build` and `dotnet test`.

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M0 | F#-only VSIX layout; spikes confirming the Concord assumptions | done |
| M1 | Result provider: F# values and type names | done |
| M2 | Frame and instruction decoder: F# call stack names | done |
| M3 | F# expression compiler core (first in a `dotnet/fsharp` branch, now in this repo) | done |
| M4 | Concord integration: Watch, Immediate, conditions, Locals, assignment | in progress |
| M5 | Closures, state machines and compiler temporaries in Locals; custom type info | done |
| M6 | Upstream PDB improvements in `dotnet/fsharp` | |

The design, the reasoning behind it, and the results of each milestone are in
[docs/DESIGN.md](docs/DESIGN.md).

## How it fits into the debugger

The evaluator plugs into Concord, the VS debug engine. It registers only for frames whose PDB
says F#; C# and VB frames are untouched.

| Component | Concord interfaces | Runs in |
|---|---|---|
| `FSharp.ExpressionEvaluator.ResultProvider` (`netstandard2.0`) | `IDkmClrResultProvider` | devenv, and msvsmon for remote or x86 targets |
| `FSharp.ExpressionEvaluator.ExpressionCompiler` (`net48`) | `IDkmClrExpressionCompiler`, `IDkmLanguageFrameDecoder`, `IDkmLanguageInstructionDecoder` | devenv |
| `FSharp.ExpressionEvaluator.Vsix` | packaging, and a package for the on/off option | devenv |

When a value is not an F# shape, or a frame can't be named, the call is passed to the next
implementer: the C# evaluator.

**Turning it off.** *Tools > Options > Debugging > F# Expression Evaluator > Use the F#
expression evaluator in F# frames*. When off, F# frames get the C# evaluator, as without the
extension. It applies from the next evaluation, without restarting Visual Studio (values
already shown keep their F# display until they are evaluated again). `FSHARP_EE_CSHARP=1` in
the environment also turns it off.

## Repository layout

| Path | What |
|---|---|
| `src/FSharp.ExpressionEvaluator.ResultProvider` | Value display |
| `src/FSharp.ExpressionEvaluator.ExpressionCompiler` | Frame names, and the expression compiler (`Evaluation.fs`, on the private F# compiler) |
| `src/FSharp.ExpressionEvaluator.Vsix` | The VSIX |
| `tests/FSharp.ExpressionEvaluator.ResultProvider.Tests` | Value display against live F# values |
| `tests/FSharp.ExpressionEvaluator.ExpressionCompiler.Tests` | Frame names from F# fixtures read back through System.Reflection.Metadata; expressions compiled against this assembly as the debuggee |
| `tests/TestDebuggee` | F# app that stops in a module function, a closure, a member, a generic function, a task and a `function`, plus a loop for a conditional breakpoint and a tracepoint; net10.0 (x64) and net48 (x86) |
| `eng/` | Build the private compiler; install into the Exp instance; drive it through DTE |
| `docs/DESIGN.md` | Design and milestone results |

## Build, test, try

Requires Windows, the .NET 10 SDK, a .NET 11 runtime (preview: `artifacts\fsc` targets net11.0)
and Visual Studio 2026 (18.x, Insiders included). Run `eng\Build-Compiler.ps1` first (see
above).

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

To check the deployed evaluator in the live debugger:

```powershell
powershell -File eng\Test-Debugger.ps1 -Target net10.0
```

`Test-Debugger.ps1` runs `Probe-Debugger.ps1` three times, with expected values. It checks
watch values, types, Locals rows, assignments, a conditional breakpoint and a tracepoint, and
exits with 1 when a check fails. Use `-Target net48` too. `Probe-Debugger.ps1` takes the same
checks:

| Option | Checks |
|---|---|
| `-Expect 'break: expression => value'` | a Watch value (`!error` for an expression that must fail) |
| `-ExpectHex 'break: expression => value'` | the same, with Hexadecimal Display on |
| `-ExpectType 'break: expression => type'` | a Watch value's Type column |
| `-ExpectLocalType 'break: name => type'` | a Locals row's Type column |
| `-ExpectLocalChild 'break: row / child => value'` | a row under a Locals row |
| `-Expand 'break: expression'` | that a value has children, and lists them |
| `-Assign 'name=value'` | an assignment, read back |
| `-AssignLocal 'break: row [/ child] = value'` | an edit of a Locals row, which compiles an assignment to the row's full name |
| `-ExpectLog 'text'` | a line in the EE log, such as a call stack name |
| `-Condition`, `-Tracepoint`, `-ExpectTrace`, `-ExpectEnd` | a conditional breakpoint or a tracepoint on the line marked `// probe: breakpoint` |

The components log to `%FSHARP_EE_LOG%` if set, otherwise to
`%LOCALAPPDATA%\FSharpExpressionEvaluator\ee.log`. `Probe-Debugger.ps1` sets
`FSHARP_EE_LOG` to `artifacts\logs\ee.log` (its `-Log` parameter changes that).
