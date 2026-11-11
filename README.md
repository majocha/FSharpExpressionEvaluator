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

Primitives are F# literals too, at top level and as the rows under F# values, with F# type
names: `radius = 2.0 : float`, `"a\tb"`, `'q'`, `1.5M`, `nan`. The value column has no integer
suffix (the Type column is beside it); an edit starts from the F# literal (`5L`) and takes F#
(`2.5`, `"a\tb"`). Hexadecimal Display works as before, and the rows keep C#'s flags, string
visualizer and underlying string (the text a visualizer and a raw copy use).

Expanding a value shows its F# fields, list items, map entries or captured values. A
**Raw View** row shows the raw .NET members. Everything else is rendered by the C# result
provider.

**Add Watch** on a row under an F# value puts an F# expression in the Watch window, which
evaluates to the same value:

| Row | Expression |
|---|---|
| record or anonymous record field, ref cell | `point.X`, `anon.Count`, `cell.contents` |
| list item, array element | `xs[1]`, `nested[0][1]`, `grid[1, 2]` |
| tuple item | `tuple.Item2`, `big.Rest.Item1`; struct tuples `(let struct (_, x) = pair in x)` |
| union case field | `(match shape with Program.Shape.Circle x -> x \| _ -> failwith "not Circle")` |
| F# exception field | `(match (error :> exn) with Program.MyError (x, _) -> x \| _ -> failwith "not MyError")` |
| `option` / `Lazy` value | `opt.Value`, `created.Value` |
| map entry | `map["two"]` for a key that is a literal, else `(Seq.item 0 points).Value` |
| set item | `Seq.item 1 set` |

Rows that F# can't assign to (tuple and list items, map entries, union case and anonymous record
fields) are read-only; record fields and array elements can be edited. A closure's captured
values have no expression outside the closure's own frame.

**Call stack and breakpoint locations** in F# frames:

```
Program.inspect (xs: int list) (name: string) (trip: float<m>)
Program.generic<int list> (value: int list) (pair: int * string)
Program.f@66 (x: int)
Program.work@24 (state machine)
Program.fetch@69-2 (async)
Program.numbers@12 (seq)
Program.Counter.Increment (by: int)
```

