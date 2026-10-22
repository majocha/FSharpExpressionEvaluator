[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

# Builds the VSIX and installs it into the Exp instance of the latest VS 2026 (18.x), Insiders included.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instance = @(& $vswhere -latest -prerelease -products '*' -version '[18.0,19.0)' -format json | ConvertFrom-Json)[0]
if (-not $instance) { throw 'No VS 2026 installation found.' }
$devenv = Join-Path $instance.installationPath 'Common7\IDE\devenv.exe'

$runningExp = Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" |
    Where-Object { $_.ExecutablePath -eq $devenv -and $_.CommandLine -match '(?i)/rootsuffix\s+"?Exp' }
if ($runningExp) { throw 'Close the experimental Visual Studio instance before deploying.' }

$project = Join-Path $root 'src\FSharp.ExpressionEvaluator.Vsix\FSharp.ExpressionEvaluator.Vsix.fsproj'
if (-not $SkipBuild) {
    & dotnet build $project -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$vsix = Join-Path $root "src\FSharp.ExpressionEvaluator.Vsix\bin\$Configuration\net48\FSharp.ExpressionEvaluator.Vsix.vsix"
$installer = Join-Path $instance.enginePath 'VsixInstaller\VSIXInstaller.exe'
$install = Start-Process $installer -ArgumentList @('/quiet', '/rootSuffix:Exp', "/instanceIds:$($instance.instanceId)", "`"$vsix`"") -PassThru -Wait
if ($install.ExitCode -ne 0) { throw "Installation failed with exit code $($install.ExitCode); see the VSIX installer log in TEMP." }

$update = Start-Process $devenv -ArgumentList '/RootSuffix Exp /UpdateConfiguration' -PassThru -Wait
if ($update.ExitCode -ne 0) { throw "Configuration refresh failed with exit code $($update.ExitCode)." }

Write-Host "Installed $vsix into $($instance.displayName) (Exp)."
