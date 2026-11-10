<#
.SYNOPSIS
  Drives the Exp instance of VS 2026 or later through DTE: attaches to tests\TestDebuggee, and at every
  Debugger.Break() reports the frame name, locals and a set of watch expressions.
  With -Expect, -ExpectTrace or -ExpectEnd it also checks what it sees, and exits with 1 when a check
  fails; so does an assignment that fails or doesn't read back. eng\Test-Debugger.ps1 runs the checks.
  Run with Windows PowerShell 5.1 (powershell.exe): it needs STA COM and Marshal-free ROT access.
#>
[CmdletBinding()]
param(
    [ValidateSet('net10.0', 'net48')]
    [string] $Target = 'net10.0',
    [string[]] $Expressions = @('point', 'shape', 'someShape', 'map', 'captured', 'count'),
    [int] $Breaks = 5,
    # At these breaks, step over $Steps times and list the locals after each step (return values show there).
    [int[]] $StepAt = @(),
    [int] $Steps = 4,
    # A breakpoint with this condition on the TestDebuggee line marked '// probe: breakpoint' (break 7)...
    [string] $Condition,
    # ...or a tracepoint there with this message; it doesn't break, its output is read from the Debug pane.
    [string] $Tracepoint,
    # At each break, assignments 'name=value' to the names that evaluate there, then read back.
    [string[]] $Assign = @(),
    # Edits of Locals rows ('break: row = value', or 'break: row / child = value' for a row under it),
    # read back from a fresh Locals list. An edit compiles an assignment to the row's full name.
    [string[]] $AssignLocal = @(),
    # Checks 'break: expression => value': the expression's value at that break, before any assignment,
    # as the Watch window shows it. The value '!error' expects the expression not to evaluate.
    [string[]] $Expect = @(),
    # Statements of the Immediate window ('break: statement', or 'break: statement => value' to check
    # what it shows; '!error' for one that must fail), run before the other checks of that break, in
    # order. A declaration (`let x = ...`) makes a variable later expressions and breaks can read.
    [string[]] $Immediate = @(),
    # Checks 'break: expression => value' as -Expect does, with the debugger's Hexadecimal Display on.
    [string[]] $ExpectHex = @(),
    # Checks 'break: expression => type': the expression's type at that break, as the Watch window shows it.
    [string[]] $ExpectType = @(),
    # Checks 'break: name => type': the type of that Locals row at that break.
    [string[]] $ExpectLocalType = @(),
    # Checks 'break: row / child => value': the value of a child of that Locals row at that break.
    [string[]] $ExpectLocalChild = @(),
    # Checks 'break: row / child [/ child...] => full name': the full name of a row under a Locals row, the
    # expression Add Watch puts in the Watch window, read from the EE log (FSHARP_EE_LOG_FULL_NAMES). It
    # must evaluate to the row's value. The full name '*' takes any, '!none' expects none (no Add Watch).
    # A trailing ' [read-only]' or ' [editable]' checks whether the row can be edited.
    [string[]] $ExpectFullName = @(),
    # At these breaks ('break: expression'), expand the expression and list its children; fails without any.
    [string[]] $Expand = @(),
    # Checks 'break: name': the name of the frame at that break, as DTE has it (names only, no arguments).
    # `*` matches anything, such as the line in a closure's compiled name (`f@*`).
    [string[]] $ExpectFrame = @(),
    # Text the EE log must have, in a line written during this run (e.g. a call stack name); `*` matches
    # anything within the line.
    [string[]] $ExpectLog = @(),
    # Lines the tracepoint must have written to the Debug pane.
    [string[]] $ExpectTrace = @(),
    # After the last break, the debuggee must run to its end without breaking again.
    [switch] $ExpectEnd,
    # Run TestDebuggee with --startup: two breaks in top-level code (Startup.fs, StartupNamespace.fs)
    # come before break 1, so pass -Breaks 7 to reach break 5.
    [switch] $Startup,
    # Run TestDebuggee with --frames: only the breaks F1-F5 in compiler-generated code (lambdas, an
    # inner function, `async`, `seq`), for their call stack names.
    [switch] $Frames,
    # Defaults to artifacts\logs\ee.log.
    [string] $Log,
    [switch] $KeepVsOpen,
    # Quit the Exp instance at the end even when it was already running.
    [switch] $QuitVs,
    # Inspect a debug session that is already stopped at a break (attached by hand or by an earlier run).
    [switch] $Reuse
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# powershell -File passes a list as one string: also accept expressions separated by ';;'.
$Expressions = @($Expressions | ForEach-Object { $_ -split ';;' } | Where-Object { $_ })
$Assign = @($Assign | ForEach-Object { $_ -split ';;' } | Where-Object { $_ })
$expectations = @($Expect | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*=>\s*(.*?)\s*$') { throw "-Expect '$_' is not 'break: expression => value'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Text = $Matches[2]; Value = $Matches[3] }
})
$localAssignments = @($AssignLocal | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*([^/=]+?)\s*(?:/\s*([^=]+?)\s*)?=\s*(.*?)\s*$') { throw "-AssignLocal '$_' is not 'break: row [/ child] = value'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Row = $Matches[2]; Child = $Matches[3]; Value = $Matches[4] }
})
$hexExpectations = @($ExpectHex | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*=>\s*(.*?)\s*$') { throw "-ExpectHex '$_' is not 'break: expression => value'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Text = $Matches[2]; Value = $Matches[3] }
})
$expansions = @($Expand | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*$') { throw "-Expand '$_' is not 'break: expression'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Text = $Matches[2] }
})
$typeExpectations = @($ExpectType | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*=>\s*(.*?)\s*$') { throw "-ExpectType '$_' is not 'break: expression => type'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Text = $Matches[2]; Type = $Matches[3] }
})
$localTypeExpectations = @($ExpectLocalType | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*=>\s*(.*?)\s*$') { throw "-ExpectLocalType '$_' is not 'break: name => type'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Name = $Matches[2]; Type = $Matches[3] }
})
$localChildExpectations = @($ExpectLocalChild | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*/\s*(.+?)\s*=>\s*(.*?)\s*$') { throw "-ExpectLocalChild '$_' is not 'break: row / child => value'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Row = $Matches[2]; Child = $Matches[3]; Value = $Matches[4] }
})
$fullNameExpectations = @($ExpectFullName | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*=>\s*(.*?)\s*$') { throw "-ExpectFullName '$_' is not 'break: row / child => full name'." }
    $path = @($Matches[2] -split '\s+/\s+')
    if ($path.Count -lt 2) { throw "-ExpectFullName '$_' names no row under a Locals row." }
    $break = [int]$Matches[1]
    $fullName = $Matches[3]
    $editable = $null
    if ($fullName -match '^(.*?)\s*\[(read-only|editable)\]$') { $fullName = $Matches[1]; $editable = $Matches[2] -eq 'editable' }
    [pscustomobject]@{ Break = $break; Path = $path; FullName = $fullName; Editable = $editable }
})
$statements = @($Immediate | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*(?:=>\s*(.*?))?\s*$') { throw "-Immediate '$_' is not 'break: statement [=> value]'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Text = $Matches[2]; Value = $Matches[3] }
})
$frameExpectations = @($ExpectFrame | ForEach-Object { $_ -split ';;' } | Where-Object { $_ } | ForEach-Object {
    if ($_ -notmatch '^\s*(\d+)\s*:\s*(.+?)\s*$') { throw "-ExpectFrame '$_' is not 'break: name'." }
    [pscustomobject]@{ Break = [int]$Matches[1]; Name = $Matches[2] }
})
$ExpectTrace = @($ExpectTrace | ForEach-Object { $_ -split ';;' } | Where-Object { $_ })
$failures = New-Object System.Collections.Generic.List[string]
$checks = 0
# Not a parameter default: Windows PowerShell 5.1 leaves $PSScriptRoot empty there under CmdletBinding.
if (-not $Log) { $Log = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\logs\ee.log' }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class Rot {
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    public static object Get(string displayName) {
        IRunningObjectTable rot; GetRunningObjectTable(0, out rot);
        IEnumMoniker monikers; rot.EnumRunning(out monikers);
        IBindCtx ctx; CreateBindCtx(0, out ctx);
        var moniker = new IMoniker[1];
        while (monikers.Next(1, moniker, IntPtr.Zero) == 0) {
            string name; moniker[0].GetDisplayName(ctx, null, out name);
            if (name == displayName) { object o; rot.GetObject(moniker[0], out o); return o; }
        }
        return null;
    }
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}

