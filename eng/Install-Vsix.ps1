[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

# Builds the VSIX and reinstalls it into the normal instance (not Exp) of the latest VS 2026 or later,
# Insiders included. The version changes only with a new commit (Directory.Build.targets), so the
# installed copy is removed first: VSIXInstaller does not install over the same version.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instance = @(& $vswhere -latest -prerelease -products '*' -version '18.0' -format json | ConvertFrom-Json)[0]
if (-not $instance) { throw 'No VS 2026 or later installation found.' }
$devenv = Join-Path $instance.installationPath 'Common7\IDE\devenv.exe'

# VSIXInstaller cannot change the extensions of an instance while any of its devenv processes run.
$running = Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object { $_.ExecutablePath -eq $devenv }
if ($running) { throw "Close all windows of $($instance.displayName) before installing." }

$project = Join-Path $root 'src\FSharp.ExpressionEvaluator.Vsix\FSharp.ExpressionEvaluator.Vsix.fsproj'
if (-not $SkipBuild) {
    & dotnet build $project -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$vsix = Join-Path $root "src\FSharp.ExpressionEvaluator.Vsix\bin\$Configuration\net48\FSharp.ExpressionEvaluator.Vsix.vsix"
if (-not (Test-Path $vsix)) { throw "$vsix is missing: build without -SkipBuild." }

$manifest = [xml](Get-Content (Join-Path $root 'src\FSharp.ExpressionEvaluator.Vsix\source.extension.vsixmanifest'))
$id = $manifest.PackageManifest.Metadata.Identity.Id
$installer = Join-Path $instance.enginePath 'VsixInstaller\VSIXInstaller.exe'

# A failure here usually means that the extension is not installed; the install below reports real problems.
$uninstall = Start-Process $installer -ArgumentList @('/quiet', "/instanceIds:$($instance.instanceId)", "/uninstall:$id") -PassThru -Wait
if ($uninstall.ExitCode -ne 0) { Write-Host "Uninstall exited with code $($uninstall.ExitCode); continuing (not installed?)." }

$install = Start-Process $installer -ArgumentList @('/quiet', "/instanceIds:$($instance.instanceId)", "`"$vsix`"") -PassThru -Wait
if ($install.ExitCode -ne 0) { throw "Installation failed with exit code $($install.ExitCode); see the VSIX installer log in TEMP." }

$update = Start-Process $devenv -ArgumentList '/UpdateConfiguration' -PassThru -Wait
if ($update.ExitCode -ne 0) { throw "Configuration refresh failed with exit code $($update.ExitCode)." }

Write-Host "Reinstalled $vsix ($Configuration) into $($instance.displayName)."
