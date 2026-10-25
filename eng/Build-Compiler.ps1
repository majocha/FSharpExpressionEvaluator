[CmdletBinding()]
param(
    # A dotnet/fsharp checkout to build instead of the pinned commit (eng\fsharp.commit), e.g. to try
    # a newer one. It needs no changes for the expression evaluator, and must have been built once
    # in the same configuration (`.\build.cmd -c Release`). Defaults to FSHARP_EE_FSHARP_REPO.
    [string] $FSharpRepo = $env:FSHARP_EE_FSHARP_REPO,
    # Release by default, as the VSIX ships it: warm queries are about 20% faster than with a Debug
    # build (docs/DESIGN.md §17). Debug is for stepping into the compiler.
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

# Builds the expression evaluator's private F# compiler (docs/DESIGN.md §8.2): FSharp.Compiler.Service
# from dotnet/fsharp, under its own assembly identity (FSharp.ExpressionEvaluator.Compiler), unsigned
# and with the evaluator as a friend (PrivateCompiler.targets), and copies it to artifacts\compiler,
# where the ExpressionCompiler project and the VSIX take it from. It also copies the same commit's
# fsc to artifacts\fsc, which compiles the tests' fixtures.
#
# By default the commit is the one pinned in eng\fsharp.commit. It is fetched (shallow) and built once
# into artifacts\fsharp-ee, in a folder named by the commit's first 12 characters: untracked, inside
# this checkout, so that it stays with the repo's other build output. FSHARP_EE_CACHE overrides it.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent

function Invoke-Native([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

# Builds the private compiler and fsc in a checkout. Run from the checkout, so that its global.json
# picks the .NET SDK it needs.
function Build-Checkout([string] $checkout, [bool] $withReferences) {
    $env:BUILDING_USING_DOTNET = 'true'
    Push-Location $checkout
    try {
        & dotnet build-server shutdown | Out-Null
        $project = Join-Path $checkout 'src\Compiler\FSharp.Compiler.Service.fsproj'
        if ($withReferences) {
            # The project references (FSharp.Core, FSharp.Build, fslex and fsyacc) in the layout
            # `dotnet build` expects, under the normal identity.
            Invoke-Native 'Build of the references' {
                & dotnet build $project -c $Configuration -f netstandard2.0 -nodeReuse:false -v minimal
            }
        }
        Invoke-Native 'Build of the private compiler' {
            & dotnet build $project -c $Configuration -f netstandard2.0 -nodeReuse:false -v minimal `
                "-p:CustomBeforeMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'PrivateCompiler.targets')" `
                -p:BuildProjectReferences=false
        }
        # The commit's own fsc compiles the tests' fixtures, as the private compiler would: released
        # SDKs don't lower task state machines in Debug builds (docs/DESIGN.md §16). With its project
        # references, so that it gets the commit's FSharp.Core.
        Invoke-Native 'Build of fsc' {
            & dotnet build (Join-Path $checkout 'src\fsc\fscProject\fsc.fsproj') -c $Configuration -f net11.0 `
                -nodeReuse:false -v minimal
        }
    } finally {
        Pop-Location
    }
}

if ($FSharpRepo) {
    if (-not (Test-Path (Join-Path $FSharpRepo 'src\Compiler\FSharp.Compiler.Service.fsproj'))) {
        throw "No dotnet/fsharp checkout at $FSharpRepo."
    }
    $checkout = $FSharpRepo
    Build-Checkout $checkout $false
} else {
    # eng\fsharp.commit: the remote, then the commit (lines starting with # are comments).
    $pin = @(Get-Content (Join-Path $PSScriptRoot 'fsharp.commit') | Where-Object { $_ -and -not $_.StartsWith('#') })
    $remote, $sha = $pin[0].Trim(), $pin[1].Trim()
    # A short path: the repo's deepest files and build outputs would pass Windows' 260 characters.
    $cache = if ($env:FSHARP_EE_CACHE) { $env:FSHARP_EE_CACHE } else { Join-Path $root 'artifacts\fsharp-ee' }
    $checkout = Join-Path $cache $sha.Substring(0, 12)
    $built = Join-Path $checkout ".ee-built-$Configuration"

    if (-not (Test-Path $built)) {
        if (-not (Test-Path (Join-Path $checkout '.git'))) {
            New-Item -ItemType Directory -Force $checkout | Out-Null
            Invoke-Native 'git init' { & git -C $checkout init -q }
            Invoke-Native 'git config' { & git -C $checkout config core.longpaths true }
            Invoke-Native 'git remote add' { & git -C $checkout remote add origin $remote }
        }
        if ((& git -C $checkout rev-parse --verify -q HEAD) -ne $sha) {
            Write-Host "Fetching dotnet/fsharp $sha from $remote into $checkout"
            Invoke-Native 'git fetch' { & git -C $checkout fetch -q --depth 1 origin $sha }
            Invoke-Native 'git checkout' { & git -C $checkout -c advice.detachedHead=false checkout -q FETCH_HEAD }
        }
        # The repo's own build first: it restores its toolset and .NET SDK (into .dotnet), and generates
        # the lexer and parser with its bootstrap tools (fsyacc built by `dotnet build` in Debug
        # overflows the stack on the F# grammar).
        Invoke-Native 'Build of the checkout' { & (Join-Path $checkout 'Build.cmd') -c $Configuration -noVisualStudio }
        Build-Checkout $checkout $true
        Set-Content $built (Get-Date -Format o)
    }
}

$output = Join-Path $checkout "artifacts\bin\FSharp.ExpressionEvaluator.Compiler\$Configuration\netstandard2.0"
$target = Join-Path $root 'artifacts\compiler'
New-Item -ItemType Directory -Force $target | Out-Null
# The PDB is embedded in the dll.
Copy-Item (Join-Path $output 'FSharp.ExpressionEvaluator.Compiler.dll') $target -Force
$fscTarget = Join-Path $root 'artifacts\fsc'
if (Test-Path $fscTarget) { Remove-Item $fscTarget -Recurse -Force }
Copy-Item (Join-Path $checkout "artifacts\bin\fsc\$Configuration\net11.0") $fscTarget -Recurse
$commit = (& git -C $checkout rev-parse --short HEAD)
Set-Content (Join-Path $target 'source.txt') "$checkout @ $commit ($Configuration)"
Write-Host "Copied FSharp.ExpressionEvaluator.Compiler ($commit, $Configuration) to $target, and fsc to $fscTarget"
