[CmdletBinding()]
param(
    [switch]$PassThru,
    [switch]$BanCheckOnly
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'Player-Access-Common.ps1')

function Test-Project21BannedResponse {
    param(
        [Parameter(Mandatory = $true)]$ErrorRecord,
        [switch]$Polling
    )

    $payload = Get-Project21ControlPlaneErrorPayload $ErrorRecord
    if (-not $payload) { return $false }

    $isBanned = if ($Polling) {
        [string]$payload.status -eq 'denied' -and [string]$payload.reason -eq 'banned'
    }
    else {
        [string]$payload.error -eq 'banned'
    }
    if (-not $isBanned) { return $false }

    $reason = if ($Polling) { [string]$payload.banReason } else { [string]$payload.reason }
    Write-Project21BanMarker -Reason $reason
    return $true
}

$settings = Get-Project21ControlPlaneSettings $Root
if ($settings.enabled -ne $true) {
    if ($PassThru) { return $null }
    if ($BanCheckOnly) { exit 0 }
    Write-Host 'Discord control-plane authentication is disabled.'
    exit 0
}

$baseUrl = [string]$settings.baseUrl
$accessToken = Get-Project21AccessToken
$configuration = $null
if ($accessToken) {
    try {
        $configuration = Invoke-Project21ControlPlane -Method GET -Uri ($baseUrl + '/api/device/config') -AccessToken $accessToken
    }
    catch {
        if (Test-Project21BannedResponse -ErrorRecord $_) {
            throw 'Aurora 21 (Alpha v1) access is banned.'
        }

        if ($BanCheckOnly) { exit 0 }

        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403) {
            Remove-Project21AccessToken
            throw 'Aurora 21 (Alpha v1) access has been revoked for this Discord account or device.'
        }
        Remove-Project21AccessToken
        $accessToken = $null
    }
}

if ($BanCheckOnly) { exit 0 }

if (-not $configuration) {
    $deviceName = $env:COMPUTERNAME
    if (-not $deviceName) { $deviceName = 'Windows PC' }
    $flow = Invoke-Project21ControlPlane -Method POST -Uri ($baseUrl + '/api/device/start') -Body @{ deviceName = $deviceName }
    Write-Host ''
    Write-Host ('Discord approval code: ' + [string]$flow.userCode) -ForegroundColor Cyan
    Write-Host 'A browser window is opening. Approve this launcher with Discord once.'
    Start-Process ([string]$flow.verificationUriComplete)

    $deadline = (Get-Date).AddSeconds([int]$flow.expiresIn)
    $poll = $null
    do {
        Start-Sleep -Seconds ([Math]::Max(2, [int]$flow.interval))
        try {
            $poll = Invoke-Project21ControlPlane -Method POST -Uri ($baseUrl + '/api/device/poll') -Body @{
                flowId = [string]$flow.flowId
                pollSecret = [string]$flow.pollSecret
            }
        }
        catch {
            if (Test-Project21BannedResponse -ErrorRecord $_ -Polling) {
                throw 'Discord account access is banned.'
            }
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403) {
                throw 'Discord account access was denied.'
            }
            throw
        }
    } while ($poll -and [string]$poll.status -eq 'pending' -and (Get-Date) -lt $deadline)

    if (-not $poll -or [string]$poll.status -ne 'approved' -or -not [string]$poll.accessToken) {
        throw 'Discord approval did not complete before the code expired.'
    }

    Save-Project21AccessToken -AccessToken ([string]$poll.accessToken)
    $accessToken = [string]$poll.accessToken
    $configuration = Invoke-Project21ControlPlane -Method GET -Uri ($baseUrl + '/api/device/config') -AccessToken $accessToken
}

Write-Host ('Discord access approved for ' + [string]$configuration.username) -ForegroundColor Green
if ($PassThru) { return $configuration }
exit 0
