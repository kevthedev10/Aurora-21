[CmdletBinding()]
param()

# Headless settings/portability checks for the current WPF launcher. No service startup, UAC,
# game launch, debugger, window construction, or visible UI.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'User-Settings.ps1')
$launcherAssemblyPath = Join-Path $PSScriptRoot 'Aurora21-UltimateTeam-Launcher.dll'
$probeProject = Join-Path $PSScriptRoot 'tests\LauncherSettingsProbe\LauncherSettingsProbe.csproj'
if (-not (Test-Path -LiteralPath $launcherAssemblyPath -PathType Leaf)) { throw 'Build the launcher first.' }
if (-not (Test-Path -LiteralPath $probeProject -PathType Leaf)) { throw "Settings probe was not found: $probeProject" }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Aurora21-Launcher-Tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks = 0

function Assert-Launcher($condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    $script:checks++
    Write-Host "PASS: $message"
}

$probeOutput = & dotnet run --project $probeProject --configuration Release `
    -p:BaseOutputPath="$testRoot\bin\" -p:BaseIntermediateOutputPath="$testRoot\obj\" `
    -- $PSScriptRoot $testRoot
if ($LASTEXITCODE -ne 0) { throw "Native launcher settings probe failed with exit code $LASTEXITCODE.`n$($probeOutput -join [Environment]::NewLine)" }
$probeOutput | Write-Host
Assert-Launcher ($probeOutput -match 'native .NET launcher settings checks') 'Native .NET 10 settings probe completed'

$project = [xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot 'UltimateTeamLauncher.csproj'))
Assert-Launcher ($project.Project.PropertyGroup.UseWPF -eq 'true') 'Launcher project uses WPF'
Assert-Launcher (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'UltimateTeamLauncher.MainWindow.xaml') -PathType Leaf) 'WPF window resource is present'
Assert-Launcher (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Aurora21-UltimateTeam-Launcher.exe') -PathType Leaf) 'Windows apphost is present'

foreach ($file in @('Launch-UltimateTeam.ps1', 'Stop-UltimateTeam.ps1', 'User-Settings.ps1', 'Build-FIFA21-Launcher.ps1', 'Offline-UltimateTeam.ps1')) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors)
    Assert-Launcher ($errors.Count -eq 0) "$file parses without errors"
}
Write-Host "$checks PowerShell/WPF checks passed. Test fixtures: $testRoot"