Names show curried argument groups, tuple parameters, `()`, operators and source names. Code
the compiler generates keeps the name F# gives it (`f@66`, as decompiled code and error messages
show it), without `.Invoke`; the code of `task { }`, `async { }` and `seq { }` is marked. Names
also show the frame's actual generic arguments and, optionally, argument values. When the F# compiler has loaded the debuggee's assemblies (from the first
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

In the **Immediate window**, `let` declares a variable that later expressions, in Watch and
Immediate and at later breaks, read by its name:

```
let digits = xs |> List.map string     ["1"; "2"; "3"]
let mutable total = List.sum xs        6
total <- total + 10
digits <- []                           error FS0027: This value is not mutable...
```

A variable is listed in Locals, where its row can be edited. A value whose type the statement
itself defines (`let r = {| A = 1 |}`) is declared as `obj`, and the statement says so.

Expressions are F# only. The C# debugger's format specifiers (`x, h`, `s, nq`) are not
accepted, so `x, h` is a tuple. Format with F# instead (`$"%x{x}"`), and use Hexadecimal
Display for hex. The only specifiers split off are the ones Visual Studio writes into the
names of Raw View and Results View rows itself (`, raw`, `, results`).

**Locals and types in F# frames** (M5). Locals shows the variables as the source has them:

| In Locals | Why |
|---|---|
| `captured`, `counter` in a closure | a closure's captured values, as variables of the frame |
| `doubled` in `task { }` or `seq { }` | the hoisted variables of the state machine, without its plumbing fields |
| in `task { }`: only what is in scope | a hoisted variable shows from its `let` to the end of its scope; one that shadows another keeps its name (`label`, `label (shadowed)`); the builder's awaiters and enumerators are under `[compiler generated]` |
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
| `tests/TestDebuggee` | F# app that stops in a module function, a closure, a member, a generic function, a task and a `function`, plus a loop for a conditional breakpoint and a tracepoint; net11.0 (x64) and net48 (x86) |
| `eng/` | Build the private compiler; install into the Exp instance; drive it through DTE |
| `docs/DESIGN.md` | Design and milestone results |

## Build, test, try

Requires Windows and the .NET 11 SDK
and Visual Studio 2026 (18.x, Insiders included). Run `eng\Build-Compiler.ps1` first (see
above).

```powershell
dotnet build FSharp.ExpressionEvaluator.slnx
dotnet test FSharp.ExpressionEvaluator.slnx
```

Versions are `Major.Minor.Commits`: `VersionMajorMinor` in `Directory.Build.props` (set by hand),
then the number of commits to HEAD (`Directory.Build.targets`). The VSIX and the file versions use
it; assembly versions stay at `Major.Minor.0.0`. Pass `-p:GitHeight=<n>` to set the last part.

To install into the Exp instance and see it in action:

```powershell
powershell -File eng\Deploy-Experimental.ps1 -SkipBuild
powershell -File eng\Probe-Debugger.ps1 -Target net11.0
```

To reinstall into the normal VS instance (Release by default; close VS first):

```powershell
powershell -File eng\Install-Vsix.ps1
```

The GitHub workflow `.github/workflows/build.yml` builds the private compiler (cached by the files
that build it), then builds and tests each push and pull request. Each workflow run keeps the VSIX
as an artifact.

- **Open VSIX Gallery**: each push to `master` uploads the VSIX to the
  [Open VSIX Gallery](https://www.vsixgallery.com/extension/FSharp.ExpressionEvaluator.f049de54-75e7-4bb4-9839-fb6993919394).
  This gives nightly builds. No account or token is necessary.
- **Visual Studio Marketplace**: put `[release]` in the commit message, or start the workflow
  manually. The workflow then publishes to the Marketplace and makes a GitHub release with
  the tag `v<version>`. The repository secret `VS_MARKETPLACE_PAT` must contain the token (see below).

To get the nightly builds in Visual Studio, add an additional extension gallery in **Tools >
Options > Environment > Extensions** with the URL
`https://www.vsixgallery.com/feed/extension/FSharp.ExpressionEvaluator.f049de54-75e7-4bb4-9839-fb6993919394`.

To publish to the Visual Studio Marketplace from your computer, commit all changes first. Then run:

```powershell
$env:VS_MARKETPLACE_PAT = '<token>'
powershell -File eng\Publish-Vsix.ps1
```

The token is an Azure DevOps personal access token with the **Marketplace (Manage)** scope and
**All accessible organizations**. The listing is in `src\FSharp.ExpressionEvaluator.Vsix\publishManifest.json`,
and the Marketplace page is `overview.md` in the same folder. The Marketplace does not accept a
version again, so each upload needs a new commit.

`Probe-Debugger.ps1` starts the Exp instance and attaches to the test debuggee. At each
break it prints the call stack, the locals and some watch values. Use `-Target net48` for an
x86 process debugged through msvsmon. Run the scripts with Windows PowerShell 5.1. You can
also open `tests\TestDebuggee` in the Exp instance and debug it by hand.

To check the deployed evaluator in the live debugger:

```powershell
powershell -File eng\Test-Debugger.ps1 -Target net11.0
```

`Test-Debugger.ps1` runs `Probe-Debugger.ps1` four times, with expected values. It checks
watch values, types, Locals rows, full names, Immediate-window variables, assignments, call stack
names, a conditional breakpoint and a tracepoint, and exits with 1 when a check fails. Use `-Target net48` too. `Probe-Debugger.ps1` takes the same
checks:

| Option | Checks |
|---|---|
| `-Expect 'break: expression => value'` | a Watch value (`!error` for an expression that must fail) |
| `-ExpectHex 'break: expression => value'` | the same, with Hexadecimal Display on |
| `-ExpectType 'break: expression => type'` | a Watch value's Type column |
| `-ExpectLocalType 'break: name => type'` | a Locals row's Type column |
| `-ExpectLocalChild 'break: row / child => value'` | a row under a Locals row |
| `-ExpectFullName 'break: row / child => expression'` | the expression Add Watch uses for a row under a Locals row (logged with `FSHARP_EE_LOG_FULL_NAMES=1`), and that it evaluates to the row's value; `[read-only]` or `[editable]` at the end checks that too |
| `-Immediate 'break: statement [=> value]'` | a statement of the Immediate window, such as `let x = ...` |
| `-ExpectFrame 'break: name'` | the frame's name (`*` matches anything); `-Frames` runs TestDebuggee's breaks in lambdas, an inner function, `async` and `seq` instead |
| `-Expand 'break: expression'` | that a value has children, and lists them |
| `-Assign 'name=value'` | an assignment, read back |
| `-AssignLocal 'break: row [/ child] = value'` | an edit of a Locals row, which compiles an assignment to the row's full name |
| `-ExpectLog 'text'` | a line in the EE log, such as a call stack name |
| `-Condition`, `-Tracepoint`, `-ExpectTrace`, `-ExpectEnd` | a conditional breakpoint or a tracepoint on the line marked `// probe: breakpoint` |

The components log to `%FSHARP_EE_LOG%` if set, otherwise to
`%LOCALAPPDATA%\FSharpExpressionEvaluator\ee.log`. `Probe-Debugger.ps1` sets
`FSHARP_EE_LOG` to `artifacts\logs\ee.log` (its `-Log` parameter changes that).

By default the log gets only errors, the lines that show when a component loads, and timings
(sessions, compiler warm-up). These environment variables add more lines. Set them in the environment
of devenv (and of msvsmon when it runs the result provider):

| Variable | Adds |
| --- | --- |
| `FSHARP_EE_LOG_VERBOSE=1` | each call stack frame name (with argument values when the debugger shows them), each expression text, the F# resources of each module. These lines can contain your data. |
| `FSHARP_EE_LOG_FULL_NAMES=1` | the full name of each row that the result provider lists |
| `FSHARP_EE_TRACE=1` | the start and the completion of each asynchronous call |

`Probe-Debugger.ps1` sets the first two. Several processes can write to the log at the same time.
When the log is larger than 4 MB, the first line of a process moves it to `ee.log.1` (this replaces
the previous `ee.log.1`) and starts a new `ee.log`.

## License

[MIT](LICENSE).
