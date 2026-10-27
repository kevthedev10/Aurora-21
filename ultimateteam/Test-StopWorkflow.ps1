# Headless ownership checks only: never runs the stop script or changes services.
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Stop-UltimateTeam.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$function = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Test-ProcessOwnsLaunchWorker'
}, $true)
if (-not $function) { throw 'Launch worker ownership check is missing' }
Invoke-Expression $function.Extent.Text
$scriptPath = Join-Path $PSScriptRoot 'Launch-UltimateTeam.ps1'
$shellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$checks = 0
function Assert-Owned($process, [bool]$expected, [string]$message) {
    if ((Test-ProcessOwnsLaunchWorker $process $scriptPath) -ne $expected) { throw $message }
    $script:checks++
}
Assert-Owned ([pscustomobject]@{ExecutablePath=$shellPath;CommandLine=('powershell.exe -NoProfile -File "'+$scriptPath+'" -UserConfigRoot "C:\Fixture Settings"')}) $true 'Own quoted worker must match'
Assert-Owned ([pscustomobject]@{ExecutablePath=$shellPath;CommandLine=('powershell.exe -File "'+$scriptPath.ToUpperInvariant()+'"')}) $true 'Windows path matching must ignore case'
Assert-Owned ([pscustomobject]@{ExecutablePath='C:\Other\powershell.exe';CommandLine=('powershell.exe -File "'+$scriptPath+'"')}) $false 'Foreign executable must not match'
Assert-Owned ([pscustomobject]@{ExecutablePath=$shellPath;CommandLine='powershell.exe -File "C:\Other Checkout\Launch-UltimateTeam.ps1"'}) $false 'Other checkout must not match'
Assert-Owned ([pscustomobject]@{ExecutablePath=$shellPath;CommandLine=('powershell.exe -File "'+$scriptPath+'.backup"')}) $false 'Script prefix must not match'
Assert-Owned ([pscustomobject]@{ExecutablePath=$shellPath;CommandLine=('powershell.exe -Command Write-Host "'+$scriptPath+'"')}) $false 'Text mentioning the script must not match'
Assert-Owned ([pscustomobject]@{ExecutablePath=$shellPath;CommandLine=('powershell.exe -File "'+(Join-Path $PSScriptRoot 'Stop-UltimateTeam.ps1')+'"')}) $false 'Stop worker must not cancel itself'
Assert-Owned ([pscustomobject]@{ExecutablePath=$null;CommandLine=$null}) $false 'Missing process evidence must not match'
Write-Host "PASS: $checks stop-worker ownership checks; no process/service execution."
