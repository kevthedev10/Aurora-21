Set-StrictMode -Version Latest

function Get-CloudflareClientSettings {
    param([string]$Root)
    $path = Join-Path $Root 'cloudflare-client.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing Cloudflare client settings: $path"
    }
    $settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $teamName = [string]$settings.teamName
    $serverAddress = [string]$settings.serverAddress
    if ($settings.enabled -ne $true) { return $settings }
    if ($teamName -eq 'CHANGE_ME_TO_CLOUDFLARE_TEAM' -or
        $teamName -notmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$') {
        throw 'Cloudflare teamName is not configured. The host must run Prepare-Cloudflare-Host.cmd before sharing this launcher.'
    }
    $parsed = $null
    if ($serverAddress -eq 'CHANGE_ME_TO_PRIVATE_SERVER_IP' -or
        -not [Net.IPAddress]::TryParse($serverAddress, [ref]$parsed) -or
        $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'Cloudflare serverAddress must be the private IPv4 /32 routed through the tunnel.'
    }
    return $settings
}

function Find-WarpCli {
    $command = Get-Command 'warp-cli.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($path in @(
        (Join-Path $env:ProgramFiles 'Cloudflare\Cloudflare WARP\warp-cli.exe'),
        (Join-Path $env:ProgramFiles 'Cloudflare\Cloudflare One\warp-cli.exe')
    )) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
    }
    return $null
}

function Test-Project21ServerPorts {
    param([string]$Address, [int]$TimeoutMilliseconds = 4000)
    $failed = @()
    foreach ($port in @(44321, 8090, 10853)) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync($Address, $port)
            if (-not $task.Wait($TimeoutMilliseconds) -or -not $client.Connected) {
                $failed += $port
            }
        } catch {
            $failed += $port
        } finally {
            $client.Dispose()
        }
    }
    return @($failed)
}
