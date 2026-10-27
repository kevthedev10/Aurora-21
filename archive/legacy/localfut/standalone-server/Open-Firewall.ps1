[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'server-settings.json') -Raw | ConvertFrom-Json
$relayEnabled = if ($null -eq $settings.gameplayRelayEnabled) { $true } else { $settings.gameplayRelayEnabled -eq $true }
$relayPortStart = if ($null -eq $settings.gameplayRelayPortStart) { 31000 } else { [int]$settings.gameplayRelayPortStart }
$relayPortEnd = if ($null -eq $settings.gameplayRelayPortEnd) { 31199 } else { [int]$settings.gameplayRelayPortEnd }
$ports = '44321,8090,10853,17502,8081,42231,42232'
$tcpRuleName = 'Project 21 Standalone Server (TCP)'
$udpRuleName = 'Project 21 Standalone Server (UDP QoS)'
$relayRuleName = 'Project 21 Standalone Server (UDP Gameplay Relay)'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator.'
}
if ($relayPortStart -lt 1024 -or $relayPortEnd -gt 65535 -or $relayPortStart -ge $relayPortEnd) {
    throw 'Gameplay relay ports must define an ordered range inside 1024-65535.'
}
if (-not (Get-NetFirewallRule -DisplayName $tcpRuleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $tcpRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $ports -Profile Private | Out-Null
}
if (-not (Get-NetFirewallRule -DisplayName $udpRuleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $udpRuleName -Direction Inbound -Action Allow -Protocol UDP -LocalPort 17502 -Profile Private | Out-Null
}
Get-NetFirewallRule -DisplayName $relayRuleName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
if ($relayEnabled) {
    New-NetFirewallRule -DisplayName $relayRuleName -Direction Inbound -Action Allow `
        -Protocol UDP -LocalPort "$relayPortStart-$relayPortEnd" -Profile Private | Out-Null
}
Write-Host 'Private-network firewall rules created.'
