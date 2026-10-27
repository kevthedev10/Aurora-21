[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ports = '44321,8090,10853,17502,8081,42231,42232'
$tcpRuleName = 'Project 21 Standalone Server (TCP)'
$udpRuleName = 'Project 21 Standalone Server (UDP QoS)'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator.'
}
if (-not (Get-NetFirewallRule -DisplayName $tcpRuleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $tcpRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $ports -Profile Private | Out-Null
}
if (-not (Get-NetFirewallRule -DisplayName $udpRuleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $udpRuleName -Direction Inbound -Action Allow -Protocol UDP -LocalPort 17502 -Profile Private | Out-Null
}
Write-Host 'Private-network firewall rules created.'
