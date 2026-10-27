[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$UserConfigRoot,
    [string]$ResultPath,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
& (Join-Path $packageRoot 'Verify-Package.ps1')
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Package verification failed.' }
. (Join-Path $packageRoot 'ultimateteam\Client-LeaderboardMods.ps1')
$manifest = Get-UltimateTeamLeaderboardModManifest
$toolRoot = Join-Path $packageRoot 'ultimateteam\client-mod-tools'
if ($ValidateOnly) {
    foreach ($test in @('--self-test-zip', '--self-test-localisation', '--self-test-currency-art', '--self-test-fmt')) {
        & (Join-Path $toolRoot 'Aurora21-LeaderboardUiTool.exe') $test
        if ($LASTEXITCODE -ne 0) { throw "Compiler check failed: $test" }
    }
    Write-Host ('Package verified. Client profile: ' + $manifest.revision)
    return
}
if (-not $GamePath) { throw 'Supply -GamePath with the full path to FIFA21.exe.' }
if (Get-Process -Name FIFA21 -ErrorAction SilentlyContinue) { throw 'Close FIFA 21 before preparing mods. No process was stopped.' }
if (-not $UserConfigRoot) { $UserConfigRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Aurora21\UltimateTeam' }
$UserConfigRoot = [IO.Path]::GetFullPath($UserConfigRoot)
if (-not $ResultPath) { $ResultPath = Join-Path $UserConfigRoot 'client-mods\launcher-mod-result.json' }
$ResultPath = [IO.Path]::GetFullPath($ResultPath)
$modData = Get-UltimateTeamLeaderboardModPath -GamePath $GamePath -UserConfigRoot $UserConfigRoot -Manifest $manifest -ToolRoot $toolRoot
$result = [ordered]@{
    schema = 1
    revision = $manifest.revision
    modDataPath = $modData
    gameArguments = ('-dataPath "{0}"' -f $modData)
    requiresGameRestart = $true
    verifiedLanguages = 21
    verifiedArtworkAssets = 11
}
New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
[IO.File]::WriteAllText($ResultPath, ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host ('Ready. Read launcher result: ' + $ResultPath)
