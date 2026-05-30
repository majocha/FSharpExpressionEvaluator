# F# Expression Evaluator

A Visual Studio debugger extension that provides F#-aware formatting for the
VS Concord debugger engine.

## What this extension does

The extension registers with Visual Studio's Concord extensibility system and
intercepts the `IDkmClrFormatter` interface **for F# debugging sessions only**
(filtered to the F# language ID `ab4f38c9-b6e6-43ba-be3b-58080b2ccce3`).

### Type-name formatting

When hovering over a value or inspecting variables in the debugger, CLR type
names are displayed using idiomatic F# syntax instead of the raw .NET names.

| CLR type | Displayed as |
|---|---|
| `System.Int32` | `int` |
| `System.String` | `string` |
| `System.Boolean` | `bool` |
| `System.Double` | `float` |
| `Microsoft.FSharp.Core.Unit` | `unit` |
| `FSharpOption<int>` | `int option` |
| `FSharpValueOption<int>` | `int voption` |
| `FSharpList<string>` | `string list` |
| `FSharpRef<int>` | `int ref` |
| `FSharpSet<int>` | `Set<int>` |
| `FSharpMap<string,int>` | `Map<string, int>` |
| `FSharpFunc<int,string>` | `int -> string` |
| `IEnumerable<float>` | `float seq` |
| `Tuple<int,string>` | `(int * string)` |
| `ValueTuple<int,string>` | `struct (int * string)` |
| `List<int>` *(BCL)* | `ResizeArray<int>` |
| `int[]` | `int[]` |
| `int[,]` | `int[,]` |
| DU / record / class | Simple name, e.g. `Color`, `Point` |
| `<>f__AnonymousType0<int,string>` | `{| Age: int; Name: string |}` |
| Struct anonymous record | `struct {| X: float; Y: float |}` |

Generic types that are not specifically recognised are displayed using their
simple class name with F# angle-bracket syntax (e.g. `MyGeneric<int>`).

#### Anonymous record formatting

F# anonymous records (`{| … |}`) and C# anonymous types both compile to
compiler-generated sealed generic classes whose CLR names begin with
`<>f__AnonymousType`.  The formatter detects this naming convention, reads
the public readable properties of the type via LMR reflection, and displays
the type as a proper F# anonymous record signature.

For example, given `let anon = {| Name = "Alice"; Age = 30 |}`, the **Type**
column in the Locals window shows `{| Age: int; Name: string |}` instead of
the raw `<>f__AnonymousType0'2[System.Int32,System.String]`.

> **Note on FSharp.Compiler.Service**: Using FCS for type-name formatting was
> considered but is not appropriate here.  FCS would require loading assembly
> metadata and/or F# source files at debug time—expensive operations that
> would noticeably slow the debugger.  The LMR (Lightweight Metadata Reader)
> API already exposes everything needed (type names, generic
> arguments, and property metadata) with no runtime cost penalty.

### Value formatting

`GetValueString` applies F#-specific post-processing to the raw value string
returned by the default CLR formatter:

| F# type | Default C# EE value | With F# EE |
|---|---|---|
| `unit` | `{}` | `()` |
| `char` | `65 'A'` | `'A'` |

#### Locals window example

Given this F# code stopped at a breakpoint:

```fsharp
let greet (name: string) =
    let initial : char = name.[0]
    let result  : unit = printfn "Hello, %s!" name
    initial, result
```

Without the F# Expression Evaluator the **Locals** window would show:

| Name | Value | Type |
|---|---|---|
| `name` | `"Alice"` | `System.String` |
| `initial` | `65 'A'` | `System.Char` |
| `result` | `{}` | `Microsoft.FSharp.Core.Unit` |

With the F# Expression Evaluator installed:

| Name | Value | Type |
|---|---|---|
| `name` | `"Alice"` | `string` |
| `initial` | `'A'` | `char` |
| `result` | `()` | `unit` |

## Architecture

```
ExpressionEvaluator.csproj          ← VSIX host project (C#)
FSharp.ExpressionEvaluator/
  TypeNameFormatter.fs              ← Pure type-name formatting logic (no VS APIs)
  ValueFormatter.fs                 ← Pure value-string formatting logic (no VS APIs)
  ExpressionLowering.fs             ← Pure F# → C#-EE expression lowering logic
  Formatter.fs                      ← IDkmClrFormatter implementation
  ExpressionCompiler.fs             ← IDkmClrExpressionCompiler delegating via lowering
  Formatter.vsdconfigxml            ← Concord component registration (Formatter)
  Compiler.vsdconfigxml             ← Concord component registration (compiler)
FSharp.ExpressionEvaluator.Tests/
  ExpressionLoweringTests.fs        ← xUnit tests for ExpressionLowering
  TypeNameFormatterTests.fs         ← xUnit tests for TypeNameFormatter
  ValueFormatterTests.fs            ← xUnit tests for ValueFormatter
FSharp.DebugSample/
  Program.fs                        ← F# console app for live-testing the extension
```

### Key design decisions

* **`TypeNameFormatter`** is a pure F# module—it works on plain strings and
  lists and has no dependency on any VS/Concord API.  This makes it directly
  unit-testable and re-usable without VS being installed.

* **`ValueFormatter`** is likewise a pure module—it post-processes the raw
  value string returned by the default CLR formatter and applies F#-specific
  rules (e.g. `unit` → `()`, strip char code-point prefix).  Having no VS
  dependency keeps it fully unit-testable.

* **`Formatter`** bridges the Concord API world: it calls
  `DkmClrType.GetLmrType()` to obtain an
  `Microsoft.VisualStudio.Debugger.Metadata.Type` (an LMR—Lightweight
  Metadata Reader—type representing the type in the debugged process) and
  recursively formats it using `TypeNameFormatter` for the type column and
  `ValueFormatter` for the value column.

* **`ExpressionLowering`** is a pure F# module that lowers a small supported
  subset of F# expressions (currently identifiers, member access, tuple/indexer
  syntax, and initial `Seq`/`List` module calls) into forms that the built-in
  C# EE can compile.

