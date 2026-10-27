[CmdletBinding()]
param(
    [string]$TeamName = '',
    [string]$PrivateAddress = '10.203.21.1'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$HostSettingsPath = Join-Path $Root 'cloudflare-host.json'
$ServerSettingsPath = Join-Path $Root 'server-settings.json'
$CloudflaredPath = Join-Path $Root 'tools\cloudflared.exe'
. (Join-Path $Root 'Verify-CloudflareDownload.ps1')

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Prepare-Cloudflare-Host.cmd so Windows can install the tunnel service.'
}

$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$cloudflaredAsset = 'cloudflared-windows-amd64.exe'
if ($osArchitecture -eq 'Arm64') {
    Write-Host 'Windows ARM64 detected. Cloudflare currently publishes cloudflared for Windows AMD64; Windows 11 x64 emulation will be used.' -ForegroundColor Cyan
} elseif ($osArchitecture -ne 'X64') {
    throw "Unsupported Windows architecture '$osArchitecture'. Project 21 supports Windows x64 and Windows 11 ARM64 with x64 emulation."
}

if ([string]::IsNullOrWhiteSpace($TeamName)) {
    $TeamName = Read-Host 'Cloudflare Zero Trust team name (the part before .cloudflareaccess.com)'
}
$TeamName = $TeamName.Trim().ToLowerInvariant()
if ($TeamName -notmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$') {
    throw 'Team name may contain only lowercase letters, numbers, and hyphens. Do not include .cloudflareaccess.com.'
}

$candidateAddresses = @(Get-NetIPAddress -AddressFamily IPv4 -Type Unicast -ErrorAction SilentlyContinue |
    Where-Object {
        $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and
        $_.InterfaceAlias -notmatch 'Tailscale|WARP|Loopback'
    } | Select-Object -ExpandProperty IPAddress -Unique)
if ([string]::IsNullOrWhiteSpace($PrivateAddress)) {
    Write-Host "Detected IPv4 addresses: $($candidateAddresses -join ', ')"
    $PrivateAddress = Read-Host 'Private IPv4 address of this server to route through Cloudflare'
}
$PrivateAddress = $PrivateAddress.Trim()
$parsed = $null
if (-not [Net.IPAddress]::TryParse($PrivateAddress, [ref]$parsed) -or
    $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'PrivateAddress must be an IPv4 address assigned to this computer.'
}
if ($candidateAddresses -notcontains $PrivateAddress -and $PrivateAddress -ne '10.203.21.1') {
    throw "'$PrivateAddress' is not currently assigned to a non-VPN network interface on this computer."
}
$bytes = $parsed.GetAddressBytes()
$isPrivate = $bytes[0] -eq 10 -or
    ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
    ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
if (-not $isPrivate) { throw 'Use an RFC1918 private address; never route the host public address.' }

