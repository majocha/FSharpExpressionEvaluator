# F# Expression Evaluator

An F# expression evaluator for the Visual Studio debugger, written entirely in F#.

When the debugger stops in F# code, this extension shows values, types and call stack names
as F# writes them. It also compiles F# expressions in the Watch window, the Immediate window
and DataTips.

Without this extension, Visual Studio uses the C# evaluator in F# frames. You then get C#
syntax and C# type names. To show F# values, the C# evaluator calls `sprintf "%A"` in the
debugged process. That is slow, it needs function evaluation, and it does not work in dumps.

> **Preview.** The extension is in development. Some features are not complete.

## Values

The extension reads values from memory. It does not run code in the debugged process.

| Value | Type |
|---|---|
| `{ X = 1; Y = 2 }` | `Program.Point` |
| `Some (Circle 1.5)` | `Program.Shape option` |
| `Error "failed"` | `Result<int, string>` |
| `[[1; 2]; [3]]` | `int list list` |
| `map [("one", 1); ("two", 2)]` | `Map<string, int>` |
| `struct (1.5, 'c')` | `struct (float * char)` |
| `{\| Count = 3; Name = "anon" \|}` | `{\| Count: int; Name: string \|}` |
| `<fun:add@53>` | `int -> int -> int` |
| `12345678901234567890123I` | `bigint` |

The extension shows records, anonymous records, unions, lists, arrays, tuples, `Map`, `Set`,
F# exceptions, closures, `Lazy` values and `bigint`. It does not force a `Lazy` value.

When you expand a value, you see its F# fields, list items, map entries or captured values.
A **Raw View** row shows the .NET members. **Add Watch** on a row writes an F# expression
for that row, for example `point.X` or `xs[1]`.

## Call stack names

```
Program.inspect (xs: int list) (name: string) (trip: float<m>)
Program.generic<int list> (value: int list) (pair: int * string)
Program.work@24 (state machine)
Program.fetch@69-2 (async)
Program.Counter.Increment (by: int)
```

Names show curried argument groups, tuple parameters, operators and units of measure.
The extension marks the code of `task { }`, `async { }` and `seq { }`.

## F# expressions

The F# compiler compiles the expressions in the Watch window, the Immediate window and
DataTips:

```
xs |> List.sum                      6
List.map (fun i -> i * 2) xs        [2; 4; 6]
this.Increment 0                    12
```

In the Immediate window, `let` declares a variable. Later expressions can use it:

```
let digits = xs |> List.map string     ["1"; "2"; "3"]
let mutable total = List.sum xs        6
total <- total + 10
```

Expressions must be F#. The extension does not accept C# format specifiers such as `x, h`.
Use the **Hexadecimal Display** command for hexadecimal values.

## Locals

The Locals window shows the variables as they are in the source. It shows the captured
values of a closure and the variables of a `task { }` or `seq { }` state machine. The rows
for compiler variables are in one collapsed row, `[compiler generated]`.

## Requirements

- Visual Studio 2026 (18.0 or later), x64.

The extension operates only in frames that the PDB identifies as F#. C# and Visual Basic
frames do not change.

## Turn the extension off

1. Select **Tools > Options > Debugging > F# Expression Evaluator**.
2. Clear **Use the F# expression evaluator in F# frames**.

The change applies from the next evaluation. You do not have to restart Visual Studio.

## Feedback

Report problems and send ideas on
[GitHub](https://github.com/majocha/FSharpExpressionEvaluator/issues).

## License

The extension is open source under the
[MIT License](https://github.com/majocha/FSharpExpressionEvaluator/blob/master/LICENSE).