* **`ExpressionCompiler`** is now registered for F# sessions and uses
  `ExpressionLowering` before delegating supported expressions to the built-in
  C# EE. Unsupported expressions still fall through safely.

### Roslyn reference

The overall structure mirrors the Roslyn Expression Evaluator under
`src/ExpressionEvaluator` (e.g. `CSharpFormatter.cs`,
`CSharpExpressionCompiler.cs`).  The separation between pure type-name
formatting logic and the VS API adaptor layer is inspired by how Roslyn keeps
its `TypeNameDecoder` separate from the `Formatter` class.

## Building

The extension targets Windows (Visual Studio 2022+) and requires the VS SDK.

On **Windows** (full build including VSIX packaging):

```
dotnet build ExpressionEvaluator.sln
```

On **Linux / macOS** (unit tests only—`vsdconfigtool.exe` is skipped
automatically via the OS condition on the `VsdConfigXmlFiles` item):

```
dotnet test FSharp.ExpressionEvaluator.Tests/FSharp.ExpressionEvaluator.Tests.fsproj
```

## Live debugging with the sample project

`FSharp.DebugSample/Program.fs` is a small F# console application that
exercises every type the formatter handles.  Use it to verify the extension
end-to-end in Visual Studio:

1. Build and install the VSIX extension.
2. Open the solution in Visual Studio 2022.
3. Set `FSharp.DebugSample` as the startup project.
4. Set a breakpoint on the `printfn ">>> Locals are ready for inspection."` line.
5. Press **F5**.
6. When the breakpoint is hit, open the **Locals** or **Watch** window and
   compare the Type column against the tables above.

Locals you will see and their expected types with the F# EE:

| Variable | Expected type |
|---|---|
| `n` | `int` |
| `f` | `float` |
| `b` | `bool` |
| `c` | `char` |
| `s` | `string` |
| `u` | `unit` |
| `pair` | `(int * string)` |
| `triple` | `(int * float * string)` |
| `opt` | `int option` |
| `vopt` | `float voption` |
| `lst` | `int list` |
| `arr` | `string[]` |
| `mapped` | `Map<string, int>` |
| `s_et` | `Set<int>` |
| `r` | `int ref` |
| `color` | `Color` |
| `shape` | `Shape` |
| `pt` | `Point` |
| `anon` | `{| Age: int; Name: string |}` |
| `anonStruct` | `struct {| X: float; Y: float |}` |
| `tree` | `BinaryTree<int>` |
| `add` | `int -> int -> int` |

## Running the tests

```
dotnet test FSharp.ExpressionEvaluator.Tests/FSharp.ExpressionEvaluator.Tests.fsproj
```

64 xUnit tests cover `TypeNameFormatter`, `ValueFormatter`, and
`ExpressionLowering` (primitives, F# library types, tuples, arrays, nested
generics, anonymous records, fallback behaviour, unit/char value formatting,
and the initial expression-lowering subset).

## Roadmap

### Next: extend the supported lowering subset

The compiler now lowers and delegates a small F# subset. The next increments
should stay inside the pure `ExpressionLowering` module first, then wire each
new form through `ExpressionCompiler`:

1. Add more FSharp.Core module coverage beyond the initial `Seq` and `List`
   support.
2. Support additional expression forms that already have direct C#-EE
   equivalents, such as more indexing/member-access combinations.
3. Keep `GetClrLocalVariableQuery` and unsupported expressions on the safe
   fallback path until there is a proven F#-specific implementation.

### Longer term

* **Discriminated union value formatting** in `GetValueString`: display DU
  cases as `Case value` rather than raw field projections, render
  `FSharpList` as `[a; b; c]`, render `FSharpOption` as `Some value` /
  `None`.  This requires walking `DkmClrValue` children via the Concord
  inspection API.

* **Expression preprocessing**: transform idiomatic F# syntax (pipeline
  operators `|>`, `||>`, lambda shorthands, `match` expressions) into an
  equivalent form that the underlying C# EE can evaluate.

* **Full F# compilation**: invoke FSharp.Compiler.Service in-process to
  produce MSIL for arbitrary F# expressions, eliminating the C# EE dependency.