/// Retries calls VS rejects while busy (RPC_E_CALL_REJECTED).
public class RetryMessageFilter : IOleMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IOleMessageFilter filter, out IOleMessageFilter old);
    public static void Register() { IOleMessageFilter old; CoRegisterMessageFilter(new RetryMessageFilter(), out old); }
    public int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType) { return rejectType == 2 && tickCount < 60000 ? 200 : -1; }
    public int MessagePending(IntPtr callee, int tickCount, int pendingType) { return 2; }
}
'@

[RetryMessageFilter]::Register()

$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "tests\TestDebuggee\bin\Debug\$Target\TestDebuggee.exe"
if (-not (Test-Path $exe)) { throw "Build tests\TestDebuggee first: $exe" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instance = @(& $vswhere -latest -prerelease -products '*' -version '18.0' -format json | ConvertFrom-Json)[0]
$devenv = Join-Path $instance.installationPath 'Common7\IDE\devenv.exe'

Add-Type -Path (Join-Path $instance.installationPath 'Common7\IDE\PublicAssemblies\envdte.dll')
Add-Type -Path (Join-Path $instance.installationPath 'Common7\IDE\PublicAssemblies\envdte80.dll')
Add-Type -Path (Join-Path $instance.installationPath 'Common7\IDE\PublicAssemblies\envdte90.dll')

function Invoke-Com($target, [type] $interface, [string] $name, [object[]] $arguments = @()) {
    # PowerShell binds COM objects to their default interface (Debugger, Process), so call the
    # *2 interfaces through their interop types; reflection makes the RCW QueryInterface for them.
    $property = $interface.GetProperty($name)
    if ($property) { return $property.GetValue($target, $arguments) }
    $interface.GetMethod($name).Invoke($target, $arguments)
}

function Get-Items($collection, [type] $collectionType) {
    # PowerShell cannot enumerate these DTE collections directly; index them through the interop type.
    $count = $collectionType.GetProperty('Count').GetValue($collection, $null)
    $item = $collectionType.GetMethod('Item')
    for ($i = 1; $i -le $count; $i++) { $item.Invoke($collection, [object[]]@([object]$i)) }
}

function Wait-Until([scriptblock] $condition, [int] $seconds, [string] $what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (& $condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for $what." }
        Start-Sleep -Milliseconds 250
    }
}

# Start (or reuse) the Exp instance, with the EE's spike switches in its environment.
$env:FSHARP_EE_SPIKE_MARKERS = '1'
$env:FSHARP_EE_LOG = $Log
# Full names of rows, for -ExpectFullName. A VS that was already running keeps its environment.
$env:FSHARP_EE_LOG_FULL_NAMES = '1'
# -ExpectLog reads only what this run writes.
$logStart = if (Test-Path $Log) { (Get-Item $Log).Length } else { 0 }
$vs = Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" |
    Where-Object { $_.ExecutablePath -eq $devenv -and $_.CommandLine -match '(?i)/rootsuffix\s+"?Exp' } | Select-Object -First 1
$startedVs = $false
if (-not $vs) {
    # Opening the exe as a debug-only project skips the start window, which blocks attaching.
    $p = Start-Process $devenv -ArgumentList @('/RootSuffix', 'Exp', "`"$exe`"") -PassThru
    $vsPid = $p.Id
    $startedVs = $true
} else {
    $vsPid = $vs.ProcessId
}
$script:dte = $null
$dteVersion = "$(([version]$instance.installationVersion).Major).0"
Wait-Until { $script:dte = [Rot]::Get("!VisualStudio.DTE.$($dteVersion):$vsPid"); $null -ne $script:dte } 180 'DTE registration'
$dte = $script:dte
Write-Host "VS $($dte.Version) (pid $vsPid), EE log: $Log"

# Func-eval of F# '%A' display can raise a modal "unsafe abort" dialog that blocks DTE; dismiss and log it.
$watchdog = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSScriptRoot\DialogWatchdog.ps1`"", '-VsPid', $vsPid, '-Log', "`"$Log`"")

# Everything after the watchdog starts runs under try/finally so it is always cleaned up.
try {

if ($Reuse) {
    $debuggee = Get-Process TestDebuggee | Select-Object -First 1
} else {
    if ($dte.Debugger.CurrentMode -ne 1) { $dte.Debugger.TerminateAll() }
    Wait-Until { $dte.Debugger.CurrentMode -eq 1 } 30 'debugger to stop'

    # Breakpoints for break 7, set before the debuggee runs: a condition or a tracepoint.
    $source = Join-Path $root 'tests\TestDebuggee\Program.fs'
    $line = (Select-String -Path $source -Pattern '// probe: breakpoint').LineNumber
    if ($Condition -or $Tracepoint) {
        foreach ($b in @(Get-Items $dte.Debugger.Breakpoints ([EnvDTE.Breakpoints]))) {
            if ([EnvDTE.Breakpoint].GetProperty('FileLine').GetValue($b, $null) -eq $line) { [EnvDTE.Breakpoint].GetMethod('Delete').Invoke($b, @()) | Out-Null }
        }
        # Add(Function, File, Line, Column, Condition, ConditionType (1 = when true), Language, Data, DataCount, Address, HitCount, HitCountType (1 = none))
        $added = $dte.Debugger.Breakpoints.Add('', $source, $line, 1, $(if ($Condition) { $Condition } else { '' }), 1, '', '', 0, '', 0, 1)
        if ($Tracepoint) {
            foreach ($b in @(Get-Items $added ([EnvDTE.Breakpoints]))) {
                [EnvDTE80.Breakpoint2].GetProperty('Message').SetValue($b, $Tracepoint, $null)
                [EnvDTE80.Breakpoint2].GetProperty('BreakWhenHit').SetValue($b, $false, $null)
            }
        }
        Write-Host "Breakpoint at Program.fs:$line, condition '$Condition', tracepoint '$Tracepoint'"
    }

    # Start the debuggee; it prints its PID once the runtime is up, then spins until a debugger attaches.
    $stdout = [IO.Path]::GetTempFileName()
    $debuggeeArguments = '--wait' + $(if ($Startup) { ' --startup' } else { '' }) + $(if ($Frames) { ' --frames' } else { '' })
    $debuggee = Start-Process $exe -ArgumentList $debuggeeArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout
    Wait-Until { (Get-Content $stdout -Raw -ErrorAction SilentlyContinue) -match 'PID' } 30 'debuggee start'

    $transports = Invoke-Com $dte.Debugger ([EnvDTE80.Debugger2]) 'Transports'
    $transport = Invoke-Com $transports ([EnvDTE80.Transports]) 'Item' @('Default')
    $engines = @(Get-Items (Invoke-Com $transport ([EnvDTE80.Transport]) 'Engines') ([EnvDTE80.Engines]) |
        ForEach-Object { [EnvDTE80.Engine].GetProperty('Name').GetValue($_, $null) })
    $pattern = if ($Target -eq 'net48') { '\.NET Framework 4|Managed \(v4' } else { '\.NET Core|CoreCLR' }
    # Must be a plain string: a pipeline-wrapped PSObject reaches VS as an object and Attach2 fails (0x8971001E).
    [string] $engine = $engines | Where-Object { $_ -match $pattern } | Select-Object -First 1
    Write-Host "Attaching with engine '$engine' to pid $($debuggee.Id) ($Target)"
    $process = Get-Items $dte.Debugger.LocalProcesses ([EnvDTE.Processes]) |
        Where-Object { [EnvDTE.Process].GetProperty('ProcessID').GetValue($_, $null) -eq $debuggee.Id } | Select-Object -First 1
    # Attaching can also fail while VS is still initializing (start window, package loads); retry briefly.
    $deadline = (Get-Date).AddSeconds(30)
    while ($true) {
        try { Invoke-Com $process ([EnvDTE80.Process2]) 'Attach2' ([object[]]@([object]$engine)) | Out-Null; break }
        catch {
            if ((Get-Date) -gt $deadline) { throw }
            Write-Host "  attach failed ($($_.Exception.InnerException.Message)); retrying"
            Start-Sleep -Seconds 3
        }
    }
}

function Show-Value($e, [string] $indent) {
    $p = { param($n) [EnvDTE.Expression].GetProperty($n).GetValue($e, $null) }
    $valid = if (& $p 'IsValidValue') { '' } else { ' (invalid)' }
    Write-Host "$indent$(& $p 'Name') = $(& $p 'Value') : $(& $p 'Type')$valid"
}

# What this run has written to the EE log so far.
function Read-Log {
    if (-not (Test-Path $Log)) { return '' }
    $stream = [IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek([Math]::Min($logStart, $stream.Length), 'Begin')
        (New-Object IO.StreamReader($stream)).ReadToEnd()
    } finally { $stream.Dispose() }
}

function Test-Check([string] $what, [bool] $passed, [string] $actual) {
    $script:checks++
    if ($passed) { Write-Host "  PASS $what" }
    else { Write-Host "  FAIL $what, got: $actual"; $script:failures.Add("$what, got: $actual") }
}

$assigned = @{}

    for ($i = 1; $i -le $Breaks; $i++) {
        Wait-Until { $dte.Debugger.CurrentMode -eq 2 } 60 "break $i"
        $frame = $dte.Debugger.CurrentStackFrame
        Write-Host ''
        Write-Host "=== Break $i : $($frame.FunctionName)  [language: $($frame.Language)]"
        foreach ($check in @($frameExpectations | Where-Object { $_.Break -eq $i })) {
            Test-Check "break $i : frame $($check.Name)" ($frame.FunctionName -clike $check.Name) $frame.FunctionName
        }
        Write-Host '--- Call stack'
        # The Call Stack window asks for every frame's name with its parameters (-ExpectLog checks
        # them); DTE's StackFrames ask only the top frame's. Open it, as the window layout may not.
        if ($ExpectLog -and $i -eq 1) {
            try { $dte.ExecuteCommand('Debug.CallStack') } catch { Write-Host "  (no Call Stack window: $_)" }
        }
        foreach ($f in @(Get-Items $dte.Debugger.CurrentThread.StackFrames ([EnvDTE.StackFrames]) | Select-Object -First 4)) {
            Write-Host "  $($f.FunctionName)  : $($f.ReturnType)"
        }
        Write-Host '--- Locals'
        $locals = @(Get-Items $frame.Locals ([EnvDTE.Expressions]))
        foreach ($local in $locals) {
            Show-Value $local '  '
        }
        if ($StepAt -contains $i) {
            for ($k = 1; $k -le $Steps; $k++) {
                $dte.Debugger.StepOver($true)
                Wait-Until { $dte.Debugger.CurrentMode -eq 2 } 30 "step $k"
                Start-Sleep -Milliseconds 500
                Write-Host "--- Locals after step $k in $($dte.Debugger.CurrentStackFrame.FunctionName)"
                foreach ($local in Get-Items $dte.Debugger.CurrentStackFrame.Locals ([EnvDTE.Expressions])) {
                    Show-Value $local '  '
                }
            }
        }
        $here = @($expectations | Where-Object { $_.Break -eq $i })
        $hereTypes = @($typeExpectations | Where-Object { $_.Break -eq $i })
        $hereExpand = @($expansions | Where-Object { $_.Break -eq $i })
        $hereLocalTypes = @($localTypeExpectations | Where-Object { $_.Break -eq $i })
        $hereLocalChildren = @($localChildExpectations | Where-Object { $_.Break -eq $i })
        $hereFullNames = @($fullNameExpectations | Where-Object { $_.Break -eq $i })
        $hereStatements = @($statements | Where-Object { $_.Break -eq $i })
        if ($here -or $hereTypes -or $hereExpand -or $hereLocalTypes -or $hereLocalChildren -or $hereFullNames -or $hereStatements) { Write-Host '--- Checks' }
        foreach ($check in $hereStatements) {
            # As the Immediate window runs it: a statement, not an expression (TreatAsStatement).
            $e = Invoke-Com $dte.Debugger ([EnvDTE90.Debugger3]) 'GetExpression2' @($check.Text, $true, $true, 10000)
            $what = "break $i : Immediate $($check.Text)"
            Write-Host "    > $($check.Text) = $($e.Value)"
            if (-not $check.Value) {
                Test-Check $what $e.IsValidValue $e.Value
            } elseif ($check.Value -eq '!error') {
                Test-Check "$what => !error" (-not $e.IsValidValue) $e.Value
            } else {
                Test-Check "$what => $($check.Value)" ($e.IsValidValue -and $e.Value -ceq $check.Value) $e.Value
            }
        }
        # Locals again, for the variables the statements declared.
        if ($hereStatements) { $locals = @(Get-Items $dte.Debugger.CurrentStackFrame.Locals ([EnvDTE.Expressions])) }
        foreach ($check in $hereFullNames) {
            $what = "break $i : full name of $($check.Path -join ' / ') => $($check.FullName)"
            # Walk down from the Locals row. Listing a row's children has the result provider log each
            # child's full name under the parent's full name (a Locals row's is its name).
            $node = @($locals | Where-Object { $_.Name -ceq $check.Path[0] } | Select-Object -First 1)
            $fullName = $check.Path[0]
            $readOnly = ''
            $missing = $null
            for ($k = 1; $k -lt $check.Path.Count; $k++) {
                if (-not $node) { $missing = 'no such row'; break }
                if ($null -eq $fullName) { $missing = "no full name for $($check.Path[$k - 1])"; break }
                $name = $check.Path[$k]
                $node = @(Get-Items $node[0].DataMembers ([EnvDTE.Expressions]) | Where-Object { $_.Name -ceq $name } | Select-Object -First 1)
                $pattern = "full name of '$([regex]::Escape($fullName))' / '$([regex]::Escape($name))'( \(read-only\))?: (\(none\)|<(.*)>)\r?$"
                $logged = @([regex]::Matches((Read-Log), $pattern, 'Multiline'))
                if (-not $logged) { $missing = "no full name of $name in the EE log"; break }
                $last = $logged[$logged.Count - 1]
                $fullName = if ($last.Groups[3].Success) { $last.Groups[3].Value } else { $null }
                $readOnly = if ($last.Groups[1].Success) { ' (read-only)' } else { '' }
            }
            if (-not $missing -and -not $node) { $missing = 'no such row' }
            if ($missing) { Test-Check $what $false $missing; continue }
            Write-Host "    $($check.Path -join ' / ') = $($node[0].Value), full name: $(if ($fullName) { $fullName } else { '(none)' })$readOnly"
            if ($null -ne $check.Editable) {
                $kind = if ($check.Editable) { 'editable' } else { 'read-only' }
                Test-Check "$what, $kind" ($check.Editable -eq (-not $readOnly)) $(if ($readOnly) { 'read-only' } else { 'editable' })
            }
            if ($check.FullName -eq '!none') {
                Test-Check $what ($null -eq $fullName) $fullName
            } elseif ($null -eq $fullName) {
                Test-Check $what $false '(none)'
            } elseif ($check.FullName -ne '*' -and $fullName -cne $check.FullName) {
                Test-Check $what $false $fullName
            } else {
                # Add Watch: the full name evaluates to the row's value.
                $e = $dte.Debugger.GetExpression($fullName, $true, 10000)
                Test-Check "$what, evaluates to $($node[0].Value)" ($e.IsValidValue -and $e.Value -ceq $node[0].Value) $e.Value
            }
        }
        foreach ($check in $hereLocalChildren) {
            # The children of a Locals row come from the result providers' GetChildren.
            $row = @($locals | Where-Object { $_.Name -ceq $check.Row } | Select-Object -First 1)
            $children = if ($row) { @(Get-Items $row[0].DataMembers ([EnvDTE.Expressions])) } else { @() }
            foreach ($child in $children) { Show-Value $child '    ' }
            $child = @($children | Where-Object { $_.Name -ceq $check.Child } | Select-Object -First 1)
            $actual = if ($child) { $child[0].Value } elseif ($row) { 'no such child' } else { 'no such row' }
            Test-Check "break $i : Locals row $($check.Row) / $($check.Child) => $($check.Value)" ($child -and $actual -ceq $check.Value) $actual
        }
        foreach ($check in $hereLocalTypes) {
            $row = @($locals | Where-Object { $_.Name -ceq $check.Name } | Select-Object -First 1)
            $actual = if ($row) { $row[0].Type } else { 'no such row' }
            Test-Check "break $i : Locals row $($check.Name) => type $($check.Type)" ($row -and $actual -ceq $check.Type) $actual
        }
        foreach ($check in $hereExpand) {
            # Children come from the result providers' GetChildren; a Raw View row's children too.
            $e = $dte.Debugger.GetExpression($check.Text, $true, 10000)
            $children = @(Get-Items $e.DataMembers ([EnvDTE.Expressions]))
            foreach ($child in $children) {
                Show-Value $child '    '
                if ($child.Name -eq 'Raw View') {
                    foreach ($raw in @(Get-Items $child.DataMembers ([EnvDTE.Expressions]))) { Show-Value $raw '      ' }
                }
            }
            Test-Check "break $i : $($check.Text) expands" ($children.Count -gt 0) 'no children'
        }
        foreach ($check in $hereTypes) {
            $e = $dte.Debugger.GetExpression($check.Text, $true, 10000)
            Test-Check "break $i : $($check.Text) => type $($check.Type)" ($e.IsValidValue -and $e.Type -ceq $check.Type) $e.Type
        }
        foreach ($check in $here) {
            $e = $dte.Debugger.GetExpression($check.Text, $true, 10000)
            if ($check.Value -eq '!error') {
                Test-Check "break $i : $($check.Text) => !error" (-not $e.IsValidValue) $e.Value
            } else {
                Test-Check "break $i : $($check.Text) => $($check.Value)" ($e.IsValidValue -and $e.Value -ceq $check.Value) $e.Value
            }
        }
        $hereHex = @($hexExpectations | Where-Object { $_.Break -eq $i })
        if ($hereHex) {
            $dte.Debugger.HexDisplayMode = $true
            try {
                foreach ($check in $hereHex) {
                    $e = $dte.Debugger.GetExpression($check.Text, $true, 10000)
                    Test-Check "break $i : $($check.Text) => $($check.Value) (hex)" ($e.IsValidValue -and $e.Value -ceq $check.Value) $e.Value
                }
            } finally { $dte.Debugger.HexDisplayMode = $false }
        }
        foreach ($assignment in $Assign) {
            $name, $value = $assignment -split '=', 2
            $e = $dte.Debugger.GetExpression($name, $true, 10000)
            if (-not $e.IsValidValue) { continue }
            $assigned[$assignment] = $true
            try {
                [EnvDTE.Expression].GetProperty('Value').SetValue($e, $value, $null)
                $after = $dte.Debugger.GetExpression($name, $true, 10000)
                Write-Host "--- Assigned $name = $value -> $($after.Value)"
                Test-Check "break $i : $name = $value reads back" ($after.IsValidValue -and $after.Value -ceq $value) $after.Value
            } catch {
                Write-Host "--- Assigning $name = $value failed"
                Test-Check "break $i : $name = $value" $false $_.Exception.InnerException.Message
            }
        }
        foreach ($check in @($localAssignments | Where-Object { $_.Break -eq $i })) {
            $what = if ($check.Child) { "$($check.Row) / $($check.Child)" } else { $check.Row }
            # The row (or the child of a row) in a Locals list.
            $find = {
                param($list)
                $row = @($list | Where-Object { $_.Name -ceq $check.Row } | Select-Object -First 1)
                if (-not $row) { return $null }
                if (-not $check.Child) { return $row[0] }
                @(Get-Items $row[0].DataMembers ([EnvDTE.Expressions]) | Where-Object { $_.Name -ceq $check.Child } | Select-Object -First 1)[0]
            }
            $edited = & $find $locals
            if (-not $edited) { Test-Check "break $i : Locals edit $what = $($check.Value)" $false 'no such row'; continue }
            try {
                [EnvDTE.Expression].GetProperty('Value').SetValue($edited, $check.Value, $null)
                $after = & $find @(Get-Items $dte.Debugger.CurrentStackFrame.Locals ([EnvDTE.Expressions]))
                $actual = if ($after) { $after.Value } else { 'no such row after the edit' }
                Write-Host "--- Edited Locals $what = $($check.Value) -> $actual"
                Test-Check "break $i : Locals edit $what = $($check.Value) reads back" ($after -and $actual -ceq $check.Value) $actual
            } catch {
                Test-Check "break $i : Locals edit $what = $($check.Value)" $false $_.Exception.InnerException.Message
            }
        }
        Write-Host '--- Watch'
        foreach ($text in $Expressions) {
            $e = $dte.Debugger.GetExpression($text, $true, 10000)
            if (-not $e.IsValidValue -and $e.Value -match 'error CS0103|does not exist|is not defined') { continue }
            Show-Value $e '  '
            $children = @(Get-Items $e.DataMembers ([EnvDTE.Expressions]) | Select-Object -First 4)
            foreach ($child in $children) {
                Show-Value $child '      '
                if ($child.Name -eq 'Raw View') {
                    foreach ($raw in @(Get-Items $child.DataMembers ([EnvDTE.Expressions]) | Select-Object -First 3)) { Show-Value $raw '          ' }
                }
            }
        }
        # After the last break, wait for the next break or the end when they are checked: right after
        # Go($false) the debugger can still report break mode.
        $dte.Debugger.Go($i -eq $Breaks -and ($ExpectEnd -or $Tracepoint))
    }

    foreach ($assignment in $Assign) {
        if (-not $assigned[$assignment]) { Test-Check "$assignment at some break" $false 'the name evaluated at none' }
    }

    if ($ExpectLog) {
        $written = Read-Log
        foreach ($text in $ExpectLog) {
            $found = if ($text.Contains('*')) { @($written -split "`r?`n" | Where-Object { $_ -clike "*$text*" }).Count -gt 0 } else { $written.Contains($text) }
            Test-Check "the EE log has '$text'" $found 'not in this run''s log'
        }
    }

    if ($ExpectEnd -or $Tracepoint) {
        # The last Go waited for the next break or the end (or the debugger is still settling).
        Wait-Until { $dte.Debugger.CurrentMode -ne 3 } 60 'the debuggee to finish or break'
        if ($ExpectEnd) {
            $broke = if ($dte.Debugger.CurrentMode -eq 2) { "a break in $($dte.Debugger.CurrentStackFrame.FunctionName)" } else { 'the end' }
            Test-Check 'the debuggee runs to its end after the last break' ($dte.Debugger.CurrentMode -eq 1) $broke
        }
    }

    if ($Tracepoint) {
        # The debuggee runs to its end; then read the tracepoint's output from the Debug pane.
        Wait-Until { $dte.Debugger.CurrentMode -eq 1 } 60 'the debuggee to finish'
        $toolWindows = [EnvDTE80.DTE2].GetProperty('ToolWindows').GetValue($dte, $null)
        $outputWindow = [EnvDTE80.ToolWindows].GetProperty('OutputWindow').GetValue($toolWindows, $null)
        $panes = [EnvDTE.OutputWindow].GetProperty('OutputWindowPanes').GetValue($outputWindow, $null)
        $pane = [EnvDTE.OutputWindowPanes].GetMethod('Item').Invoke($panes, @([object]'Debug'))
        $document = [EnvDTE.OutputWindowPane].GetProperty('TextDocument').GetValue($pane, $null)
        # VS writes to the pane asynchronously, so the last lines can come after the debuggee ends:
        # read until the expected lines are there, for up to 10 s.
        $prefix = [regex]::Escape(($Tracepoint -split '\{')[0])
        $deadline = (Get-Date).AddSeconds(10)
        do {
            $text = $document.StartPoint.CreateEditPoint().GetText($document.EndPoint)
            $traced = @($text -split "`r?`n" | Where-Object { $_ -match $prefix })
            $missing = @($ExpectTrace | Where-Object { $e = $_; -not ($traced | Where-Object { $_.Trim() -ceq $e }) })
            if ($missing.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        Write-Host '--- Tracepoint output'
        $traced | ForEach-Object { Write-Host "  $_" }
        foreach ($expected in $ExpectTrace) {
            Test-Check "tracepoint wrote '$expected'" (@($traced | Where-Object { $_.Trim() -ceq $expected }).Count -gt 0) "$($traced.Count) other lines"
        }
    }
}
finally {
    Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
    # Leave no breakpoints of ours behind in the user's solution.
    if ($Condition -or $Tracepoint) {
        foreach ($b in @(Get-Items $dte.Debugger.Breakpoints ([EnvDTE.Breakpoints]))) {
            if ([EnvDTE.Breakpoint].GetProperty('FileLine').GetValue($b, $null) -eq $line) { [EnvDTE.Breakpoint].GetMethod('Delete').Invoke($b, @()) | Out-Null }
        }
    }
    if ($dte.Debugger.CurrentMode -ne 1) { $dte.Debugger.TerminateAll() }
    if (-not $debuggee.HasExited) { Stop-Process -Id $debuggee.Id -Force }
    if (($startedVs -or $QuitVs) -and -not $KeepVsOpen) {
        # The exe opened as a project makes a throwaway solution; close it unsaved, or Quit asks to save it.
        try { $dte.Solution.Close($false) } catch { }
        $dte.Quit()
    }
}

if ($checks) {
    Write-Host ''
    if ($failures.Count) {
        Write-Host "$($failures.Count) of $checks checks failed:"
        $failures | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    Write-Host "All $checks checks passed."
}
