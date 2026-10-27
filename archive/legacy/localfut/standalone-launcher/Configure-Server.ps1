[CmdletBinding()]
param([string]$ServerAddress = '')

$ErrorActionPreference = 'Stop'
$SettingsPath = Join-Path $PSScriptRoot 'remote-server.json'
if ([string]::IsNullOrWhiteSpace($ServerAddress)) {
    $ServerAddress = Read-Host 'Enter the standalone server LAN/VPN IP address or DNS name'
}
$ServerAddress = $ServerAddress.Trim()
if ([string]::IsNullOrWhiteSpace($ServerAddress) -or $ServerAddress -match '[\s/:\\]') {
    throw 'Enter only an IP address or DNS name, without http://, a port, slash, or spaces.'
}
try {
    $resolved = @([System.Net.Dns]::GetHostAddresses($ServerAddress))
} catch {
    throw "Cannot resolve '$ServerAddress'. Check the address and network connection."
}
if (-not $resolved) {
    throw "'$ServerAddress' did not resolve to an IP address."
}

$settings = [ordered]@{
    serverAddress = $ServerAddress
    connectTimeoutSeconds = 5
}
$settings | ConvertTo-Json | Set-Content -LiteralPath $SettingsPath -Encoding utf8
Write-Host "Saved standalone server: $ServerAddress"
Write-Host "Settings file: $SettingsPath"
foreach ($port in @(44321, 8090, 10853)) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync($ServerAddress, $port)
        if (-not $task.Wait(3000) -or -not $client.Connected) {
            Write-Warning "Server is not currently reachable on TCP $port."
        } else {
            Write-Host "TCP $port reachable" -ForegroundColor Green
        }
    } catch {
        Write-Warning "Server is not currently reachable on TCP $port."
    } finally {
        $client.Dispose()
    }
}
