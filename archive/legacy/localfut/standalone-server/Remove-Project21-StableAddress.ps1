[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$address = '10.203.21.1'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Remove-Project21-StableAddress.cmd so Windows can request Administrator access.'
}

$nativeLoopback = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '127.0.0.1' -ErrorAction SilentlyContinue |
    Select-Object -First 1
$entries = if ($nativeLoopback) {
    @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $address -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceIndex -eq $nativeLoopback.InterfaceIndex })
} else {
    @()
}
if ($entries.Count -eq 0) {
    Write-Host "Project 21 stable address $address/32 is not installed. Nothing to remove."
    exit 0
}

foreach ($entry in $entries) {
    Remove-NetIPAddress -InputObject $entry -Confirm:$false -ErrorAction Stop
}
Write-Host "Removed Project 21 stable address $address/32." -ForegroundColor Green
