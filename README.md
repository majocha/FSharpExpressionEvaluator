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

Generic types that are not specifically recognised are displayed using their
simple class name with F# angle-bracket syntax (e.g. `MyGeneric<int>`).

## Architecture

```
ExpressionEvaluator.csproj          ← VSIX host project (C#)
FSharp.ExpressionEvaluator/
  TypeNameFormatter.fs              ← Pure F# formatting logic (no VS APIs)
  Formatter.fs                      ← IDkmClrFormatter implementation
  ExpressionCompiler.fs             ← IDkmClrExpressionCompiler skeleton
  Formatter.vsdconfigxml            ← Concord component registration (Formatter)
  Compiler.vsdconfigxml             ← Concord component registration (compiler – not yet active)
FSharp.ExpressionEvaluator.Tests/
  TypeNameFormatterTests.fs         ← xUnit tests for TypeNameFormatter
```

### Key design decisions

* **`TypeNameFormatter`** is a pure F# module—it works on plain strings and
  lists and has no dependency on any VS/Concord API.  This makes it directly
  unit-testable and re-usable without VS being installed.

* **`Formatter`** bridges the Concord API world: it calls
  `DkmClrType.GetLmrType()` to obtain an
  `Microsoft.VisualStudio.Debugger.Metadata.Type` (the LMR type—a
  reflection-like object representing the type in the debugged process) and
  recursively formats it using `TypeNameFormatter`.

* **`ExpressionCompiler`** is an architectural skeleton for future F#-specific
  expression compilation.  It is not yet registered with the component system;
  see the [Roadmap](#roadmap) section below.

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
automatically via `Directory.Build.targets`):

```
dotnet test FSharp.ExpressionEvaluator.Tests/FSharp.ExpressionEvaluator.Tests.fsproj
```

## Running the tests

```
dotnet test FSharp.ExpressionEvaluator.Tests/FSharp.ExpressionEvaluator.Tests.fsproj
```

36 xUnit tests cover the `TypeNameFormatter` module (primitives, F# library
types, tuples, arrays, nested generics, fallback behaviour).

## Roadmap

### Next: ExpressionCompiler registration

The `ExpressionCompiler.fs` skeleton can be registered by:

1. Adding `Compiler.vsdconfigxml` to the `VsdConfigXmlFiles` item group in
   `FSharp.ExpressionEvaluator.fsproj`.
2. Changing the component's `<NoFilter />` to
   `<Filter><LanguageId RequiredValue="ab4f38c9-b6e6-43ba-be3b-58080b2ccce3"/></Filter>`
   so it only intercepts F# sessions.
3. Implementing the three methods—initially by throwing
   `NotImplementedException` (which maps to E\_NOTIMPL and causes the Concord
   host to fall through to the default C# EE), later by preprocessing F#
   expressions or by invoking the F# compiler service directly.

### Longer term

* **F#-specific value formatting** in `GetValueString`: display discriminated
  union cases as `Case value` rather than raw field projections, render
  `FSharpList` as `[a; b; c]`, etc.  This requires walking `DkmClrValue`
  children via the Concord inspection API.

* **Expression preprocessing**: transform idiomatic F# syntax (pipeline
  operators `|>`, `||>`, lambda shorthands, `match` expressions) into an
  equivalent form that the underlying C# EE can evaluate.

* **Full F# compilation**: invoke FSharp.Compiler.Service in-process to
  produce MSIL for arbitrary F# expressions, eliminating the C# EE dependency.
