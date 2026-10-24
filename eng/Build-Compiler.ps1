[CmdletBinding()]
param(
    # A dotnet/fsharp checkout of the expression-evaluator branch, built once (e.g. `.\build.cmd -c Debug`).
    # Defaults to FSHARP_EE_FSHARP_REPO, else ..\fsharp.worktrees\expression-evaluator next to this repo.
    [string] $FSharpRepo = $env:FSHARP_EE_FSHARP_REPO,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

# Builds the expression evaluator's private F# compiler (docs/DESIGN.md §8.2): FSharp.Compiler.Service
# from the dotnet/fsharp branch, under its own assembly identity (FSharp.ExpressionEvaluator.Compiler)
# and unsigned, and copies it to artifacts\compiler, where the ExpressionCompiler project and the VSIX
# take it from.

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
        -p:FSharpExpressionEvaluatorBuild=true -p:BuildProjectReferences=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

$output = Join-Path $FSharpRepo "artifacts\bin\FSharp.ExpressionEvaluator.Compiler\$Configuration\netstandard2.0"
$target = Join-Path $root 'artifacts\compiler'
New-Item -ItemType Directory -Force $target | Out-Null
# The PDB is embedded in the dll.
Copy-Item (Join-Path $output 'FSharp.ExpressionEvaluator.Compiler.dll') $target -Force
$commit = (& git -C $FSharpRepo rev-parse --short HEAD)
Set-Content (Join-Path $target 'source.txt') "$FSharpRepo @ $commit ($Configuration)"
Write-Host "Copied FSharp.ExpressionEvaluator.Compiler ($commit, $Configuration) to $target"
