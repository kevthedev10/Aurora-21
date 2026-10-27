Set-StrictMode -Version Latest

function Get-Project21WireGuardSettings {
    param([Parameter(Mandatory = $true)][string]$Root)
    $path = Join-Path $Root 'wireguard-client.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing WireGuard settings: $path"
    }
    $settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($settings.enabled -ne $true) { return $settings }
    $name = [string]$settings.tunnelName
    if ($name -notmatch '^[A-Za-z0-9_=+.-]{1,32}$') {
        throw 'wireguard-client.json contains an invalid tunnelName.'
    }
    return $settings
}

function Find-WireGuardExecutable {
    $command = Get-Command 'wireguard.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $path = Join-Path $env:ProgramFiles 'WireGuard\wireguard.exe'
    if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
    return $null
}

function Find-WgExecutable {
    $command = Get-Command 'wg.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $path = Join-Path $env:ProgramFiles 'WireGuard\wg.exe'
    if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
    return $null
}

function Get-Project21WireGuardDirectory {
    $path = Join-Path $env:LOCALAPPDATA 'Project21\WireGuard'
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User)
    foreach ($sid in @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        $security.AddAccessRule($rule)
    }
    [IO.Directory]::SetAccessControl($path, $security)
    return $path
}

function Get-Project21WireGuardPrivateKey {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { return $null }
    $match = [regex]::Match(
        [IO.File]::ReadAllText($ConfigPath),
        '(?im)^\s*PrivateKey\s*=\s*([A-Za-z0-9+/]{43}=)\s*$'
    )
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value
}

function Get-Project21WireGuardPublicKey {
    param(
        [Parameter(Mandatory = $true)][string]$WgPath,
        [Parameter(Mandatory = $true)][string]$PrivateKey
    )
    $publicKey = ($PrivateKey | & $WgPath pubkey 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $publicKey -notmatch '^[A-Za-z0-9+/]{43}=$') {
        throw 'WireGuard could not derive the device public key.'
    }
    return $publicKey
}

function Get-Project21WireGuardProvisioning {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$PublicKey
    )
    . (Join-Path $Root 'Player-Access-Common.ps1')
    $control = Get-Project21ControlPlaneSettings $Root
    if ($control.enabled -ne $true) { throw 'The Discord control plane must be enabled for WireGuard access.' }
    $accessToken = Get-Project21AccessToken
    if (-not $accessToken) { throw 'The protected Discord device token is unavailable.' }
    try {
        return Invoke-Project21ControlPlane -Method POST `
            -Uri (([string]$control.baseUrl).TrimEnd('/') + '/api/device/wireguard') `
            -Body @{ publicKey = $PublicKey } -AccessToken $accessToken
    }
    finally {
        $accessToken = $null
    }
}

function Test-Project21WireGuardService {
    param([Parameter(Mandatory = $true)][string]$TunnelName)
    return $null -ne (Get-Service -Name ('WireGuardTunnel$' + $TunnelName) -ErrorAction SilentlyContinue)
}

function Test-Project21ServerPorts {
    param([string]$Address, [int]$TimeoutMilliseconds = 4000)
    $failed = @()
    foreach ($port in @(44321, 8090, 10853)) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync($Address, $port)
            if (-not $task.Wait($TimeoutMilliseconds) -or -not $client.Connected) { $failed += $port }
        } catch { $failed += $port }
        finally { $client.Dispose() }
    }
    return @($failed)
}
