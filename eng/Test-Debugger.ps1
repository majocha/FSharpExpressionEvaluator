<#
.SYNOPSIS
  Checks the deployed expression evaluator in the Exp instance of VS 2026 or later: watch values,
  assignment, a breakpoint condition and a tracepoint in tests\TestDebuggee, each a run of
  Probe-Debugger.ps1 with expected values. Exits with 1 when a check fails.
  Deploy first (eng\Deploy-Experimental.ps1). Run with Windows PowerShell 5.1 (powershell.exe).
#>
[CmdletBinding()]
param(
    [ValidateSet('net11.0', 'net48')]
    [string] $Target = 'net11.0',
    [switch] $KeepVsOpen
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$probe = Join-Path $PSScriptRoot 'Probe-Debugger.ps1'

$runs = @(
    @{
        Name = 'watch and assignment'
        Arguments = @{
            Breaks = 6
            Expressions = @()
            Expect = @(
                '1: point => { X = 1; Y = 2 }'
                '1: opt => Some 42'
                '1: xs |> List.sum => 6'
                '1: List.map (fun i -> i * 2) xs => [2; 4; 6]'
                '1: fst tuple => 1'
                '1: anon.Name => "anon"'
                # C# renders it (its DebuggerDisplay), on the result provider's own work list.
                '1: dict => Count = 2'
                '1: undefinedName => !error'
                '1: xs, h => !error'
                # Lazy values read without forcing them, and a bigint literal.
                '1: created => [1; 2]'
                '1: pending => <unevaluated>'
                '1: pending.IsValueCreated => false'
                '1: big => 12345678901234567890123I'
                # Names in double backticks.
                '1: ``the sum`` + 1 => 8'
                '1: odd.``my field`` => 1'
                '1: (match oddUnion with TestDebuggee.Program.OddUnion.``My Case`` x -> x | _ -> failwith "not My Case") => 7'
                # A local that shadows the parameter, and the parameter by its Locals row's full name.
                '1: name => "TEST"'
                '1: ``name (shadowed)`` => "test"'
                # The full names of union case fields' rows (Add Watch): a match on the case.
                '1: (match rect with TestDebuggee.Program.Shape.Rect (_, x) -> x | _ -> failwith "not Rect") => 2.5'
                '1: (match result with Result.Error x -> x | _ -> failwith "not Error") => "failed"'
                # Primitives as F# literals, at top level: floats with a decimal point, F# escapes, `M`.
                '1: ratio => 2.0'
                '1: text => "a\tb"'
                '1: money => 1.5M'
                '1: letter => ''q'''
                '1: count64 => 5'
                '1: nan => nan'
                '1: 1.0 / 0.0 => infinity'
                '1: "a" + string ''\n'' => "a\n"'
                # The Immediate window's variables (declared at break 1).
                '1: digits => ["1"; "2"; "3"]'
                '1: runningTotal => 16'
                '1: String.concat "," digits => "1,2,3"'
                '1: snd pair2 => "one"'
                '2: runningTotal + x => 17'
                '3: digits.Length + by => 5'
                '2: captured + x => 6'
                # A captured mutable local is a ref cell; it reads as its contents.
                '2: counter => 1'
                '2: counter + x => 2'
                '2: counter.Value => 1'
                '3: count => 12'
                '3: this.Increment 0 => 12'
                '3: by * 2 => 4'
                '4: value, pair => ([1; 2], (3, "c"))'
                '5: delay * 2 => 2'
                # A task's hoisted variables: one that shadows the parameter, and one out of scope.
                '5: doubled + label.Length => 4'
                '5: ``label (shadowed)`` => "w"'
                '5: squared => !error'
                '6: List.map (fun i -> i * 2) xs => [2; 4; 6]'
                '6: _arg1 => Some [1; 2; 3]'
            )
            # Variables declared in the Immediate window, read in later expressions and breaks, assigned
            # with `<-` when mutable, and edited through their Locals rows. A value whose type the
            # statement itself defines is declared as obj, which the statement says.
            Immediate = @(
                '1: let digits = xs |> List.map string => ["1"; "2"; "3"]'
                '1: let mutable runningTotal = List.sum xs => 6'
                '1: runningTotal <- runningTotal + 10'
                '1: digits <- [] => !error'
                '1: let pair2: int * string = (1, "one") => (1, "one")'
                '1: let made = {| A = 1 |} => "made is declared as obj: its type, {| A: int |}, is defined by this expression, and later expressions can''t name it"'
            )
            # Integers inside F# previews follow the debugger's Hexadecimal Display. `x, h` is an F#
            # tuple: the C# debugger's format specifiers are not F#.
            ExpectHex = @('1: [ 10; 255 ] => [0x0000000a; 0x000000ff]', '1: opt => Some 0x0000002a', '1: big => 12345678901234567890123I', '1: count64 => 0x0000000000000005', '1: ratio => 2.0', '1: letter => ''q''')
            # Children from GetChildren: an F# record's fields (each rendered by C#) and its Raw View,
            # C#'s own children of a Dictionary, and a class's fields.
            Expand = @('1: point', '1: dict', '3: this')
            # F# types the CLR type has lost: a unit of measure, a type abbreviation.
            ExpectType = @(
                '1: distance => float<m>'
                '1: [ distance ] => float<m> list'
                '1: ids => Ids'
                '1: xs => int list'
                # A parameter, typed from its function's F# signature.
                '1: trip => float<m>'
                '1: trip / 2.0 => float<m>'
                '1: created => Lazy<int list>'
                '1: big => bigint'
                # Type names in double backticks: from the result provider, and an abbreviation's
                # from the expression compiler.
                '1: oddBox => TestDebuggee.Program.``Odd Box``'
                '1: [ oddBox ] => TestDebuggee.Program.``Odd Box`` list'
                '1: myIds => ``My Ids``'
                '1: digits => string list'
                '1: made => obj {{| A: int |}}'
                # Primitives and their arrays have F# type names.
                '1: ratio => float'
                '1: count64 => int64'
                '1: box ratio => obj {float}'
                '1: array => float array'
            )
            ExpectLocalType = @('1: oddBox => TestDebuggee.Program.``Odd Box``', '1: trip => float<m>', '1: xs => int list', '1: name (shadowed) => string', '1: name => string', '5: label (shadowed) => string', '5: label => string', '5: doubled => int')
            # A call stack name with a parameter's F# type, once the session is built (break 2). A
            # closure and a task keep their compiled names.
            ExpectLog = @('TestDebuggee.Program.closure (start: float<m>)', 'TestDebuggee.Program.f@* (x: int)')
            ExpectFrame = @('2: TestDebuggee.Program.f@*', '5: TestDebuggee.Program.work@* (state machine)')
            # Compiler temporaries under one Locals row: here `function`'s argument.
            ExpectLocalChild = @('6: [compiler generated] / _arg1 => Some [1; 2; 3]', '1: rect / Item2 => 2.5', '1: created / Value => [1; 2]', '1: shape / radius => 2.0', '1: array / [0] => 1.0', '1: map / ["one"] => 1')
            # The full names of rows under Locals rows, which Add Watch puts in the Watch window: each
            # evaluates to the row's value. Rows that F# can't assign to are read-only.
            ExpectFullName = @(
                '1: point / X => point.X [editable]'
                '1: shape / radius => (match shape with TestDebuggee.Program.Shape.Circle x -> x | _ -> failwith "not Circle") [read-only]'
                '1: tree / Item1 => (match tree with TestDebuggee.Program.Tree.Node (x, _) -> x | _ -> failwith "not Node") [read-only]'
                '1: someShape / Value / radius => (match someShape.Value with TestDebuggee.Program.Shape.Circle x -> x | _ -> failwith "not Circle")'
                '1: tuple / Item2 => tuple.Item2 [read-only]'
                '1: big9 / Item8 => big9.Rest.Item1'
                '1: structTuple / Item2 => (let struct (_, x) = structTuple in x) [read-only]'
                '1: anon / Count => anon.Count [read-only]'
                '1: error / code => (match (error :> exn) with TestDebuggee.Program.MyError (x, _) -> x | _ -> failwith "not MyError") [read-only]'
                '1: typedError / Data1 => (match (typedError :> exn) with TestDebuggee.Program.MyError (_, x) -> x | _ -> failwith "not MyError") [read-only]'
                '1: xs / [1] => xs[1] [read-only]'
                '1: nested / [0] / [1] => nested[0][1]'
                '1: array / [1] => array[1] [editable]'
                '1: grid / [1, 2] => *'
                '1: map / ["two"] => map["two"] [read-only]'
                '1: points / [{ X = 1; Y = 2 }] => (Seq.item 0 points).Value'
                '1: set / [1] => Seq.item 1 set [read-only]'
                '1: created / Value => created.Value'
            )
            # A parameter of a unit-of-measure type (edited without the unit), a record's immutable
            # field (as an edit of its row under `point`), a closure's captured value, a parameter and
            # a class let field.
            # Edits of Locals rows compile an assignment to the row's full name, which needs double
            # backticks here: a local, and record fields under its row.
            AssignLocal = @('4: runningTotal = 30', '1: the sum = 10', '1: odd / my field = 5', '1: odd / type = "u"', '1: ratio = 2.5', '1: text = "a\tc"', '1: money = 2.5M', '1: letter = ''r''', '1: array / [1] = 3.5')
            Assign = @('trip=4.5', 'point.X=5', 'captured=42', 'by=5', 'count=100')
        }
    }
    @{
        Name = 'call stack names'
        Arguments = @{
            Breaks = 6
            Frames = $true
            Expressions = @()
            # Compiler-generated code keeps its compiled name, after the innermost `let` it is in: a
            # lambda (in `firsts`), an inner function and a lambda in it; the code of an `async { }`
            # and of a `seq { }` are marked.
            ExpectFrame = @(
                '1: TestDebuggee.Program.firsts@*'
                '2: TestDebuggee.Program.scale@*'
                '3: TestDebuggee.Program.scale@*'
                '4: TestDebuggee.Program.asyncFrame@* (async)'
                '5: TestDebuggee.Program.seqFrame@* (seq)'
                # A lambda that returns an async is the user's own function: no marker.
                '6: TestDebuggee.Program.asyncLambda@[0-9][0-9][0-9]'
            )
            ExpectLog = @('TestDebuggee.Program.firsts@* (x: int)', 'TestDebuggee.Program.scale@* (k: int)', 'TestDebuggee.Program.asyncLambda@* (url: string)')
            Expect = @('1: x => 1', '2: k * 10 => 20', '3: x * k => 2', '4: n + 1 => 2', '6: url.Length => 3')
            ExpectEnd = $true
        }
    }
    @{
        Name = 'breakpoint condition'
        Arguments = @{
            Breaks = 7
            Expressions = @()
            # F# equality, not a C# assignment: stops once, at the 7th iteration.
            Condition = 'i = 7'
            Expect = @('7: i => 7', '7: total => 21')
            ExpectEnd = $true
            # A mutable local, stored to its frame slot.
            Assign = @('total=100')
        }
    }
    @{
        Name = 'tracepoint'
        Arguments = @{
            Breaks = 6
            Expressions = @()
            Tracepoint = 'loop i={i} total={total} sum={List.sum [ 1 .. i ]}'
            ExpectTrace = @('loop i=1 total=0 sum=1', 'loop i=7 total=21 sum=28', 'loop i=10 total=45 sum=55')
            ExpectEnd = $true
        }
    }
)

# Every run but the last keeps VS open for the next; the last quits it unless it was open before.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instance = @(& $vswhere -latest -prerelease -products '*' -version '18.0' -format json | ConvertFrom-Json)[0]
$devenv = Join-Path $instance.installationPath 'Common7\IDE\devenv.exe'
$vsWasOpen = [bool](Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" |
    Where-Object { $_.ExecutablePath -eq $devenv -and $_.CommandLine -match '(?i)/rootsuffix\s+"?Exp' })

$failed = @()
for ($r = 0; $r -lt $runs.Count; $r++) {
    $run = $runs[$r]
    $arguments = $run.Arguments.Clone()
    $arguments.Target = $Target
    if ($r -lt $runs.Count - 1 -or $KeepVsOpen) { $arguments.KeepVsOpen = $true }
    elseif (-not $vsWasOpen) { $arguments.QuitVs = $true }
    Write-Host ''
    Write-Host "##### $($run.Name) ($Target)"
    $global:LASTEXITCODE = 0
    try {
        & $probe @arguments
        if ($LASTEXITCODE -ne 0) { $failed += $run.Name }
    } catch {
        Write-Host "Run failed: $_"
        $failed += $run.Name
    }
}

Write-Host ''
if ($failed) {
    Write-Host "Failed: $($failed -join ', ')"
    exit 1
}
Write-Host "All $($runs.Count) runs passed ($Target)."
