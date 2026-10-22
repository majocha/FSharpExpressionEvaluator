# F# Expression Evaluator for Visual Studio — Design

Status: **draft for review** (2026-10-22). M0 (spikes and the F#-only VSIX layout) is done; see §13.

This document says what an F# expression evaluator (EE) for the Visual Studio
managed debugger should do, and how. It is based on:

- the Concord extensibility docs (*CLR Expression Evaluators*, *Managed
  Expression Evaluator Sample*, *CLR Expression Evaluator Intrinsics*,
  *Component Levels*, *Component discovery and configuration*);
- Roslyn's C#/VB EE (`roslyn/src/ExpressionEvaluator`, local clone at `72324baac53`);
- the F# compiler (`fsharp`, local clone at `6f98d9ac8a`) and FSharp.Core;
- what VS 2026 (18.x Insiders, Concord 18.12) actually ships on this machine;
- `ThemeFontWeight`, an existing VSIX written only in F#.

Statements marked **(verify)** were assumptions to confirm before building on
them. M0 confirmed most of them (§13). The two still open are marked
**(verify, M3)**.

---

## 1. Today

### 1.1 What VS does for F# today

F# PDBs carry the F# language GUID `AB4F38C9-B6E6-43BA-BE3B-58080B2CCCE3`.
Concord has no `DkmLanguageId` constant for it; the VS 18.12 API has constants
for C#, VB, C++, Rust and others, but not F#. No component in VS registers for
that language. Roslyn registers its C# compiler and result provider with
`<NoFilter/>` and `Priority="Low"`, so **C# handles every F# frame**:

- Watch, Immediate, DataTips and conditional breakpoints parse **C# syntax**.
  Simple member access looks fine, but `xs |> List.map f`, `match`, `fun`,
  `x.[0]` and F# operators fail.
- Values show in C# form: `FSharpList<int>`, `Tuple<int, string>`,
  `FSharpFunc<int, int>`, and `null` for `None`/`()`. F# records and unions
  display through `[DebuggerDisplay("{__DebugDisplay(),nq}")]`, which the F#
  compiler emits (`IlxGen.fs`, `debugDisplayMethodName`). That calls
  `sprintf "%+0.8A"` **by func-eval** in the debuggee, so it is slow, can
  time out, needs func-eval, and fails in dumps.
- Expanding a value shows compiler plumbing: `Tag`, `IsCons`, `X@` backing
  fields, `_unique_None`, closure classes, `<StartupCode$...>`.
- Call stacks show compiled names: `Program.f@12.Invoke(Int32 x)`,
  `MoveNext()`, curried arguments flattened into one list.
- Locals show compiler temporaries such as `matchValue`, `copyOfStruct`,
  `_arg1`, `patternInput` and `tupledArg`. Mutable locals captured by closures
  show as `FSharpRef<int>`.

### 1.2 What is in this repo

| Item | State |
|---|---|
| `ExpressionEvaluator.csproj` + `Properties/AssemblyInfo.cs` | Legacy C# VSIX shell that wraps the F# project. Targets VS `[17.0,18.0)`, so it **excludes VS 2026**. |
| `FSharp.ExpressionEvaluator.vsdconfig` (root, binary) | Stale build output from the HelloWorld sample (`HelloWorldService`). Delete it. |
| `Compiler.vsdconfigxml` | Registers a non-existent `ExpressionCompiler` class with `<NoFilter/>` at the C# level. The project doesn't currently build it, but if it were enabled it would **take over C# and VB** as well. |
| `Formatter.fs` / `Formatter.vsdconfigxml` | An `IDkmClrFormatter` filtered to F#, at 63000. An unregistered `ResultProvider` that only forwards calls. |
| `sformat.fs/.fsi` (untracked) | A copy of FSharp.Core's `%A` engine, built with `COMPILER` so it lands in `FSharp.Compiler.Text`. |

Two findings shape the result-provider design:

1. **An `IDkmClrFormatter` alone has almost no effect.** Roslyn's
   `ResultProvider` formats through its own `Formatter2`/`FullNameProvider`
   instances, which it gets in its constructor (`ResultProvider.cs:53`). It
   does not go back through Concord dispatch, so a separate F# formatter is
   bypassed for everything the C# result provider renders. To change what
   users see, we must own **`IDkmClrResultProvider`**.
2. **`sformat.fs` cannot be reused as is.** It walks *live* objects with
   `System.Reflection`/`FSharp.Reflection`. A result provider sees
   *remote* values (`DkmClrValue`) and *LMR* types
   (`Microsoft.VisualStudio.Debugger.Metadata`). LMR is a hierarchy *parallel*
   to System.Reflection (`Metadata.Type : Metadata.MemberInfo : Object`,
   confirmed in M1), so `FSharpType.IsUnion` and friends cannot even be
   called on it. `DkmClrValue.HostObjectValue` holds a value only for
   primitives and strings. We keep its *policy* (F# literal syntax, `[a; b]`,
   `{ X = 1 }`, `Some x`, limits on print length and depth) and drop the code.
   See §5.3.

---

## 2. How Concord EEs work (summary)

```
 UI (Watch / Locals / DataTip / Immediate / BP condition)
   │ text + DkmInspectionContext (language = frame's PDB language)
   ▼
 Managed EE (Microsoft, IDkmLanguageExpressionEvaluator)  ── don't replace
   │ dispatch by vsdconfig filter (LanguageId)
   ▼
 IDkmClrExpressionCompiler  ──► PE bytes: class <>x { static T <>m0(args…) { locals…; expr } }
   │                              (params and leading locals mirror the frame, in the same order)
   ▼
 CLR Inspector: IL interpreter + func-eval (may run in msvsmon)
   │ raw DkmClrValue
   ▼
 IDkmClrResultProvider (+ IDkmClrFormatter/FullNameProvider)
   │ DkmEvaluationResult (name, value, type, full name, children)
   ▼
 UI
```

Other interfaces involved:

- `IDkmClrExpressionCompiler`: `CompileExpression`, `CompileAssignment` and
  `GetClrLocalVariableQuery`, plus `IDkmClrExpressionCompiler1714` with
  `GetClrLocalVariableQuery` that can return an error.
- `IDkmClrExpressionCompilerCallback.CompileDisplayAttribute`: compiles
  `[DebuggerDisplay]` expressions.
- `IDkmLanguageFrameDecoder` (+`1714.GetBindableFrameName`) and
  `IDkmLanguageInstructionDecoder`: call stack and breakpoint names.
- `IDkmRuntimeFunctionResolver`: function breakpoints. Roslyn's C# resolver
  has no language filter.
- Intrinsics are calls to `Microsoft.VisualStudio.Debugger.Clr.IntrinsicMethods`
  (`GetException`, `GetReturnValue`, `GetObjectByAlias`, `GetVariableAddress<T>`,
  `CreateVariable`, `GetObjectAtAddress`). Their metadata comes from
  `DkmClrRuntimeInstance.GetIntrinsicAssemblyMetaDataBytesPtr`. Alias lists
  come from `DkmClrRuntimeInstance.GetAliases`.
- **Chaining**: an implementation can handle some inputs and pass the rest on
  by calling the same method on the first parameter, which "goes to the next
  implementer". §5.1 depends on this.

Roslyn deploys its components like this (VS 18 install, verified):

| Roslyn component | Interfaces | Level | Where it runs |
|---|---|---|---|
| `CSharpExpressionCompiler` | ExpressionCompiler, Callback, FrameDecoder, InstructionDecoder, module notifications | 9 991 500, `Synchronized` | IDE (devenv) only |
| `CSharpResultProvider` / `CSharpFormatter` | ResultProvider; Formatter(2), FullNameProvider(2) | 63 000 | Monitor side: devenv, **and** `Remote Debugger\x86`/`x64` (msvsmon), and vsdbg |
| `FunctionResolver` | `IDkmRuntimeFunctionResolver` | 65 100 | Monitor side |

So the result provider is a **portable `netstandard2.0` assembly with minimal
dependencies**, because it runs inside msvsmon. The compiler, which pulls in
all of Roslyn, runs only in the IDE.

Roslyn's compiler pipeline, which we follow:

1. **Metadata blocks.** Get a pointer to the in-memory metadata of every module
   in the AppDomain (`DkmClrModuleInstance.GetMetaDataBytesPtr`) plus the
   intrinsics assembly. Build a `Compilation` from them, without reading files
   from disk.
2. **Cache.** Keep the compilation and the last method context on the
   `DkmClrAppDomain` as a `DkmDataItem`. Reuse it while the method, version
   and IL-offset scope are unchanged (`MethodContextReuseConstraints`). Drop it
   on module load, unload, modify or metadata invalidation.
3. **Retry.** If assembly identities are ambiguous or missing, recompile with
   direct references only (`TryCompileWithRetry`).
4. **Method context.** From the method token, IL offset and local signature
   token, plus the PDB (`GetSymReader` → scopes, local names, imports, CDIs),
   build the in-scope variables. This includes display-class and state-machine
   hoisted variables.
5. **Bind.** Bind against a *synthesized* method `<>x.<>m0` whose parameters and
   leading locals copy the frame. Binder flags: `IgnoreAccessibility`,
   `SuppressObsoleteChecks`, unsafe allowed. `MetadataImportOptions.All`.
6. **Emit.** Produce PE bytes, then `DkmCompiledClrInspectionQuery.Create(...)`
   with result flags (`PotentialSideEffect`, `BoolResult`, `ReadOnlyResult`),
   category and access, plus `DkmClrCustomTypeInfo` (C# uses it for
   `dynamic` and tuple names).

---

## 3. Goals and non-goals

### 3.1 What F# users should get

**Display (result provider). This is the highest value and the lowest risk.**

| Value | Today (C# EE) | F# EE |
|---|---|---|
| `[1; 2; 3]` | `Length = 3` / func-eval `DebugDisplay` | `[1; 2; 3]` (read from memory, no func-eval) |
| `Some 42` / `None` | `{Some(42)}` / `null` | `Some 42` / `None` |
| `()` | `null` | `()` |
| record | `{ X = 1 ... }` by func-eval `%A` | `{ X = 1; Y = "a" }` read from memory |
| union | func-eval `%A` | `Circle 2.0`, `Rect (1.0, 2.0)` |
| tuple | `(1, "a")` typed `Tuple<int, string>` | `(1, "a")` typed `int * string` |
| struct tuple | C# tuple | `struct (1, "a")` |
| closure | `{Program+f@12}` | `<fun:f@12>` typed `int -> int`; expands to captured values |
| `int ref` | `{contents = 1}` | `{ contents = 1 }` typed `int ref` |
| `Map`/`Set` | `Count = 3` + proxy (func-eval) | `map [(1, "a"); ...]`, items listed in key order |
| `Lazy<_>` | default | `<unevaluated>` or the value |
| type names | `FSharpList<FSharpOption<int>>` | `int option list` |
| strings/chars | C# escapes | F# escapes |
| float | `1` | `1.0` |

Expansion hides `Tag`, `Is*`, `_unique_*`, `X@` backing fields and
`<StartupCode$…>`. Union fields use their declared names, list items appear
as `[0]`, `[1]`, … and records show their fields. Non-public members and Raw
View stay available.

**Expressions (expression compiler).** F# syntax in Watch, Immediate,
DataTips, conditional breakpoints, tracepoints (`{expr}`) and value editing:

- identifiers: locals, parameters, the self identifier (`this`/`x`/`__`),
  class `let` fields, closure-captured values, and module values reachable
  through the `open`s in scope;
- member access, `.[i]` and `[i]` indexing, method calls (func-eval), F#
  function application, partial application, operators (inlined through
  FSharp.Core optimization data), `if/then/else`, `match`, `let … in`,
  `fun` lambdas, `|>`, `:?`, `:>`, `:?>`, `typeof<_>`, `nameof`, string
  interpolation, `seq { … }` and list/array expressions;
- assignment: `x <- v`, `r.F <- v`, `arr[i] <- v`. Locals the user did not
  declare `mutable` can still be changed (see §6.6);
- pseudo-variables `$exception`, `$ReturnValue`, `$ReturnValueN`, `$1`… and
  `$stowedexception`;
- format specifiers (`, h`, `, d`, `, nq`, `, raw`, `, results`, `, hidden`,
  `, nse`, `, ac`), with F#'s tuple comma handled as in §6.7.

**Call stack (frame and instruction decoders).** F#-shaped names, see §7.

### 3.2 Non-goals for v1

- Replacing `IDkmLanguageExpressionEvaluator` or the CLR inspector.
- VS Code / vsdbg / Linux remote. The design keeps this possible
  (the RP is portable; the compiler core is host-agnostic) but we won't ship it.
- `async { }` / `task { }` / `let!` in expressions. The debugger cannot await.
- Declaring persistent variables in Immediate (`let x = …` through
  `CreateVariable`). Deferred to v2.
- Erased type-provider types. They do not exist at runtime.
- `[DebuggerDisplay]` strings written in F# syntax (§5.5).
- Debugging F# Interactive sessions. Works only where the dynamic assemblies
  carry metadata and sigdata (§6.2), and we won't test it in v1.

---

## 4. Components and deployment

| Component | Assembly | Interfaces | Level / filter | Runs in | Dependencies |
|---|---|---|---|---|---|
| **F# Result Provider** | `FSharp.ExpressionEvaluator.ResultProvider` (`netstandard2.0`) | `IDkmClrResultProvider`, `IDkmClrFormatter`, `IDkmClrFormatter2`, `IDkmClrFullNameProvider(2)` | 63 000, `LanguageId = F#` | monitor side (devenv; msvsmon for remote and for x86 targets) | Concord Engine + Metadata, FSharp.Core. **No FCS.** |
| **F# Expression Compiler** | `FSharp.ExpressionEvaluator.ExpressionCompiler` (`net48`) | `IDkmClrExpressionCompiler`, `…1714`, module notifications (`MetaDataPointerInvalidated`, `ModuleModified`, `ModuleInstanceUnload`) | 9 991 500, `LanguageId = F#`, `Synchronized="true"` | IDE | the RP assembly (shared name/type logic), private FCS (§8.2), FSharp.Core (VS's) |
| **F# Frame/Instruction Decoder** | same assembly as the compiler, but it never touches FCS | `IDkmLanguageFrameDecoder(1714)`, `IDkmLanguageInstructionDecoder` | 9 991 500, `LanguageId = F#`, `RuntimeId = Clr, ClrNativeCompilation` | IDE | RP assembly only |

Notes:

- Every interface group filters on `LanguageId = AB4F38C9-…` so we never
  intercept C# or VB frames. With a filter and normal priority we take
  precedence over Roslyn's `NoFilter`/`Priority="Low"`. VB uses the same
  mechanism.
- FCS loads **lazily**, on the first `CompileExpression`. The decoders and
  Locals-only sessions never load it. Building the compiler state is started
  in the background at the first break in an F# frame (§6.9).
- The language is chosen **per frame**. In an F# frame, *every* value goes
  through the F# result provider, including C# objects and BCL collections.
  In a C# frame, F# values still go through C#. Concord gives us no hook
  for that.

---

## 5. Result Provider

### 5.1 Strategy: render F# shapes, delegate everything else

Porting Roslyn's ~7.4 k-line result provider core is the "own everything"
option. It is unnecessary for v1, and its internals are only visible to C# and
VB (`InternalsVisibleTo`), so we could not subclass it. Instead:

- `GetResult(value, …)`: classify the value's **declared and runtime type**
  (§5.2). For an **F# shape**, we build the `DkmEvaluationResult` and attach
  our own `DkmDataItem` that records how to expand it. **Otherwise we chain**:
  `value.GetResult(workList, declaredType, …, resultName, resultFullName, k)`
  goes to Roslyn's C# result provider. That keeps everything it already does:
  `DebuggerDisplay`, `DebuggerTypeProxy`, Results View, Dynamic View, Raw View,
  favorites, inline arrays, pointers and native view.
- `GetChildren` and `GetItems`: if the result or enum context carries our data
  item, we expand it. Otherwise we chain.
- When we expand an F# value, each child goes through *our* `GetResult`
  logic. So a `Dictionary` inside a record is delegated to C#, and a record
  inside that `Dictionary`'s proxy is shown by C#.

**Confirmed in M0** (§13): chaining works in both directions.

- A call made from inside our `GetResult` goes to the next implementer, which
  is Roslyn's. It never comes back to us, so there is no recursion.
- A call made from our `GetChildren` (through `DkmClrValue.GetResult`) is
  dispatched from the top, so it comes back to us.
- When Roslyn expands a result it created, it also creates each member's
  result through Concord. So `X`/`Y` of a chained record, or `radius` of a
  chained union case, arrive at our `GetResult`.

So an F# value nested inside something C# renders **does** reach the F# RP.
The remaining cost is cosmetic: the parent row and the generic arguments of
delegated types use C# type names. This is far better than today and costs a
fraction of a full port. If it proves limiting, porting Roslyn's core to F#
becomes v2 work.

### 5.2 Recognizing F# shapes without live reflection

We read shapes from metadata through LMR `CustomAttributeData`. The source of
truth is FSharp.Core's `CompilationMappingAttribute(SourceConstructFlags, …)`:

| Flag | Shape | Representation notes |
|---|---|---|
| `SumType` | union | `Tag` property; nested case classes; nullary cases as `_unique_X` singletons; `UseNullAsTrueValue` means `null` is the first nullary case (`None`); `[<Struct>]` unions; case/field names from `CompilationMapping(UnionCase/Field, …)` on members |
| `RecordType` | record, anonymous record | fields ordered by `CompilationMapping(Field, i)`; read backing fields `X@` directly |
| `ObjectType` | class/interface | delegate to C#, but use F# type names |
| `Exception` | F# exception | `Failure "msg"`-style display |
| `Module` | module (static class) | members are module values; `ModuleSuffix` handled |
| `Closure` | closure | `FSharpFunc<,>` subclass; fields are the captured values |

FSharp.Core types with special handling: `FSharpList`1` (walk `head`/`tail`),
`FSharpOption`1`, `FSharpValueOption`1`, `FSharpRef`1`, `Unit`,
`FSharpMap`2`/`FSharpSet`1` (walk the AVL tree in order), `FSharpResult`2`,
`FSharpChoice`, `Lazy<T>`, `FSharpAsync`1` (type name only), `BigInteger`.

**FSharp.Core version sensitivity.** List, option and union layout has been
stable since FSharp.Core 4.x. `MapTree` and `SetTree` changed shape (node
subclassing, FSharp.Core 5+). Each special walker therefore checks the field
layout it expects. If the layout is unknown, we delegate to C#, whose
`DebuggerTypeProxy` (`MapDebugView` and so on) still works through func-eval.

**No func-eval for F# shapes.** Everything in the table is a field read
(`DkmClrValue.GetFieldValue`, array element reads). So it works in dumps,
with func-eval disabled, and with `, nse`. If a user type has a *custom*
`[<DebuggerDisplay>]`, an overridden `ToString` or `[<StructuredFormatDisplay>]`,
we honor it. `__DebugDisplay` itself is ignored for F# shapes.

### 5.3 Formatting values and type names

- **Values** follow FSI/`%A` conventions. Some cases:
  - Floats always show a decimal point or exponent: `1.0`, `nan`, `infinity`.
  - Number suffixes appear only when hex display is off and the type is
    ambiguous: `1L` in previews, `1` in the value column next to the type.
  - Strings and chars use F# escapes; `, nq` turns quoting off.
  - Decimals print as `1.5M`.
  - Lists and arrays have a preview length limit (configurable, default 10
    items, then `...`). Nested previews have a depth limit (default 2).
  - A function value shows as `<fun:name@line>`. A union shows its case name
    and fields, tupled.
- **Type names** follow FSI conventions:
  - abbreviations: `int`, `float`, `float32`, `int64`, `uint`, `nativeint`,
    `obj`, `unit`, `exn`, `bigint`…
  - postfix generics: `int list`, `int option`, `int voption`, `int ref`,
    `int array`, `int[,]`, `seq<int>`;
  - `int * string`, `struct (int * string)`, `int -> int -> int` (recovered
    from nested `FSharpFunc` and `OptimizedClosures.FSharpFunc<_,_,_>`
    shapes);
  - `byref<int>`/`inref<int>`/`outref<int>`, generic parameters as `'T`, and
    nullness `string | null` where the metadata says so;
  - closure classes named after their function type. Compiler-generated names
    are cleaned: `f@12` → `f`. `CompilationSourceName` and `CompiledName` are
    mapped back to source names.
- **F#-only type information** (units of measure, abbreviations such as
  `type Id = int`, original function arity) is not in IL. It reaches the RP in
  two ways: the expression compiler attaches it as `DkmClrCustomTypeInfo` with
  an F# payload GUID (§6.8), and later from the PDB (§9). Without it we show
  the erased type.

The literal and layout helpers live in `Shared/FSharpDisplay.fs`. The RP and
the frame decoder both compile them; the decoder needs type names for
signatures.

### 5.4 Full names (Add Watch, edit value, drag to Watch)

`IDkmClrFullNameProvider` must return **F#-parseable** expressions, because the
F# compiler re-parses them:

| Child | Full name |
|---|---|
| record or class field/property | `r.X` (backing fields map to property names) |
| array element | `arr[3]`, `arr[1, 2]` |
| list item | `xs[3]` (`FSharpList.Item`; O(n), but correct) |
| map entry | `m["key"]` (only for keys that format as literals; otherwise none) |
| union field | `(match u with Shape.Circle (r) -> r \| _ -> Unchecked.defaultof<_>)` (positional pattern) |
| closure capture | the captured variable's name, which the compiler binds (§6.4) |
| cast | `(x :?> Ns.Derived)` |
| delegated (C#) child | C# full name. **Not** F#-parseable, so we wrap or rewrite where we can. Otherwise `null`, which disables Add Watch. |

The last row is a known gap of the hybrid approach.

### 5.5 `[DebuggerDisplay]` compilation

We **do not implement** `IDkmClrExpressionCompilerCallback` in v1. C#
(`NoFilter`) keeps compiling every `DebuggerDisplay` string. That is correct
for the BCL and C# libraries, whose attribute strings are written in C#. It also
works for the expressions FSharp.Core and the F# compiler generate:
`{__DebugDisplay(),nq}`, `{get_DebugDisplay(this),nq}` and `{Count}`. F#-syntax
display strings are a later, opt-in feature: try F#, then chain to C#.

---

## 6. Expression Compiler

### 6.1 Approach: typed-tree synthesis inside FCS

We considered two approaches.

- **S1, source synthesis plus IL rewriting.** Generate
  `let <>m0 (this) (args…) (locals…) = <expr>` as F# text, compile it with FCS,
  then rewrite `ldarg`s for locals into `ldloc`s and splice in the frame's
  local signature. It looks cheap, but it can't express byref-like locals,
  class `let` fields or closure fields. Captured locals become copies.
  Accessibility, in-memory references and emitting to bytes would *still* need
  FCS changes. **Rejected**, except possibly as a throwaway M0 spike.
- **S2, Roslyn-style binding in the typed tree. Chosen.** Inside FCS:
  1. Build `TcImports` from the debuggee's in-memory metadata (§6.2).
  2. Build a `TcEnv` for the frame (§6.3–6.5). Every in-scope variable is a
     synthetic `Val` with a **preassigned IlxGen storage**: `Arg n`,
     `Local n`, a field of `this`, `contents` of a ref cell, or a static field
     or property.
  3. Parse the expression with the normal parser, after the `$alias`
     preprocessing in §6.7.
  4. Run `TcExpr` with an **EE accessor domain** that grants all access, then
     post-inference checks with EE relaxations (§6.6).
  5. Run the optimizer only for mandatory inlining (`inline` and SRTP), with
     `--optimize-` and `--tailcalls-`, so no `tail.` prefixes are emitted.
  6. Run IlxGen with a new entry point that emits class `<>x` with static
     `<>m0`. Its parameters mirror the frame, including `this` as the first
     parameter for instance methods. **The local slots start pre-seeded with
     the frame's exact local signature**, including pinned slots, byrefs and
     modifiers. Query temporaries come after them. Closures and lambdas go into
     the same assembly as nested types.
  7. Write the IL to `byte[]` with the in-memory path that `fsi` multi-emit
     already uses.

  This matches how FSI already compiles incrementally: earlier interactions
  are `Val`s with `ValStorage.StaticProperty`. What the EE adds is new storage
  kinds plus a pre-seeded local frame. The core is a pure function:
  `(metadata blobs + F# resources, frame description, text) → PE bytes + result properties`.
  It does not depend on Concord, which lets us test it in-process and later run
  it out of process or inside vsdbg.

### 6.2 Metadata and F# signature data

The **critical F#-specific problem**: `GetMetaDataBytesPtr` returns the
ECMA-335 metadata only. F# signature and optimization data live in **managed
resources** in the PE image: `FSharpSignatureCompressedData.<asm>`,
`FSharpOptimizationCompressedData.<asm>` and the uncompressed `FSharpSignatureData…`
variants. Without them, FSharp.Core looks like a plain .NET library:

- no `inline` or SRTP operators, so `xs |> List.sum` fails;
- no module values or functions, no unions or records as F# types;
- no type abbreviations, no units of measure.

The plan:

1. For each `DkmClrModuleInstance` in the AppDomain, take the metadata blob
   (`GetMetaDataBytesPtr`) and its **MVID**.
2. For modules with an F# signature, mainly the current module, FSharp.Core
   and F# libraries:
   - read the F# resources from the **loaded image in debuggee memory**
     with small targeted reads:
     - read the PE headers and CLI header from `BaseAddress`
       (`DkmClrModuleInstance.MemoryLayout` is `MemoryPE`, so RVA equals offset);
     - take the resource offsets from the in-memory metadata;
     - read each blob.

     **Confirmed in M0:** one `ReadMemory` over the whole image fails on the
     unmapped gaps between sections, while targeted reads work. FSharp.Core's
     ~400 KB of F# resources read in 1–4 ms, byte-identical to the file
     (`PeImage.fs`);
   - fall back to the module file on disk (`DkmModule.Name`/path) when the
     memory is not readable (heapless dumps);
   - if both fail, import the module as plain IL and report degraded mode once
     in the Output window.
3. Cache resources by MVID for the session. Cache `TcImports` per AppDomain,
   keyed by the module set (MVID plus EnC generation).
4. Feed FCS **without touching disk**. The prototype wraps metadata and
   resources in a minimal in-memory PE (`ManagedPEBuilder`) and uses the
   existing `ILModuleReader`-from-bytes path. The target is an FCS reader that
   accepts *(metadata blob, resources)* directly.
5. Choosing references follows Roslyn: all loaded modules in the AppDomain,
   **plus the debuggee's** FSharp.Core. Duplicate simple names are possible
   with AssemblyLoadContexts; for those we prefer the current module's load
   context (`IDkmClrAssemblyLoadContextResolver`). If binding hits an
   ambiguity or a missing reference, we retry with "current module + its
   transitive references".
6. **Implementation assemblies.** The debuggee loads `System.Private.CoreLib`
   and facades, not reference assemblies. F#'s importer already resolves type
   forwarders. We must test that duplicated corlib types bind correctly
   **(verify, M3)**: this needs the compiler. Roslyn needed
   `IgnoreCorLibraryDuplicatedTypes`.

Supported debuggee FSharp.Core: **4.5 and later**, where signature and
optimization data are embedded resources. Older copies kept them in separate
`.sigdata`/`.optdata` files; with those we try the disk files and otherwise
degrade.

### 6.3 Method context: what is in scope

The inputs are the method token and version, the IL offset, the local
signature token, and the PDB. The PDB comes from `GetSymReader()`. F# writes
**only** portable PDBs, standalone or embedded: `--debug:full` and `pdbonly`
also produce portable ones (verified in `CompilerOptions.SetDebugSwitch`). So
we take `ISymUnmanagedReader5.GetPortableDebugMetadata` and read it with
`System.Reflection.Metadata`. From it:

- **Local scopes** containing the IL offset (innermost wins), and local
  variables (name, slot, `DebuggerHidden` attributes).
- **Import scopes.** F# already emits these (`IlxGen.AddDebugImportsToEnv`):
  `open` namespaces become `ImportNamespace`, opened modules and `open type`
  become `ImportType`. They are flattened per scope "because the C# debug
  engine doesn't handle nesting".
- **Containing type and method generics** become F# typars named from metadata.

Opens in scope, in order:

1. FSharp.Core auto-opens (implicit through `TcImports`);
2. the enclosing namespace chain of the defining type;
3. enclosing **modules** (inside `module M`, `M`'s values are unqualified);
4. the PDB import scope at this offset.

Variables, from outermost to innermost (later entries shadow earlier ones):

1. module values (through the opens);
2. class `let` fields (instance context: storage `this.field`);
3. the self identifier and `this`. Both names bind to arg 0;
4. closure-captured or state-machine-hoisted variables (§6.4);
5. parameters;
6. locals in scope at this IL offset.

**Types of variables.** IL types are imported through `TcImports` and then
normalized to F# forms: `FSharpFunc<a,b>` → `a -> b`, `System.Tuple` and
`ValueTuple` → tuple types, `Unit` → `unit`, byref → `byref`/`inref`.
Abbreviations, measures and precise arities are lost here. The fix is the PDB
CDI in §9.

### 6.4 Closures, state machines and compiler-generated names

F# does not emit `StateMachineHoistedLocalScopes` or any equivalent
(verified: there is no such CDI in `ilwritepdb.fs`). Hot-reload work recently
added EnC CDIs: `EncLocalSlotMap`, `EncLambdaAndClosureMap`,
`EncStateMachineStateMap` and an F# `SynthesizedNameSnapshot`. Those cover
EnC, not the EE. So v1 relies on **naming conventions**. The rules live in
one module and are tested against compiler output:

| Context | Recognized by | Variables |
|---|---|---|
| closure `Invoke`/`Specialize`, `this :> FSharpFunc` | `CompilationMapping(Closure)` or the `name@line` class pattern | fields of `this`, with compiler suffixes removed; `this` is hidden |
| `task { }` resumable state machine (`MoveNext` on a generated struct) | `IAsyncStateMachine` + F# naming | user fields of the state-machine data; `ResumptionPoint`, `MethodBuilder` etc. hidden |
| `seq { }` (`GenerateNext`) | `GeneratedSequenceBase<T>` subclass | user fields; `pc`, `current` hidden |
| `async { }` | closures chained through `AsyncActivation` | as for closures |
| mutable local captured by a closure | local of type `FSharpRef<T>` | bound as `x.contents`, both reading and writing **(heuristic: indistinguishable from `let x = ref …` without the CDI in §9)** |

Hidden by default in Locals: names matching
`PrettyNaming.IsCompilerGeneratedName` (contain `@`), plus the F# temporaries
`matchValue`, `copyOfStruct`, `_arg\d+`, `tupledArg`, `patternInput`,
`inputSequence`, `enumerator`, `activePatternResult\d+`, `resultOfApply`,
`unitVar\d*`, `objectArg` and `struct copies`. They remain **bindable by
name** in expressions. A setting shows them under a `[compiler generated]`
node.

### 6.5 Locals window query

`GetClrLocalVariableQuery` uses the same context and emits one method per
visible variable (`<>m0`…`<>mN`), each returning that variable. The entries
come with:

- `DkmClrLocalVariableInfo` (name, full name, `DkmClrLocalVariableInfoFlags`,
  category);
- F# custom type info (§6.8);
- in closures and state machines, the captured variables *flattened* to top
  level (as C# does for display classes);
- aliases (`$exception`, `$ReturnValue`, object ids) prepended, as Roslyn does.

We also implement `…1714`, so that errors reach the user instead of becoming
`BadImageFormatException`.

### 6.6 Assignment and F# immutability

`CompileAssignment` compiles `<lValue.FullName> <- <text>`. The debugger
lets users edit any value, so the EE checker **relaxes** "value is not
mutable" for locals, parameters, record and union fields (stored to the
backing field) and class `let` fields. Literals and `static val` are the
exceptions. Every assignment sets `PotentialSideEffect`.

### 6.7 Syntax details

- **Pseudo-variables.** The F# lexer rejects `$`. Before parsing, outside
  string and char literals, we rewrite `$exception`, `$ReturnValue[N]`,
  `$N` (object id), `$stowedexception` and `$user-defined` into reserved
  identifiers (`` ``$exception`` ``…). These bind to intrinsic calls:
  `GetException`, `GetReturnValue`, `GetObjectByAlias`.
- **Format specifiers vs tuples.** `x, h` is also an F# tuple. Rule: a
  trailing comma-separated run of **known specifier tokens** (`h`, `d`, `nq`,
  `raw`, `results`, `hidden`, `ac`, `nse`, `s8`, `su`, `view(…)`, …) counts as
  specifiers. Users write `(a, h)` to get a tuple. Same parsing as Roslyn's
  `TrimAndGetFormatSpecifiers`.
- **Multi-line text** (Immediate) is parsed as an expression with offside
  rules relaxed from column 0.
- **Conditions** must have type `bool` (`BoolResult` flag). F# has no
  implicit conversions, so `if x then` with `x: int` is an error, as in
  source.

### 6.8 Result properties and custom type info

- **Flags.** `PotentialSideEffect` for assignments, method calls and
  function applications. *Not* for property getters, field reads or pure
  FSharp.Core intrinsics such as `fst`/`snd`, `List.length` (allowlist) or
  inline primitive operators.
- **Field reads.** Record field and `let` field reads are emitted as direct
  field loads, not getter calls, wherever accessibility is bypassed anyway.
  The IL interpreter then handles them without func-eval, so they work in
  dumps.
- **Category, access and storage** from the bound symbol: Data, Property,
  Method, or Other for module values.
- **`DkmClrCustomTypeInfo`** with a new F# payload GUID carries the F#
  type of the result when it differs from what the IL type implies.
  Examples: `float<m>`, `Id` (abbreviation), `int -> int -> int` (arity), a
  union case name. The RP uses it for the Type column and value formatting.
  The encoding is a compact serialized F# type: versioned, and safe to ignore
  for unknown versions.

### 6.9 Performance and threading

- **Targets.** First evaluation in a session under about 2 s; a warm DataTip
  under about 100 ms; the Locals query under about 50 ms warm.
- **Warm-up.** On the first break in an F# frame, building `TcImports` starts
  on a background thread. The frame decoder triggers this; it is cheap.
- **Threading.** The component is `Synchronized="true"`. FCS thread statics
  (diagnostics logger, build phase) are set up per call.
- **Cancellation.** Concord calls are synchronous, so long imports must not
  block the UI. If warm-up is still running we wait with a timeout, then
  return "F# EE is still loading; try again". We do not fall back to C# here.

### 6.10 Fallback to C#

When the F# compiler reports *unsupported construct* (not a user error), we
chain to C# (`expression.CompileExpression(...)` reaches Roslyn) and append
"(evaluated as C#)" to the result. This is controlled by a setting: **on**
during early milestones, **off** at 1.0. User errors (FS0039 and the like)
are never retried as C#.

---

## 7. Frame and instruction decoder

Names come from module metadata (System.Reflection.Metadata over
`GetMetaDataBytesPtr`). Type names are rendered by the result provider's own
rules. FCS is not needed. Implemented in M2 (§15); "Shown" is with the Call
Stack window's default Types+Names.

| Compiled | Shown |
|---|---|
| `Program.main(String[] argv)` | `Program.main (argv: string array)` |
| curried `M.add(Int32 x, Int32 y)` with `CompilationArgumentCounts [1;1]` | `M.add (x: int) (y: int)` |
| tuple parameter in a curried function, `pair_0`, `pair_1` | `generic<int list> (value: int list) (pair: int * string)` |
| tupled member `T.M(Int32 a, String b)` | `T.M (a: int, b: string)` |
| no arguments / unit | `Program.closure ()` |
| `Program.f@66.Invoke(Int32 x)` | `Program.f@66 (x: int)` |
| `OptimizedClosures` `Invoke(a, b)` | `Program.add@53 (a: int) (b: int)` |
| task state machine `MoveNext()` | `Program.work@24 (state machine)` |
| `seq` `GenerateNext(...)` | `Program.numbers@12 (seq)` |
| constructor / static constructor | `Person.new (name: string)` / `Person.(static initializer)` |
| property getter `get_Name()` | `Person.Name (get) ()` |
| operators `op_Addition`, `op_PlusDot` | `Person.(+) (…)`, `Fixtures.(+.) (…)` |
| `[<CompiledName>]` members | the `CompilationSourceName` |
| `PersonModule` (ModuleSuffix) | `Person.create (name: string)` |
| generic frames | the actual type arguments from `GetClrGenericParameters`: `Box<int>.Map<string>` |

Other argument formats:

- Names only: `add x y`.
- Types only: `add (int) (int)`.
- With values: `add (x: int = 1) (y: int = 2)`, or `add 1 2` when values
  only. A tuple parameter's value shows as `(1, "a")`.

The return type uses F# type names. `IDkmLanguageFrameDecoder1714.GetBindableFrameName`
is not implemented; C#'s compiled `Type.Method` form is what the function
resolver binds.

---

## 8. FSharp.Core and FCS: the three copies

"FSharp.Core in debug evaluation" means three different things:

### 8.1 FSharp.Core in the IDE (the EE's own runtime)

- VS 18 ships FSharp.Core **11.0.0.0** (file 11.2.x). The F# project system
  pkgdef registers a **binding redirect `2.0.0.0–11.0.0.0 → 11.0.0.0`**
  with a codeBase in the F# tools folder. So whatever FSharp.Core version the
  EE references gets unified to VS's copy (verified in
  `FSharp.ProjectSystem.FSharp.pkgdef`).
- Consequences:
  1. Compile against an FSharp.Core **no newer than the one in the oldest VS
     we support**, which is VS 18.0 (§12.6). Assembly version alone does not protect us: 11.0.0.0 covers
     every 11.x file, so APIs added in 11.2 are missing in 11.0. Pin it, and
     have a CI check against each supported VS.
  2. **Ship FSharp.Core in the VSIX anyway**, as ThemeFontWeight does with
     10.1.402. Without the F# workload there is no redirect, and ours loads
     through our codeBase.

### 8.2 FCS in the IDE

- VS ships `FSharp.Compiler.Service` **43.13.200.0**, with
  `InternalsVisibleTo` limited to VS's own assemblies (`FSharp.Editor`, …).
  It is not binding-redirected. We cannot reuse it: we need internals and new
  entry points (§6.1), and its version changes with every VS update.
- **Decision.** Build a **private FCS** from a branch of `dotnet/fsharp` that
  carries the EE support, **under its own assembly identity**
  (`FSharp.ExpressionEvaluator.Compiler.dll`). That avoids identity clashes
  with VS's FCS in the same AppDomain, and lets us grant IVT to our EE
  assembly.
  - Its FSharp.Core dependency is still unified to VS's (§8.1). Build it
    against a compatible FSharp.Core, or check in CI that it only binds APIs
    present in that version **(verify, M3)**.
- **Long-term direction.** Upstream the EE support (`src/Compiler/Debugging/…`:
  storage hooks, EE accessor domain, in-memory reader, query emit) into
  `dotnet/fsharp`. This fits the hot-reload work already there, which also
  emits code against a running baseline. Then this repo consumes an official
  FCS with IVT, or a small `[<Experimental>]` public API.
- **Its own compiler instance, always.** The EE never shares the IDE's
  `FSharpChecker` (the instance in VS's F# tools that serves the editor), and
  it does not use `FSharpChecker` at all. Four reasons:
  - **Different inputs.** The editor checks a project against its reference
    assemblies. The EE compiles against what the **debuggee has loaded**:
    implementation assemblies, metadata blocks and F# resources read from
    process memory (§6.2). It also works when no project is open: attach to
    process, dumps, remote debugging.
  - **Different API level.** The EE needs a `TcImports`/`TcGlobals` per
    AppDomain, a synthetic `TcEnv` for the frame and IlxGen storage hooks
    (§6.1). Those are internals below `FSharpChecker`, whose project options,
    incremental builder and caches would only add cost.
  - **Different lifetime.** The state is created on the first break in an F#
    frame. It is cached per AppDomain, invalidated by module
    load/unload/EnC, and dropped when the debug session ends. The editor's
    checker lives for the whole IDE session.
  - **Isolation.** A debugger evaluation must not block on, invalidate or
    pollute the editor's caches, and vice versa.

  So §12.5 is only about **where the code ships**. If it ships as a separate
  VSIX, the EE carries its own privately named FCS build. If it is upstreamed
  and shipped with VS's F# tools (as Roslyn's EE ships with Roslyn), the EE
  could load **the same FCS binary** through `InternalsVisibleTo` while still
  keeping separate in-memory state. Cost of either: one FCS image plus the
  debuggee's imports. The imports are dominated by FSharp.Core's ~400 KB of
  signature and optimization data (§13), and the state is released when
  debugging stops.
- **Alternative kept open: an out-of-process compiler.** A .NET 10 helper
  process would host the core from §6.1, with Concord-side code sending it
  metadata and PDB blobs. That removes every host conflict and runs FCS on
  modern .NET, at the cost of IPC and process management. Not v1, but the
  pure-function core keeps it cheap to add.

### 8.3 FSharp.Core in the debuggee

- **Compile-time reference** for expressions. It must be the debuggee's own
  copy, never ours, because emitted IL binds to its identity. Its signature
  and optimization data come from its image (§6.2).
- **Runtime target for func-eval.** `List.length xs`, `sprintf`, `Map.find`
  and the like execute in the debuggee. `inline` code is inlined into the query
  assembly, so `+` and `=` on primitives never func-eval.
- **What the debuggee already provides for debuggers:**
  - `__DebugDisplay()` on records and unions (`%+0.8A` through func-eval);
  - `DebugDisplay` on list, option and voption;
  - `[DebuggerTypeProxy]` for `list`, `Map`, `Set` and `dict`
    (`ListDebugView`, `MapDebugView`, `SetDebugView`, `DictDebugView`).

  The F# RP **does not depend** on any of these for F# shapes. It reads memory
  instead (§5.2): faster, works in dumps, no side effects. They remain the
  fallback through C# delegation.
- **New debugging helpers in FSharp.Core?** **No, not required.** The EE must
  work with any debuggee FSharp.Core ≥ 4.5. A user who wants `%A` output can
  use a specifier, `x, fsi`, which compiles `sprintf "%A" x` into the query and
  func-evals it. Optional upstream improvements, unrelated to the EE, could
  make `__DebugDisplay` cheaper.

### 8.4 FSharp.Core on the monitor side (msvsmon)

The RP runs where Concord's monitor runs. For remote and x86-local targets,
that is msvsmon, where **there is no redirect and no FSharp.Core**. We deploy
`FSharp.Core.dll` next to the RP. The Concord docs say the vsdconfig is
transferred automatically, but implementation DLLs must be placed next to
msvsmon by hand. The RP is written to keep its FSharp.Core use small:
no `printf` family, no `Async`.

---

## 9. Proposed F# compiler (PDB) improvements

These are independent of the EE and can land upstream in parallel. Each one
replaces a heuristic.

1. **`StateMachineHoistedLocalScopes`** (Roslyn's standard CDI) for `task`,
   `seq` and resumable code. Gives exact hoisted-variable names and scopes.
2. **An F# local-info CDI** (new GUID) per method. It holds the F# type of
   each local and parameter (measures, abbreviations, arity), plus
   flags: *mutable captured in a ref cell*, *compiler temporary*,
   *self identifier*. Lets us remove the naming heuristics in §6.4.
3. **A closure capture map**: captured field → source variable name and
   scope. Partly overlaps with the EnC `LambdaAndClosureMap`; worth
   checking whether that one can be reused.
4. **Self identifier name.** The `this` parameter's source name (`x`, `__`)
   isn't recorded today. Confirmed in M0: `Counter.Increment` has no local
   scope at all in the PDB, and arg 0 has no `Param` row. We would put the
   name in (2).

---

## 10. Repository layout (F#-only VSIX)

We follow the ThemeFontWeight model: SDK-style projects, VSSDK build tools,
`net48`, and `TargetVSVersion` 18. No C# shell project.

```
global.json                          .NET SDK pin
Directory.Build.props / Directory.Packages.props
FSharp.ExpressionEvaluator.slnx
docs/DESIGN.md
src/
  Shared/                            FSharpNames.fs, FSharpTypeNames.fs, FSharpDisplay.fs (linked sources)
  FSharp.ExpressionEvaluator.ResultProvider/     netstandard2.0; ResultProvider.vsdconfigxml
  FSharp.ExpressionEvaluator.ExpressionCompiler/ net48; ExpressionCompiler.vsdconfigxml
                                     MetadataContext, PdbReader, FrameContext, Decoders, Concord glue
  FSharp.ExpressionEvaluator.Vsix/   net48, VSSDK packaging only; DebuggerEngineExtension assets
eng/
  Deploy-Experimental.ps1            builds the VSIX and installs it into the VS 18 Exp instance
  Probe-Debugger.ps1                 drives Exp through DTE: attach to TestDebuggee, dump frames/locals/watches
  DialogWatchdog.ps1                 dismisses the modal "unsafe abort" func-eval dialog that blocks DTE
  build-fcs.ps1                      (M3) builds the private FCS from a dotnet/fsharp branch
tests/
  ResultProvider.Tests/              vendored Roslyn Concord mock (MIT) over live objects
  ExpressionCompiler.Tests/          compile F# fixtures to PE+PDB in memory, run the EE core, check IL and diagnostics, execute static cases
  TestDebuggee/                      an F# console app with every shape and context, for F5 testing
```

Key packaging points:

- **Packaging.** The VSIX project pulls each component's
  `BuiltProjectOutputGroup`, which the VSDConfigTool package extends with the
  `.vsdconfig`. Each `Asset Type="DebuggerEngineExtension"` names the
  `.vsdconfig` file. The VSIX also carries FSharp.Core; the Interop
  assemblies the Concord packages drag in are left out.
- **Install targets.** `InstallationTarget [18.0,)` on amd64 (VS 2026 and
  later, §12.6). arm64 is to be added.
- **Concord packages.** `Microsoft.VisualStudio.Debugger.Engine` and
  `.Metadata`, at least 17.14, so `IDkmClrExpressionCompiler1714` is available.
- **Testing.** Roslyn tests its RP by compiling it against a hand-written
  mock of the Concord API that wraps **live in-process objects**
  (`Core/Test/ResultProvider/Debugger/Engine/*.cs`). We vendor that mock and
  build it as a test-only `Microsoft.VisualStudio.Debugger.Engine`. Then the
  RP runs against real F# values in unit tests, with snapshot assertions on
  name, value, type, full name and children.

Removed in M0: `ExpressionEvaluator.csproj`, `Properties/`, the root
`FSharp.ExpressionEvaluator.vsdconfig`, `Compiler.vsdconfigxml` and
`sformat.fs/.fsi`.

---

## 11. Milestones

| # | Deliverable | Exit criteria |
|---|---|---|
| **M0** ✅ | Spikes: F#-only VSIX skeleton loads in VS 18 Exp; RP registered for F# that chains everything; decoder stub. Confirm every **(verify)** item: chaining, msvsmon loading, `GetSymReader` returning portable metadata, reading F# resources from debuggee memory. | Done 2026-10-22, see §13 |
| **M1** ✅ | **Result Provider v1**: shapes from §5.2, values and types (§5.3), full names (§5.4), hiding rules, delegation; unit tests against live values. Expressions still evaluated by C#. | Done 2026-10-22, see §14 |
| **M2** ✅ | **Frame and instruction decoder** (§7) | Done 2026-10-22, see §15 |
| **M3** 🚧 | **Compiler core in the FCS branch**: in-memory `TcImports` with sigdata; frame `TcEnv`; storage hooks; pre-seeded locals; query emit; accessor domain. Tested in-process, without VS. | In progress, see §16 |
| **M4** | **Concord integration**: metadata context and caching, retries, aliases, format specifiers, Locals query (`1714`), assignment, side-effect flags, C# fallback switch | Watch, Immediate, conditional breakpoints and tracepoints work in `TestDebuggee`; perf targets (§6.9) met |
| **M5** | Closures, state machines and temporaries (§6.4); custom type info (§6.8); RP uses it | `task`, `seq` and closure frames show the right variables |
| **M6** | Upstream work: PDB CDIs (§9) in `dotnet/fsharp`; EE consumes them when present | Heuristics become fallbacks |
| later | Immediate `let` variables, F#-syntax `DebuggerDisplay`, function resolver for F# names, vsdbg/VS Code, out-of-process compiler, full RP port | — |

M1 and M2 do not need FCS and ship value on their own. M3 runs in parallel in
the `dotnet/fsharp` branch.

---

## 12. Open questions

1. **Union full names.** Is the `match` expression in §5.4 acceptable for Add
   Watch? The alternative is an EE-only syntax extension such as `u?Circle.radius`.
2. **Assigning to immutable values** (§6.6). Allow it freely, or require a
   confirmation or a setting?
3. **Value-column suffixes** (`1L`, `1uy`). Show them always, or only in
   previews (the current proposal)?
4. **List and Map indexing in full names** is O(n) / O(log n) through
   func-eval. Acceptable, or should full names avoid indexers?
5. **Where the compiler core lives long term.** An upstream FCS module
   (preferred), or a permanent fork shipped privately? Either way the EE has
   **its own compiler state**; see §8.2, "Its own compiler instance".
6. ~~**VS version floor.**~~ **Decided: VS 2026 (18.0) and later only.** No
   17.x support. The VSIX targets `[18.0,)`, and FSharp.Core and FCS are pinned
   against VS 18.0 (§8.1).

---

## 13. M0 results (2026-10-22)

The spike is VS 2026 Insiders (Concord 18.12) with the Exp instance, driven by
`eng/Probe-Debugger.ps1`. It ran `tests/TestDebuggee` as net10.0 (x64, debugged
in devenv) and as net48 (x86, debugged through msvsmon). It stopped at three
`Debugger.Break()`s: a module function, a closure `Invoke`, and a class member.

The components:

- The result provider renders `'T list` itself and chains everything else.
- The compiler component runs probes, then chains to C#.
- The frame decoder chains and, when `FSHARP_EE_SPIKE_MARKERS=1`, appends
  `[F#]`.

**Confirmed**

| Item | Result |
|---|---|
| Dispatch to F#-filtered components | Frames report language `F#`. Frame decoder, locals query, `CompileExpression` and RP all hit our components first; frame names come back as `TestDebuggee.Program.inspect [F#]`. |
| RP chaining (§5.1) | No recursion. C#-created results expand through chained `GetChildren`, and their members re-enter our `GetResult`. Our list items are created through Concord and come back to us, so `int list list` children show as `[1; 2] : int list`. |
| RP in msvsmon | `[msvsmon:…:x86] [RP] ResultProvider loaded` for the net48 x86 target. The VSIX-installed RP and our FSharp.Core load in local msvsmon without extra deployment. |
| Portable PDB (§6.3) | `GetSymUnmanagedReader()` returns `Microsoft.DiaSymReader.PortablePdb.SymReader`, which implements `ISymUnmanagedReader5`. `GetPortableDebugMetadataByVersion` succeeds and the document language is F#. |
| PDB shape | F# opens **one nested scope per `let`** (`point#0`, `shape#1`, …, each scope starting at its binding). Import scopes are present and flattened: the TypeRef tokens for opened modules, plus `Microsoft`, `Microsoft.FSharp`, `….Collections`, `….Control`, `….Core`, `System`, … . The only CDI is the module-level SourceLink one. Closure `Invoke` and `Counter.Increment` have no scopes or locals at all. |
| Metadata pointers (§6.2) | All modules of the AppDomain in 2 ms: 14 modules / 4.9 MB on .NET 10; 6 modules / 6.4 MB on .NET Framework. The raw blob opens with `MetadataReader`, and the F# resource **rows** are visible. The data is not part of the blob. |
| F# resources from debuggee memory (§6.2) | A whole-image `ReadMemory` throws. Targeted reads work: headers, then CLI header, then each blob, via `PeImage.fs`. TestDebuggee and FSharp.Core resources (`FSharpSignatureCompressedData[B]`, `FSharpOptimizationCompressedData[B]`) read in 1–4 ms on both runtimes, byte-identical to the files. |

**Observed behaviour of today's C# path in F# frames** (evidence for §1.1)

- **Records and unions through func-eval `%A` are unreliable.**
  - In the Locals window, `point` showed `{TestDebuggee.Program.Point}` and
    `shape` showed "Inspecting the state of an object in the debuggee of type
    System.Reflection.MethodBase is not supported in this context".
  - On .NET 10 the first evaluation raised a **modal** dialog: "To prevent an
    unsafe abort when evaluating the function
    'TestDebuggee.Program.Point.ToString' all threads were allowed to run…".
    It blocks the UI and DTE.
  - Later evaluations of the same values succeed, with multi-line output
    (`{ X = 1⏎  Y = 2 }`).
- Options render as `Some(42)`, and `None` as `null`, both typed
  `Microsoft.FSharp.Core.FSharpOption<int>`. Tuples render as `{(1, a)}`.
- The C# compiler's implicit `this` resolves closure captures (`captured`,
  `counter`) and class `let` fields (`count`) by name. But `counter` is an
  `FSharpRef<int>`, and in module functions `this` is CS0026. The Locals
  window in a closure shows only `this` and `x`; captured values are not
  flattened.

**Still open** (moved to M3, since they need the compiler): duplicated
corlib types (§6.2), and FSharp.Core compatibility of the private FCS (§8.2).

**Automation notes** (`eng/Probe-Debugger.ps1`)

- DTE's `*2` interfaces and collections need the interop types
  (`envdte80.dll`) through reflection from Windows PowerShell.
- The engine name passed to `Process2.Attach2` must be a plain `string`. A
  pipeline-wrapped `PSObject` fails with `0x8971001E`.
- Opening the exe as a debug-only project skips the start window.

---

## 14. M1 results (2026-10-22)

The F# result provider renders F# shapes from memory and chains everything else
to C#, on .NET 10 (devenv) and .NET Framework x86 (msvsmon).

**What `TestDebuggee` shows now** (Locals window, previously §13)

| Local | Value | Type |
|---|---|---|
| `point` | `{ X = 1; Y = 2 }` | `TestDebuggee.Program.Point` |
| `shape` / `rect` | `Circle 2.0` / `Rect (1.0, 2.5)` | `TestDebuggee.Program.Shape` |
| `five` (≥4 cases, `_tag`) | `S ("x", 2)` | `TestDebuggee.Program.Five` |
| `structUnion` | `SB "b"` | `TestDebuggee.Program.StructUnion` |
| `opt` / `none` | `Some 42` / `None` | `int option` |
| `someShape` | `Some (Circle 1.5)` | `TestDebuggee.Program.Shape option` |
| `result` | `Error "failed"` | `Result<int, string>` |
| `tuple` / `structTuple` | `(1, "a")` / `struct (1.5, 'c')` | `int * string` / `struct (float * char)` |
| `nested` / `longList` | `[[1; 2]; [3]]` / `[1; …; 10; ...]` | `int list list` / `int list` |
| `array` | `[\|1.0; 2.5\|]` | `float array` |
| `map` / `set` | `map [("one", 1); ("two", 2)]` / `set [1; 2; 3]` | `Map<string, int>` / `Set<int>` |
| `anon` | `{\| Count = 3; Name = "anon" \|}` | `{\| Count: int; Name: string \|}` |
| `cell` | `{ contents = 7 }` | `int ref` |
| `error` | `MyError (404, "not found")` | `exn {TestDebuggee.Program.MyError}` |
| `boxed` | `[1; 2]` | `obj {int list}` |
| `add` | `<fun:add@53>` | `int -> int -> int` |
| closure `this` | `<fun:f@66>`, expands to `captured`, `counter` | `int -> int` |

None of these use func-eval: no `__DebugDisplay` calls, and no "unsafe abort"
dialog.

**How it is built** (`src/FSharp.ExpressionEvaluator.ResultProvider`)

- `Model.fs`: `IType`/`IValue` abstractions.
  - LMR is not System.Reflection, so the shape logic is written once against
    these interfaces.
  - `LmrAdapters.fs` implements them over LMR and `DkmClrValue`, reading
    fields with `GetMemberValue(name, Field)`.
  - The tests implement them over reflection and live objects.
- `FSharpTypes.fs`: shapes from `CompilationMapping`. Each kind of value is
  recognized as follows:
  - **Unions:**
    - cases come from the `NewX` / `get_X` members;
    - fields come from properties tagged `(Field, case, index)`, read through
      their backing fields (`_name`, `item`, `Name@`);
    - the current case is found from `_tag` when present, otherwise from the
      runtime case subclass;
    - `UseNullAsTrueValue` makes `null` the nullary case.
  - **Records, anonymous records, ref cells and F# exceptions:** properties
    tagged `(Field, index)`.
  - **Closures:** by their `FSharpFunc` base.
  - **Map and Set:** an in-order walk of their AVL tree.

  The same file produces F# type names (precedence-aware `*` / `->`, postfix
  generics, abbreviations, module suffix, nested generics).
- `FSharpValues.fs`: previews in `%A` style, with item, depth and length
  limits, plus children and full names.
- `ResultProvider.fs`, the Concord glue:
  - Each child is created through `DkmClrValue.GetResult`, so it comes back to
    us or goes to C# according to its shape.
  - The **Raw View** row is ours and carries no value text. Its children come
    from C# in a copy of the inspection context with
    `DkmEvaluationFlags.ShowValueRaw`. That flag, not the `raw` format
    specifier, is what turns off proxies in Roslyn. Our `GetResult` also
    steps aside under that flag.
- `tests/FSharp.ExpressionEvaluator.ResultProvider.Tests`: 28 tests over every
  union representation, records, tuples (including 8+ with `Rest`), Map/Set,
  closures, literals, type names and full names. They replace the vendored
  Concord mock that §10 planned.

**Decisions and known gaps**

- **Full names use C# syntax for now** (`v.X`, `v[0]`,
  `((Ns.Shape.Circle)v).radius`), because the C# compiler still evaluates
  expressions. They switch to F# syntax with M4.
  - Generic union case classes, Map entries and Set items have no full name
    (no Add Watch).
