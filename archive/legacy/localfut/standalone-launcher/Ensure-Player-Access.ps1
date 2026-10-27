[CmdletBinding()]
param([switch]$PassThru)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'Player-Access-Common.ps1')

$settings = Get-Project21ControlPlaneSettings $Root
if ($settings.enabled -ne $true) {
    if ($PassThru) { return $null }
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
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403) {
            Remove-Project21AccessToken
            throw 'Project 21 access has been revoked for this Discord account or device.'
        }
        Remove-Project21AccessToken
        $accessToken = $null
    }
}

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
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403) {
                throw 'Discord account access was denied or banned.'
            }
        }
        if ($poll -and [string]$poll.status -eq 'approved') { break }
    } while ((Get-Date) -lt $deadline)

    if (-not $poll -or [string]$poll.status -ne 'approved' -or -not [string]$poll.accessToken) {
        throw 'Discord approval timed out. Return to the launcher and try again.'
    }
    $accessToken = [string]$poll.accessToken
    Save-Project21AccessToken $accessToken
    $configuration = Invoke-Project21ControlPlane -Method GET -Uri ($baseUrl + '/api/device/config') -AccessToken $accessToken
}

Write-Host ('Discord identity verified: ' + [string]$configuration.username) -ForegroundColor Green
if ($PassThru) { return $configuration }
exit 0
