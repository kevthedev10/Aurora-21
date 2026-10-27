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

    if (-not (Test-Path -LiteralPath $WgPath -PathType Leaf) -or
        [IO.Path]::GetFileName($WgPath) -ine 'wg.exe') {
        throw "WireGuard key utility was not found at '$WgPath'."
    }

    if ($PrivateKey -notmatch '^[A-Za-z0-9+/]{43}=$') {
        throw 'The WireGuard private device key has an invalid format.'
    }

    # Windows PowerShell 5.1 can add a UTF-8 BOM when piping a string to a
    # native process. Recent wg.exe versions reject those leading bytes. Keep
    # the key out of command-line arguments and off disk, and let cmd.exe make
    # the native-to-native ASCII pipe instead. The Base64 alphabet is safe for
    # echo() and the environment variable is removed as soon as the child starts.
    $commandProcessor = if ($env:ComSpec) {
        $env:ComSpec
    } else {
        Join-Path $env:SystemRoot 'System32\cmd.exe'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $commandProcessor
    $startInfo.Arguments = '/d /s /c "echo(%PROJECT21_WG_PRIVATE_KEY%|wg.exe pubkey"'
    $startInfo.WorkingDirectory = Split-Path -Path $WgPath -Parent
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['PROJECT21_WG_PRIVATE_KEY'] = $PrivateKey

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'WireGuard public-key derivation process could not be started.'
        }

        [void]$startInfo.EnvironmentVariables.Remove('PROJECT21_WG_PRIVATE_KEY')

        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $publicKey = $standardOutput.Trim()
        if ($process.ExitCode -ne 0 -or $publicKey -notmatch '^[A-Za-z0-9+/]{43}=$') {
            $detail = if ([string]::IsNullOrWhiteSpace($standardError)) {
                "exit code $($process.ExitCode)"
            } else {
                $standardError.Trim()
            }
            throw "WireGuard could not derive the device public key: $detail"
        }
        return $publicKey
    }
    finally {
        [void]$startInfo.EnvironmentVariables.Remove('PROJECT21_WG_PRIVATE_KEY')
        $PrivateKey = $null
        $process.Dispose()
    }
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
