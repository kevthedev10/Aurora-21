[CmdletBinding()]
param(
    [ValidateSet('Get', 'Rename', 'DailyStatus', 'ClaimDaily')]
    [string]$Action = 'Get',
    [string]$ClubName,
    [string]$ClubAbbr
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'Player-Access-Common.ps1')

$settings = Get-Project21ControlPlaneSettings -Root $Root
if ($settings.enabled -ne $true) {
    throw 'The Aurora 21 (Alpha v1) control plane is not configured.'
}

$accessToken = Get-Project21AccessToken
if (-not $accessToken) {
    throw 'Connect Discord on the Play tab before opening your club.'
}

$baseUrl = [string]$settings.baseUrl
if ($Action -eq 'Rename') {
    $ClubName = ([string]$ClubName -replace '[\x00-\x1f\x7f-\x9f]', '' -replace '\s+', ' ').Trim()
    $ClubAbbr = ([string]$ClubAbbr -replace '[\x00-\x1f\x7f-\x9f]', '' -replace '\s+', '').Trim().ToUpperInvariant()
    if (-not $ClubName -or $ClubName.Length -gt 32) {
        throw 'Club name must contain 1 to 32 characters.'
    }
    if (-not $ClubAbbr -or $ClubAbbr.Length -gt 3) {
        throw 'Club abbreviation must contain 1 to 3 characters.'
    }

    $queued = Invoke-Project21ControlPlane -Method POST `
        -Uri ($baseUrl + '/api/device/club/name') `
        -AccessToken $accessToken `
        -Body @{ clubName = $ClubName; clubAbbr = $ClubAbbr }
}
elseif ($Action -eq 'DailyStatus') {
    $queued = Invoke-Project21ControlPlane -Method POST `
        -Uri ($baseUrl + '/api/device/daily-drop/query') `
        -AccessToken $accessToken `
        -Body @{}
}
elseif ($Action -eq 'ClaimDaily') {
    $queued = Invoke-Project21ControlPlane -Method POST `
        -Uri ($baseUrl + '/api/device/daily-drop/claim') `
        -AccessToken $accessToken `
        -Body @{}
}
else {
    $queued = Invoke-Project21ControlPlane -Method POST `
        -Uri ($baseUrl + '/api/device/club/query') `
        -AccessToken $accessToken `
        -Body @{}
}

$commandId = [long]$queued.commandId
if ($commandId -le 0) {
    throw 'The control panel did not return a club command ID.'
}

$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Milliseconds 750
    $status = Invoke-Project21ControlPlane -Method GET `
        -Uri ($baseUrl + '/api/device/commands/' + $commandId) `
        -AccessToken $accessToken
    $command = $status.command
    if ([string]$command.status -eq 'failed') {
        throw ('The server rejected the club request: ' + [string]$command.result)
    }
    if ([string]$command.status -eq 'succeeded') {
        if ([string]::IsNullOrWhiteSpace([string]$command.result)) {
            throw 'The server completed the club request without returning club data.'
        }
        $command.result | ConvertFrom-Json | ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
} while ((Get-Date) -lt $deadline)

throw 'The club request timed out while waiting for the game server.'