- **Primitives are still rendered by C#**, at top level and as children
  (`radius = 2 : double`). Inside F# previews they use F# literals (`2.0`,
  `1L`, `'c'`). Owning primitives means taking over hex display, editing and
  string visualizers. That is a deliberate next step, not part of v1.
- The Type column shows `declared {runtime}` when the declared type is not an
  F# shape (`obj {int list}`, `exn {…MyError}`).
- Not yet covered:
  - `Lazy<_>`, `seq` (Results View is still C#'s), `bigint` literals and units
    of measure (§6.8 custom type info);
  - honouring the hex radix inside previews;
  - a configurable preview length.

---

## 15. M2 results (2026-10-22)

The F# frame and instruction decoder (`FrameDecoder.fs`) is a separate class
in the compiler component, registered for F# frames. It never loads FCS.

- **Names.** `MethodNames.fs` builds the name from the method's metadata:
  - curried groups from `CompilationArgumentCounts`;
  - flattened tuple parameters (`p_0`, `p_1`) folded back into `p`;
  - `()` for unit and no-argument methods;
  - operator and `CompilationSourceName` names;
  - module suffix stripped;
  - closures without `.Invoke`;
  - `(state machine)` / `(seq)` markers.
- **Types.** `MetadataTypes.fs` decodes signatures into `IType`. The **same
  `FSharpTypes.typeName`** that formats values then names parameter and return
  types, so the call stack and the Locals window spell types identically.
- **Generic frames.** The decoder asks Concord for the actual type arguments
  (`DkmStackWalkFrame.GetClrGenericParameters`). It parses the serialized
  reflection names it gets back: `generic<int list>`.
- **Argument values.** These come from `GetFrameArguments` in a
  `NoExpansion` context, so they are the result providers' one-line previews
  (ours for F# values).
- **Fallback.** Anything that fails is passed to the next decoder (C#).
- **Tests.** `tests/FSharp.ExpressionEvaluator.ExpressionCompiler.Tests` (13
  tests) reads fixtures compiled into the test assembly back through
  System.Reflection.Metadata.

In VS (net10.0 `TestDebuggee`), the Call Stack window (Types+Names) shows:

```
TestDebuggee.Program.inspect (xs: int list) (name: string)
TestDebuggee.Program.f@66 (x: int)
TestDebuggee.Program.Counter.Increment (by: int)
TestDebuggee.Program.generic<int list> (value: int list) (pair: int * string)
TestDebuggee.Program.main (argv: string array)
```

Return types show as `unit`, `int list * (int * string)` and so on.

**Observations**

- **Debug builds use the dynamic `task` fallback.** `task { }` is not compiled
  to a static state machine, so `TestDebuggee` has no `MoveNext` frame: its
  `work@91-3` frame is a closure. Some FSharp.Core-inlined helpers carry
  `@1` line numbers (`work@1-2.Invoke`), and their frames are still named by
  C#. The static form is covered by the unit test fixture.
- **Locals show the flattened tuple parameter** (`pair_0`, `pair_1`) next to
  the compiler's `pair` local. That is the C# compiler's locals query, and
  M5's job (§6.4).
- **Open questions.**
  - Whether to strip the namespace: VS shows the module name separately.
  - Whether closures should name their enclosing function rather than
    `name@line`.

---

## 16. M3 progress (2026-10-22)

**Where.** Branch `expression-evaluator` of `dotnet/fsharp`, in the local worktree
`E:\repos\fsharp.worktrees\expression-evaluator`, pushed to [majocha/fsharp `expression-evaluator`](https://github.com/majocha/fsharp/tree/expression-evaluator). The code:

- `src/Compiler/Debugging/ExpressionEvaluation.fs(i)`, module
  `FSharp.Compiler.Debugging.ExpressionEvaluation`;
- tests in `tests/FSharp.Compiler.Service.Tests/ExpressionEvaluationTests.fs`
  (8 passing).

**API (internal for now).**

- `EvaluationSession.Create(references)` builds `TcGlobals`/`TcImports` once.
- `CompileExpression(frame, text, ?localsAsParameters)` returns the query
  assembly bytes, `<>x`/`<>m0`, and the F# result type (`int list`), or F#
  diagnostics (`FS0039 …`).
- `FrameContext` carries:
  - the frame method (assembly name and token);
  - the PDB opens;
  - the locals in scope (name and slot).

**How it works.** This is the §6.1 approach, realised with less compiler
surgery than planned:

1. **Frame.** The frame's method is found by token. The F# entities enclosing
   it are looked up in the debuggee CCU.
2. **Opens.** Its namespace and modules are opened (skipping
   `RequireQualifiedAccess`), followed by the PDB's opens.
3. **Variables.** `this`, the parameters and the in-scope locals become local
   `Val`s in the `TcEnv`, typed by importing their IL types. The locals' IL
   types come from the frame's local signature.
4. **Access.** The frame's module and the debuggee assembly are added to
   `eInternalsVisibleCompPaths`. That grants F# `private` access within the
   frame's module and `internal` access to the assembly, with no change to
   `AccessibilityLogic`.
5. **Checking.** The expression is wrapped as source,
   ``module ``<>x`` … let ``<>m0`` () = (expr)``, and checked with the
   standard `CheckOneImplFile`. Post-inference checks accept the free frame
   `Val`s.
6. **Rewrite to parameters.** The checked `<>m0` is rewritten to
   `fun this p1 … l1 … -> body`, with a matching `ValReprInfo`. IlxGen therefore
   stores the frame values as **method arguments**: no new `ValStorage` kind
   or IlxGen hook was needed.
7. **Code generation.** `ApplyAllOptimizations` (local optimizations off;
   `inline` is still inlined), then `GenerateIlxCode`, `mkILSimpleModule` and
   `WriteILBinaryInMemory`, as `fsi` does.
8. **Locals into slots.** The emitted `<>m0` is rewritten at IL level: the
   trailing parameters that stand for frame locals become the **frame's local
   slots**. The frame's full local signature is prepended to the method's
   locals, `ldarg`/`starg`/`ldarga` on them become `ldloc`/`stloc`/`ldloca`,
   and the method's own locals shift up. This replaces the planned "pre-seeded
   locals" IlxGen hook.
9. **Access at run time.** The query assembly defines and applies
   `IgnoresAccessChecksToAttribute` for the debuggee. CoreCLR then permits the
   private access when the query runs as ordinary code (tests, and
   func-eval-loaded queries). The debugger's IL interpreter ignores access
   anyway.

**Tested (in-process, the query invoked by reflection).**

- constants;
- parameters;
- `this` and instance members;
- module values and FSharp.Core functions, including inlined operators and a
  lambda capturing a parameter (`moduleValue |> List.map (fun i -> i * x)`);
- private module values;
- locals, both as parameters and in their frame slots (the query's local
  signature starts with the frame's);
- F# error diagnostics.

**Next in M3**

- **In-memory debuggee metadata.** Build imports from metadata blocks plus the
  F# resources read from process memory (§6.2, §13), instead of file paths.
  The local signature must then be decoded from its `StandAloneSig` token: it
  currently comes from a second reader of the binary that includes bodies.
- **Closures and state machines (§6.4).** Frames inside closures need
  `this.field` variables (§6.4). The rewrite-to-parameters scheme extends
  naturally: a capture becomes a field load from `this`.
- **Generics.** Generic frames (map the frame's type parameters to F# typars)
  and generic results (an expression whose type is still generic, e.g. `[]`,
  should default to `obj` rather than make `<>m0` generic).
- **Aliases and the rest of §6.6–6.8.** `$exception` and friends, format
  specifiers, assignment, side-effect flags and custom type info.
- **Diagnostics.** Line and column numbers relative to the user's text rather
  than the synthesized wrapper.

