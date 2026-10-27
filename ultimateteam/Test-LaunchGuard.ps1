[CmdletBinding()]
param([switch]$Hold, [string]$FixtureRoot, [string]$ReadyEvent, [string]$ReleaseEvent)

# Only isolated mutex/event fixtures and a test PowerShell child; no game/services/UAC.
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Launch-UltimateTeam.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launch worker does not parse.' }
$preflight = $ast.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-UltimateTeamGameLaunchAvailable' }, $true)
$firstStatusWrite = $ast.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Save-Status' }, $true)
if (-not $preflight -or -not $firstStatusWrite -or $preflight.Extent.StartOffset -gt $firstStatusWrite.Extent.StartOffset) {
    throw 'Existing-game rejection must precede runtime/status changes.'
}
foreach ($name in @('Enter-UltimateTeamLaunchGuard', 'Assert-UltimateTeamGameLaunchAvailable')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $function) { throw "Missing launch guard function: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
if ($Hold) {
    $guard = Enter-UltimateTeamLaunchGuard $FixtureRoot
    $ready = [Threading.EventWaitHandle]::OpenExisting($ReadyEvent)
    $release = [Threading.EventWaitHandle]::OpenExisting($ReleaseEvent)
    try { [void]$ready.Set(); if (-not $release.WaitOne(10000)) { throw 'Fixture release timed out.' } }
    finally { $guard.ReleaseMutex(); $guard.Dispose(); $ready.Dispose(); $release.Dispose() }
    exit 0
}

Assert-UltimateTeamGameLaunchAvailable @() $false $false
Assert-UltimateTeamGameLaunchAvailable @([pscustomobject]@{ Id = 123 }) $true $false
Assert-UltimateTeamGameLaunchAvailable @([pscustomobject]@{ Id = 123 }) $false $true
$rejected = $false
try { Assert-UltimateTeamGameLaunchAvailable @([pscustomobject]@{ Id = 123 }) $false $false }
catch { if ($_.Exception.Message -notmatch 'already running') { throw }; $rejected = $true }
if (-not $rejected) { throw 'An existing game did not reject a new direct launch.' }

$suffix = [Guid]::NewGuid().ToString('N')
$FixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "Aurora21-Guard-Fixture-$suffix"
$ReadyEvent = "Local\Aurora21-Guard-Ready-$suffix"
$ReleaseEvent = "Local\Aurora21-Guard-Release-$suffix"
$ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $ReadyEvent)
$release = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $ReleaseEvent)
$child = $null
try {
    $info = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = '-NoProfile -File "{0}" -Hold -FixtureRoot "{1}" -ReadyEvent "{2}" -ReleaseEvent "{3}"' -f $PSCommandPath, $FixtureRoot, $ReadyEvent, $ReleaseEvent
    $child = [Diagnostics.Process]::Start($info)
    if (-not $ready.WaitOne(5000)) { throw 'Guard fixture child did not become ready.' }
    $rejected = $false
    try { $unexpected = Enter-UltimateTeamLaunchGuard $FixtureRoot; $unexpected.ReleaseMutex(); $unexpected.Dispose() }
    catch { if ($_.Exception.Message -notmatch 'already in progress') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Concurrent launch was not rejected.' }
    [void]$release.Set()
    if (-not $child.WaitForExit(5000) -or $child.ExitCode -ne 0) { throw 'Guard fixture child failed.' }
    $guard = Enter-UltimateTeamLaunchGuard $FixtureRoot
    $guard.ReleaseMutex(); $guard.Dispose()
    Write-Host 'PASS: existing game rejected; services/external modes preserved; concurrent start rejected; guard reusable after exit.'
} finally {
    [void]$release.Set()
    if ($child) { if (-not $child.HasExited) { $child.Kill() }; $child.Dispose() }
    $ready.Dispose(); $release.Dispose()
}