if ($PrivateAddress -eq '10.203.21.1') {
    $nativeLoopback = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '127.0.0.1' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $loopback = if ($nativeLoopback) {
        Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex $nativeLoopback.InterfaceIndex -ErrorAction SilentlyContinue
    }
    if (-not $loopback) {
        throw 'Windows loopback interface was not found. Repair the Windows TCP/IP stack and retry.'
    }

    $existingAddress = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $PrivateAddress -ErrorAction SilentlyContinue)
    $wrongInterface = @($existingAddress | Where-Object { $_.InterfaceIndex -ne $loopback.InterfaceIndex })
    if ($wrongInterface.Count -gt 0) {
        throw "$PrivateAddress is already assigned to another interface. Remove that conflicting address or choose another private /32."
    }
    if ($existingAddress.Count -eq 0) {
        Write-Host "Creating stable Project 21 address $PrivateAddress/32 on $($loopback.InterfaceAlias)..." -ForegroundColor Cyan
        New-NetIPAddress -InterfaceIndex $loopback.InterfaceIndex -AddressFamily IPv4 `
            -IPAddress $PrivateAddress -PrefixLength 32 -Type Unicast -SkipAsSource $true `
            -ErrorAction Stop | Out-Null
    } else {
        Set-NetIPAddress -InterfaceIndex $loopback.InterfaceIndex -IPAddress $PrivateAddress `
            -PrefixLength 32 -SkipAsSource $true -ErrorAction Stop
    }

    $verifiedAddress = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $PrivateAddress -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceIndex -eq $loopback.InterfaceIndex } |
        Select-Object -First 1
    if (-not $verifiedAddress) {
        throw "Windows did not activate the stable Project 21 address $PrivateAddress/32."
    }
    Write-Host "Stable Project 21 address ready: $PrivateAddress/32 (no router reservation required)." -ForegroundColor Green
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $CloudflaredPath) | Out-Null
if (-not (Test-Path -LiteralPath $CloudflaredPath -PathType Leaf)) {
    $downloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/$cloudflaredAsset"
    $temporary = $CloudflaredPath + '.download'
    try {
        Write-Host 'Downloading current cloudflared from the official Cloudflare GitHub release...'
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $temporary
        Assert-CloudflareAuthenticodeSignature $temporary
        Move-Item -LiteralPath $temporary -Destination $CloudflaredPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Assert-CloudflareAuthenticodeSignature $CloudflaredPath
try {
    $cloudflaredVersion = (& $CloudflaredPath --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($cloudflaredVersion)) {
        throw "cloudflared exited with code $LASTEXITCODE."
    }
    Write-Host "Verified $cloudflaredVersion on Windows $osArchitecture." -ForegroundColor Green
} catch {
    if ($osArchitecture -eq 'Arm64') {
        throw 'The signed Cloudflare AMD64 connector could not run. Enable Windows 11 x64 emulation and retry.'
    }
    throw
}

@{
    bindAddress = $PrivateAddress
    lsxMode = 'off'
    gameplayRelayEnabled = $true
    gameplayRelayBindAddress = 'auto'
    gameplayRelayAdvertisedAddress = 'auto'
    gameplayRelayPortStart = 31000
    gameplayRelayPortEnd = 31199
    playerIdentityMode = 'peer-ip'
} |
    ConvertTo-Json | Set-Content -LiteralPath $ServerSettingsPath -Encoding utf8
@{
    teamName = $TeamName
    privateAddress = $PrivateAddress
    addressMode = if ($PrivateAddress -eq '10.203.21.1') { 'stable-loopback' } else { 'existing-interface' }
} |
    ConvertTo-Json | Set-Content -LiteralPath $HostSettingsPath -Encoding utf8

foreach ($oldName in @(
    'Project 21 Standalone Server (TCP)',
    'Project 21 Standalone Server (UDP QoS)',
    'Project 21 Standalone Server (UDP Gameplay Relay)',
    'Project 21 via Tailscale (TCP)',
    'Project 21 via Tailscale (UDP QoS)',
    'Project 21 via Tailscale (UDP Gameplay Relay)'
)) {
    Get-NetFirewallRule -DisplayName $oldName -ErrorAction SilentlyContinue |
        Disable-NetFirewallRule | Out-Null
}

$launcherRoot = Join-Path (Split-Path -Parent $Root) 'standalone-launcher'
if (Test-Path -LiteralPath $launcherRoot -PathType Container) {
    @{ enabled = $true; teamName = $TeamName; serverAddress = $PrivateAddress } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $launcherRoot 'cloudflare-client.json') -Encoding utf8
    @{ serverAddress = $PrivateAddress; connectTimeoutSeconds = 8 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $launcherRoot 'remote-server.json') -Encoding utf8
    Write-Host 'Configured the sibling standalone-launcher folder for distribution.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Paste the token from your remotely-managed Cloudflare Tunnel.'
Write-Host 'The token is used only by cloudflared service installation and is not written to these JSON files.'
$secureToken = Read-Host 'Tunnel token' -AsSecureString
$tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'Tunnel token cannot be empty.' }
    & $CloudflaredPath service install $token
    if ($LASTEXITCODE -ne 0) { throw "cloudflared service installation failed with exit code $LASTEXITCODE." }
} finally {
    if ($tokenPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer) }
    $token = $null
}

Write-Host ''
Write-Host 'Host connector installed.' -ForegroundColor Green
Write-Host "In Cloudflare, route $PrivateAddress/32 to this tunnel."
Write-Host 'Enable Gateway TCP and UDP proxying, configure WARP split tunnels for that /32,'
Write-Host 'and allow only the LocalFUT ports (including UDP 31000-31199) plus a lower-priority catch-all Block policy.'
Write-Host 'Discord identity, approval and bans are enforced by the authenticated LocalFUT gateway.'
Write-Host 'Then run Start-Server.cmd and test with one authorised player.'
