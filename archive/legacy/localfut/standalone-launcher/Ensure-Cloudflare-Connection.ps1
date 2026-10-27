[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'Cloudflare-Common.ps1')

$controlEnabled = $false
$controlPath = Join-Path $Root 'control-plane.json'
if (Test-Path -LiteralPath $controlPath -PathType Leaf) {
    $control = Get-Content -LiteralPath $controlPath -Raw | ConvertFrom-Json
    $controlEnabled = $control.enabled -eq $true
    if ($controlEnabled) {
        $null = & (Join-Path $Root 'Ensure-Player-Access.ps1') -PassThru
    }
}

$settings = Get-CloudflareClientSettings $Root
if ($settings.enabled -ne $true) { exit 0 }
$warpCli = Find-WarpCli
if (-not $warpCli) { throw 'Cloudflare One is not installed. Choose Connect Discord in the launcher.' }

& $warpCli connect | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cloudflare One could not connect.' }
$deadline = (Get-Date).AddSeconds(20)
do {
    Start-Sleep -Milliseconds 500
    $status = (& $warpCli status 2>&1) -join "`n"
    if ($status -match '(?im)^\s*Status:\s*Connected\s*$|^\s*Connected\s*$') { break }
} while ((Get-Date) -lt $deadline)
if ($status -notmatch '(?im)^\s*Status:\s*Connected\s*$|^\s*Connected\s*$') {
    throw 'Cloudflare One is not connected.'
}

if ($controlEnabled) {
    $relay = Join-Path $Root 'runtime-source\localfut21\server.py'
    $python = Get-Command py.exe -ErrorAction SilentlyContinue
    if (-not $python) { throw 'Python is required to verify the authenticated game gateway.' }
    & $python.Source -3 $relay --check
    if ($LASTEXITCODE -ne 0) { throw 'The authenticated LocalFUT gateway rejected this device.' }
}
else {
    $failed = @(Test-Project21ServerPorts ([string]$settings.serverAddress) 3000)
    if ($failed.Count) {
        throw "LocalFUT is unreachable through Cloudflare on TCP port(s): $($failed -join ', ')."
    }
}
