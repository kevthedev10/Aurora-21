[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ServerRoot = $PSScriptRoot
$SettingsPath = Join-Path $ServerRoot 'server-settings.json'
$tcpPorts = '44321,8090,10853,17502,8081,42231,42232'
$tcpRuleName = 'Project 21 via Tailscale (TCP)'
$udpRuleName = 'Project 21 via Tailscale (UDP QoS)'
$relayRuleName = 'Project 21 via Tailscale (UDP Gameplay Relay)'
$relayPortStart = 31000
$relayPortEnd = 31199

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator access is required to create the restricted firewall rules.'
}

$tailscale = Get-Command 'tailscale.exe' -ErrorAction SilentlyContinue
if (-not $tailscale) {
    $installedPath = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
    if (Test-Path -LiteralPath $installedPath -PathType Leaf) {
        $tailscale = Get-Item -LiteralPath $installedPath
    }
}
if (-not $tailscale) {
    throw 'Tailscale is not installed. Install it from https://tailscale.com/download/windows and sign in first.'
}
$tailscalePath = if ($tailscale.Source) { $tailscale.Source } else { $tailscale.FullName }

$tailscaleIp = (& $tailscalePath ip -4 2>$null | Select-Object -First 1).Trim()
$parsedIp = $null
if (-not [Net.IPAddress]::TryParse($tailscaleIp, [ref]$parsedIp)) {
    throw 'No Tailscale IPv4 address was found. Open Tailscale and connect this computer first.'
}
$octets = $parsedIp.GetAddressBytes()
if ($octets.Length -ne 4 -or $octets[0] -ne 100 -or $octets[1] -lt 64 -or $octets[1] -gt 127) {
    throw "Unexpected Tailscale address '$tailscaleIp'; expected an address inside 100.64.0.0/10."
}

@{
    bindAddress = $tailscaleIp
    lsxMode = 'off'
    gameplayRelayEnabled = $true
    gameplayRelayBindAddress = 'auto'
    gameplayRelayAdvertisedAddress = 'auto'
    gameplayRelayPortStart = $relayPortStart
    gameplayRelayPortEnd = $relayPortEnd
    playerIdentityMode = 'peer-ip'
} | ConvertTo-Json | Set-Content -LiteralPath $SettingsPath -Encoding utf8

# Disable only the two optional broad LAN rules created by this package. Rules
# belonging to other applications or administrators are never changed.
foreach ($oldName in @(
    'Project 21 Standalone Server (TCP)',
    'Project 21 Standalone Server (UDP QoS)',
    'Project 21 Standalone Server (UDP Gameplay Relay)'
)) {
    Get-NetFirewallRule -DisplayName $oldName -ErrorAction SilentlyContinue |
        Disable-NetFirewallRule | Out-Null
}

Get-NetFirewallRule -DisplayName $tcpRuleName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName $udpRuleName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName $relayRuleName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule

New-NetFirewallRule -DisplayName $tcpRuleName -Direction Inbound -Action Allow `
    -Protocol TCP -LocalAddress $tailscaleIp -LocalPort $tcpPorts `
    -RemoteAddress '100.64.0.0/10' -Profile Any | Out-Null
New-NetFirewallRule -DisplayName $udpRuleName -Direction Inbound -Action Allow `
    -Protocol UDP -LocalAddress $tailscaleIp -LocalPort 17502 `
    -RemoteAddress '100.64.0.0/10' -Profile Any | Out-Null
New-NetFirewallRule -DisplayName $relayRuleName -Direction Inbound -Action Allow `
    -Protocol UDP -LocalAddress $tailscaleIp -LocalPort "$relayPortStart-$relayPortEnd" `
    -RemoteAddress '100.64.0.0/10' -Profile Any | Out-Null

Write-Host 'Secure remote-access mode configured.' -ForegroundColor Green
Write-Host "Server Tailscale address: $tailscaleIp"
Write-Host "The server will bind only to $tailscaleIp, not its public or LAN interfaces."
Write-Host 'Give this address or the machine MagicDNS name only to authorised players.'
Write-Host 'Now run Start-Server.cmd.'
