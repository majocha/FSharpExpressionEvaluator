<#
.SYNOPSIS
  Checks the deployed expression evaluator in the Exp instance of VS 2026 or later: watch values,
  assignment, a breakpoint condition and a tracepoint in tests\TestDebuggee, each a run of
  Probe-Debugger.ps1 with expected values. Exits with 1 when a check fails.
  Deploy first (eng\Deploy-Experimental.ps1). Run with Windows PowerShell 5.1 (powershell.exe).
#>
[CmdletBinding()]
param(
    [ValidateSet('net10.0', 'net48')]
    [string] $Target = 'net10.0',
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
                '1: undefinedName => !error'
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
                '6: List.map (fun i -> i * 2) xs => [2; 4; 6]'
            )
            # A closure's captured value, a parameter and a class let field.
            Assign = @('captured=42', 'by=5', 'count=100')
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
