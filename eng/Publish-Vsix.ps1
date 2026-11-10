[CmdletBinding()]
param(
    # Azure DevOps personal access token with the Marketplace (Manage) scope and "All accessible
    # organizations". If not set, the script uses $env:VS_MARKETPLACE_PAT.
    [string] $PersonalAccessToken = $env:VS_MARKETPLACE_PAT,
    [switch] $SkipBuild,
    # Publish from a working tree with changes. The version comes from the commit count only,
    # so a later commit then gets the same version, and the Marketplace refuses it.
    [switch] $AllowDirty
)

# Builds the Release VSIX and uploads it to the Visual Studio Marketplace. The listing comes from
# src/FSharp.ExpressionEvaluator.Vsix/publishManifest.json, and the page from overview.md beside it.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $PersonalAccessToken) {
    throw 'No personal access token: set -PersonalAccessToken or $env:VS_MARKETPLACE_PAT.'
}

$root = Split-Path $PSScriptRoot -Parent
$vsixDir = Join-Path $root 'src\FSharp.ExpressionEvaluator.Vsix'

if (-not $AllowDirty) {
    $changes = & git -C $root status --porcelain
    if ($changes) { throw 'The working tree has changes. Commit them first, or use -AllowDirty.' }
}

if (-not $SkipBuild) {
    & dotnet build (Join-Path $vsixDir 'FSharp.ExpressionEvaluator.Vsix.fsproj') -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$vsix = Join-Path $vsixDir 'bin\Release\net48\FSharp.ExpressionEvaluator.Vsix.vsix'
if (-not (Test-Path $vsix)) { throw "$vsix is missing: build without -SkipBuild." }

# VsixPublisher.exe comes with the "Visual Studio extension development" workload.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$publisher = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VSSDK `
    -find 'VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe' | Select-Object -First 1
if (-not $publisher) { throw 'VsixPublisher.exe not found: install the Visual Studio extension development workload.' }

$manifest = Join-Path $vsixDir 'publishManifest.json'
& $publisher publish -payload $vsix -publishManifest $manifest -personalAccessToken $PersonalAccessToken
if ($LASTEXITCODE -ne 0) { throw "VsixPublisher failed with exit code $LASTEXITCODE." }

$listing = Get-Content $manifest -Raw | ConvertFrom-Json
Write-Host "Published $vsix as $($listing.publisher).$($listing.identity.internalName)."
