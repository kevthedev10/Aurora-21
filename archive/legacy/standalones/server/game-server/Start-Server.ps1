[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ServerRoot = $PSScriptRoot
$SettingsPath = Join-Path $ServerRoot 'server-settings.json'
$ServerScript = Join-Path $ServerRoot 'runtime-source\localfut21\server.py'
$StateRoot = Join-Path $ServerRoot 'state'
$ConfigRoot = Join-Path $ServerRoot 'config'

if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
    throw "Missing server settings: $SettingsPath"
}
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$bindAddress = [string]$settings.bindAddress
$lsxMode = [string]$settings.lsxMode
if ($bindAddress -notmatch '^(?:0\.0\.0\.0|(?:\d{1,3}\.){3}\d{1,3})$') {
    throw 'bindAddress must be 0.0.0.0 or an IPv4 address assigned to this server.'
}
if ($lsxMode -notin @('auto', 'off', 'required')) {
    throw 'lsxMode must be auto, off, or required.'
}
if ($bindAddress -ne '0.0.0.0') {
    $assignedAddress = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $bindAddress -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $assignedAddress) {
        throw "Server address $bindAddress is not active. Start the configured WireGuard interface or choose an address assigned to this server."
    }
}

$python = $null
foreach ($candidate in @(
    @{ Path = 'py.exe'; Arguments = @('-3') },
    @{ Path = 'python.exe'; Arguments = @() }
)) {
    try {
        & $candidate.Path @($candidate.Arguments) -c 'import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)' 2>$null
        if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
    } catch { }
}
if (-not $python) {
    throw 'Python 3.10 or newer was not found. Run Install-Prerequisites.cmd first.'
}

New-Item -ItemType Directory -Force -Path $StateRoot,$ConfigRoot | Out-Null
$env:LOCALFUT_RUNTIME_ROOT = $StateRoot
$env:LOCALFUT21_USER_CONFIG_ROOT = $ConfigRoot

$serverBindAddress = $bindAddress
$gatewayProcess = $null
$controlAgentProcess = $null
$multiUserMode = $false
$controlSettingsPath = Join-Path $ServerRoot 'control-plane-host.json'
if (Test-Path -LiteralPath $controlSettingsPath -PathType Leaf) {
    $controlSettings = Get-Content -LiteralPath $controlSettingsPath -Raw | ConvertFrom-Json
    if ($controlSettings.enabled -eq $true) {
        if ($bindAddress -in @('0.0.0.0', '127.0.0.1')) {
            throw 'Authenticated mode requires server-settings.json bindAddress to be this PC private IPv4 address.'
        }
        $gatewaySettingsPath = Join-Path $ServerRoot 'gateway-settings.json'
        @{ bindAddress = $bindAddress; originAddress = '127.0.0.1' } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath $gatewaySettingsPath -Encoding UTF8
        $gatewayScript = Join-Path $ServerRoot 'runtime-source\authenticated_gateway.py'
        $gatewayOutput = Join-Path $StateRoot 'gateway.log'
        $gatewayError = Join-Path $StateRoot 'gateway.error.log'
        $gatewayArguments = @($python.Arguments) + @('-u', ('"{0}"' -f $gatewayScript))
        $gatewayProcess = Start-Process -FilePath $python.Path -ArgumentList $gatewayArguments `
            -WorkingDirectory $ServerRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $gatewayOutput -RedirectStandardError $gatewayError
        Start-Sleep -Seconds 1
        if ($gatewayProcess.HasExited) {
            throw "Authenticated gateway failed to start. See $gatewayError"
        }
        $serverBindAddress = '127.0.0.1'
        $multiUserMode = $true
        Write-Host "Authenticated gateway: $bindAddress -> 127.0.0.1"
        $agentOutput = Join-Path $StateRoot 'control-agent.log'
        $agentError = Join-Path $StateRoot 'control-agent.error.log'
        $controlAgentProcess = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f (Join-Path $ServerRoot 'Host-Control-Agent.ps1'))) `
            -WorkingDirectory $ServerRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $agentOutput -RedirectStandardError $agentError
    }
}
Write-Host "Project 21 standalone server"
Write-Host "Binding LocalFUT game services to $serverBindAddress"
Write-Host "State folder: $StateRoot"
Write-Host 'Press Ctrl+C to stop.'
try {
    if ($multiUserMode) {
        Write-Host 'Per-Discord LocalFUT instances will start automatically when approved players connect.'
        Wait-Process -Id $gatewayProcess.Id
        exit $gatewayProcess.ExitCode
    }
    else {
        & $python.Path @($python.Arguments) -u $ServerScript --host $serverBindAddress --lsx-mode $lsxMode
        exit $LASTEXITCODE
    }
}
finally {
    if ($gatewayProcess -and -not $gatewayProcess.HasExited) {
        Stop-Process -Id $gatewayProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($controlAgentProcess -and -not $controlAgentProcess.HasExited) {
        Stop-Process -Id $controlAgentProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
