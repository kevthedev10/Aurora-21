[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'WireGuard-Common.ps1')
. (Join-Path $Root 'Verify-WireGuardDownload.ps1')
. (Join-Path $Root 'Player-Access-Common.ps1')

$settings = Get-Project21WireGuardSettings $Root
if ($settings.enabled -ne $true) { exit 0 }
$access = & (Join-Path $Root 'Ensure-Player-Access.ps1') -PassThru
if (-not $access) { throw 'Discord device approval is required.' }

$wireGuard = Find-WireGuardExecutable
if (-not $wireGuard) {
    $downloadUrl = 'https://download.wireguard.com/windows-client/wireguard-installer.exe'
    $installer = Join-Path ([IO.Path]::GetTempPath()) ('WireGuard-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        Write-Host 'Downloading the official WireGuard client...'
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $installer
        Assert-WireGuardAuthenticodeSignature $installer
        Write-Host 'Windows will request permission to install the verified WireGuard client.'
        $install = Start-Process -FilePath $installer -Verb RunAs -Wait -PassThru -ArgumentList @('/install', '/quiet')
        if ($install.ExitCode -ne 0) { throw "WireGuard installation failed with exit code $($install.ExitCode)." }
    }
    finally {
        if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }
    }
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $wireGuard = Find-WireGuardExecutable
    } while (-not $wireGuard -and (Get-Date) -lt $deadline)
    if (-not $wireGuard) { throw 'WireGuard installed but wireguard.exe was not found. Restart Windows and retry.' }
}

$wg = Find-WgExecutable
if (-not $wg) { throw 'WireGuard is installed but wg.exe was not found.' }
$tunnelName = [string]$settings.tunnelName
$configDirectory = Get-Project21WireGuardDirectory
$configPath = Join-Path $configDirectory ($tunnelName + '.conf')
$privateKey = Get-Project21WireGuardPrivateKey $configPath
if (-not $privateKey) {
    $privateKey = (& $wg genkey 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $privateKey -notmatch '^[A-Za-z0-9+/]{43}=$') {
        throw 'WireGuard could not generate a private device key.'
    }
}
$publicKey = Get-Project21WireGuardPublicKey $wg $privateKey
$provisioning = Get-Project21WireGuardProvisioning $Root $publicKey

foreach ($property in @('clientAddress', 'serverAddress', 'serverPublicKey', 'endpoint', 'allowedIps')) {
    if ([string]::IsNullOrWhiteSpace([string]$provisioning.$property)) {
        throw "The control panel did not return WireGuard $property."
    }
}
$configuration = @"
[Interface]
PrivateKey = $privateKey
Address = $([string]$provisioning.clientAddress)/32

[Peer]
PublicKey = $([string]$provisioning.serverPublicKey)
AllowedIPs = $([string]$provisioning.allowedIps)
Endpoint = $([string]$provisioning.endpoint)
PersistentKeepalive = 25
"@

$serviceExists = Test-Project21WireGuardService $tunnelName
$existing = if (Test-Path -LiteralPath $configPath) { [IO.File]::ReadAllText($configPath) } else { '' }
if (-not $serviceExists -or $existing.Trim() -ne $configuration.Trim()) {
    if ($serviceExists) {
        $remove = Start-Process -FilePath $wireGuard -Verb RunAs -Wait -PassThru `
            -ArgumentList @('/uninstalltunnelservice', $tunnelName)
        if ($remove.ExitCode -ne 0) { throw "Could not replace the existing WireGuard tunnel (exit $($remove.ExitCode))." }
    }
    [IO.File]::WriteAllText($configPath, $configuration.Trim() + "`r`n", [Text.UTF8Encoding]::new($false))
    $installTunnel = Start-Process -FilePath $wireGuard -Verb RunAs -Wait -PassThru `
        -ArgumentList @('/installtunnelservice', ('"{0}"' -f $configPath))
    if ($installTunnel.ExitCode -ne 0) { throw "WireGuard tunnel installation failed with exit code $($installTunnel.ExitCode)." }
}

@{ serverAddress = [string]$provisioning.serverAddress; connectTimeoutSeconds = 8 } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $Root 'remote-server.json') -Encoding UTF8

$relay = Join-Path $Root 'runtime-source\localfut21\server.py'
$python = Get-Command py.exe -ErrorAction SilentlyContinue
if (-not $python) { throw 'Python is required to verify the authenticated game gateway.' }
$deadline = (Get-Date).AddSeconds(35)
do {
    Start-Sleep -Seconds 2
    & $python.Source -3 $relay --check
    if ($LASTEXITCODE -eq 0) { break }
} while ((Get-Date) -lt $deadline)
if ($LASTEXITCODE -ne 0) {
    throw 'WireGuard connected, but the authenticated LocalFUT gateway is not ready. Check the VPS services.'
}

$privateKey = $null
Write-Host ('Discord identity verified: ' + [string]$access.username) -ForegroundColor Green
Write-Host 'WireGuard is installed and connected. Future launches reconnect automatically.' -ForegroundColor Green
