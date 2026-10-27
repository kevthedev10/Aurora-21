[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'WireGuard-Common.ps1')
. (Join-Path $Root 'Player-Access-Common.ps1')

$settings = Get-Project21WireGuardSettings $Root
if ($settings.enabled -ne $true) { exit 0 }
$access = & (Join-Path $Root 'Ensure-Player-Access.ps1') -PassThru
if (-not $access) { throw 'Discord device approval is required.' }
$wireGuard = Find-WireGuardExecutable
$wg = Find-WgExecutable
$tunnelName = [string]$settings.tunnelName
$configPath = Join-Path (Get-Project21WireGuardDirectory) ($tunnelName + '.conf')
$privateKey = Get-Project21WireGuardPrivateKey $configPath
if (-not $wireGuard -or -not $wg -or -not $privateKey -or -not (Test-Project21WireGuardService $tunnelName)) {
    & (Join-Path $Root 'First-Run-WireGuard.ps1')
    exit $LASTEXITCODE
}

$publicKey = Get-Project21WireGuardPublicKey $wg $privateKey
$provisioning = Get-Project21WireGuardProvisioning $Root $publicKey
$privateKey = $null
if ([string]$provisioning.serverAddress -ne [string]$access.serverAddress) {
    throw 'The WireGuard and launcher server addresses do not match.'
}
$existingConfiguration = [IO.File]::ReadAllText($configPath)
$requiredValues = @(
    ('Address\s*=\s*{0}/32' -f [regex]::Escape([string]$provisioning.clientAddress)),
    ('PublicKey\s*=\s*{0}' -f [regex]::Escape([string]$provisioning.serverPublicKey)),
    ('AllowedIPs\s*=\s*{0}' -f [regex]::Escape([string]$provisioning.allowedIps)),
    ('Endpoint\s*=\s*{0}' -f [regex]::Escape([string]$provisioning.endpoint))
)
if (@($requiredValues | Where-Object { $existingConfiguration -notmatch $_ }).Count) {
    & (Join-Path $Root 'First-Run-WireGuard.ps1')
    exit $LASTEXITCODE
}
$service = Get-Service -Name ('WireGuardTunnel$' + $tunnelName) -ErrorAction Stop
if ($service.Status -ne 'Running') {
    try { Start-Service -InputObject $service -ErrorAction Stop }
    catch { throw 'The WireGuard tunnel is stopped. Run Connect Discord to repair it.' }
}

$relay = Join-Path $Root 'runtime-source\localfut21\server.py'
$python = Get-Command py.exe -ErrorAction SilentlyContinue
if (-not $python) { throw 'Python is required to verify the authenticated game gateway.' }
& $python.Source -3 $relay --check
if ($LASTEXITCODE -ne 0) { throw 'The authenticated LocalFUT gateway rejected this device or is still applying its WireGuard peer.' }
Write-Host 'WireGuard connection and Discord access are ready.' -ForegroundColor Green
