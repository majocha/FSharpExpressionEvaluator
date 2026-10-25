[CmdletBinding()]
param(
    # A dotnet/fsharp checkout, built once (e.g. `.\build.cmd -c Debug`). It needs no changes for the
    # expression evaluator. Defaults to FSHARP_EE_FSHARP_REPO, else ..\fsharp.worktrees\expression-evaluator
    # next to this repo.
    [string] $FSharpRepo = $env:FSHARP_EE_FSHARP_REPO,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

# Builds the expression evaluator's private F# compiler (docs/DESIGN.md §8.2): FSharp.Compiler.Service
# from a dotnet/fsharp checkout, under its own assembly identity (FSharp.ExpressionEvaluator.Compiler),
# unsigned and with the evaluator as a friend (PrivateCompiler.targets), and copies it to
# artifacts\compiler, where the ExpressionCompiler project and the VSIX take it from.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
if (-not $FSharpRepo) { $FSharpRepo = Join-Path (Split-Path $root -Parent) 'fsharp.worktrees\expression-evaluator' }
$project = Join-Path $FSharpRepo 'src\Compiler\FSharp.Compiler.Service.fsproj'
if (-not (Test-Path $project)) { throw "No dotnet/fsharp checkout at $FSharpRepo (set -FSharpRepo or FSHARP_EE_FSHARP_REPO)." }

$env:BUILDING_USING_DOTNET = 'true'
# Run from the checkout, so that its global.json picks the .NET SDK it needs.
Push-Location $FSharpRepo
try {
    & dotnet build-server shutdown | Out-Null
    # Project references (FSharp.Core and the build tools) come from the checkout's own build.
    & dotnet build $project -c $Configuration -f netstandard2.0 -nodeReuse:false -v minimal `
        "-p:CustomBeforeMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'PrivateCompiler.targets')" `
        -p:BuildProjectReferences=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
    # The checkout's own fsc compiles the tests' fixtures, as the private compiler would: released
    # SDKs don't lower task state machines in Debug builds (docs/DESIGN.md §16).
    # With its project references, so that it gets the checkout's FSharp.Core.
    & dotnet build (Join-Path $FSharpRepo 'src\fsc\fscProject\fsc.fsproj') -c $Configuration -f net11.0 -nodeReuse:false `
        -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build of fsc failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

$output = Join-Path $FSharpRepo "artifacts\bin\FSharp.ExpressionEvaluator.Compiler\$Configuration\netstandard2.0"
$target = Join-Path $root 'artifacts\compiler'
New-Item -ItemType Directory -Force $target | Out-Null
# The PDB is embedded in the dll.
Copy-Item (Join-Path $output 'FSharp.ExpressionEvaluator.Compiler.dll') $target -Force
$fscTarget = Join-Path $root 'artifacts\fsc'
if (Test-Path $fscTarget) { Remove-Item $fscTarget -Recurse -Force }
Copy-Item (Join-Path $FSharpRepo "artifacts\bin\fsc\$Configuration\net11.0") $fscTarget -Recurse
$commit = (& git -C $FSharpRepo rev-parse --short HEAD)
Set-Content (Join-Path $target 'source.txt') "$FSharpRepo @ $commit ($Configuration)"
Write-Host "Copied FSharp.ExpressionEvaluator.Compiler ($commit, $Configuration) to $target, and fsc to $fscTarget"
