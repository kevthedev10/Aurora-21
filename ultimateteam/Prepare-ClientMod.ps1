[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [string]$UserConfigRoot = (Join-Path $env:LOCALAPPDATA 'Aurora21\UltimateTeam')
)

# Prepare only. The caller keeps its existing game launch and authentication flow.
$ErrorActionPreference = 'Stop'
try {
    $GamePath = [IO.Path]::GetFullPath($GamePath)
    $ResultPath = [IO.Path]::GetFullPath($ResultPath)
    $UserConfigRoot = [IO.Path]::GetFullPath($UserConfigRoot)
    if (-not (Test-Path -LiteralPath $GamePath -PathType Leaf) -or (Split-Path -Leaf $GamePath) -ine 'FIFA21.exe') {
        throw 'GamePath must point to the installed FIFA21.exe.'
    }
    if ($GamePath.Contains('"') -or $UserConfigRoot.Contains('"')) { throw 'Paths cannot contain quotation marks.' }
    if (Get-Process -Name 'FIFA21', 'FIFA21_Trial' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $GamePath }) {
        throw 'Close this FIFA 21 before preparing its client mod.'
    }
    . (Join-Path $PSScriptRoot 'Client-LeaderboardMods.ps1')
    $manifest = Get-UltimateTeamLeaderboardModManifest
    $modData = Get-UltimateTeamLeaderboardModPath -GamePath $GamePath -UserConfigRoot $UserConfigRoot -Manifest $manifest -ToolRoot (Join-Path $PSScriptRoot 'client-mod-tools')
    $result = [ordered]@{
        schema = 1
        revision = $manifest.revision
        modDataPath = $modData
        gameArguments = ('-dataPath "{0}"' -f $modData)
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
    $temporary = $ResultPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $ResultPath -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    Write-Output "[READY] $ResultPath"
    exit 0
} catch {
    [Console]::Error.WriteLine('[FAILED] ' + $_.Exception.Message)
    exit 1
}
