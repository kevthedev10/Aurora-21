[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
. (Join-Path $Root 'Cloudflare-Common.ps1')
. (Join-Path $Root 'Verify-CloudflareDownload.ps1')

$accessConfig = $null
$enrollmentConfig = $null
$controlPlanePath = Join-Path $Root 'control-plane.json'
if (Test-Path -LiteralPath $controlPlanePath -PathType Leaf) {
    $controlPlane = Get-Content -LiteralPath $controlPlanePath -Raw | ConvertFrom-Json
    if ($controlPlane.enabled -eq $true) {
        . (Join-Path $Root 'Player-Access-Common.ps1')
        $accessConfig = & (Join-Path $Root 'Ensure-Player-Access.ps1') -PassThru
    }
}

$settings = Get-CloudflareClientSettings $Root
if ($settings.enabled -ne $true) {
    Write-Host 'Cloudflare mode is disabled in cloudflare-client.json.'
    exit 0
}

function Test-WarpRegistration {
    param([string]$WarpCli)
    if (-not $WarpCli) { return $false }
    try {
        $registration = (& $WarpCli registration show 2>&1) -join "`n"
        return $LASTEXITCODE -eq 0 -and $registration -notmatch 'No registration|not registered'
    }
    catch { return $false }
}

$warpCli = Find-WarpCli
$registered = Test-WarpRegistration $warpCli
if (-not $warpCli -or (-not $registered -and $accessConfig)) {
    if ($accessConfig) {
        $accessToken = Get-Project21AccessToken
        if (-not $accessToken) { throw 'The protected Discord device token is unavailable.' }
        $enrollmentConfig = Invoke-Project21ControlPlane -Method POST `
            -Uri (([string]$controlPlane.baseUrl).TrimEnd('/') + '/api/device/warp-enrollment') `
            -Body @{} -AccessToken $accessToken
        $accessToken = $null
    }
    $downloadUrl = 'https://1111-releases.cloudflareclient.com/windows/Cloudflare_WARP_Release-x64.msi'
    $installer = Join-Path ([IO.Path]::GetTempPath()) ('Cloudflare-One-' + [guid]::NewGuid().ToString('N') + '.msi')
    try {
        Write-Host 'Downloading the current Cloudflare One client from Cloudflare...'
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $installer
        Assert-CloudflareAuthenticodeSignature $installer
        Write-Host 'Windows will request permission to install the verified Cloudflare client.'
        $arguments = @('/i', ('"{0}"' -f $installer), '/qn', '/norestart', ('ORGANIZATION={0}' -f [string]$settings.teamName))
        if ($enrollmentConfig) {
            $arguments += @(
                ('AUTH_CLIENT_ID={0}' -f [string]$enrollmentConfig.warpAuthClientId),
                ('AUTH_CLIENT_SECRET={0}' -f [string]$enrollmentConfig.warpAuthClientSecret),
                'ONBOARDING=false'
            )
        }
        $install = Start-Process -FilePath 'msiexec.exe' -Verb RunAs -Wait -PassThru -ArgumentList $arguments
        if ($install.ExitCode -notin @(0, 3010)) {
            throw "Cloudflare client installation failed with exit code $($install.ExitCode)."
        }
    }
    finally {
        if ($enrollmentConfig) { $enrollmentConfig.warpAuthClientSecret = '' }
        if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }
    }
    $warpCli = Find-WarpCli
    if (-not $warpCli) {
        throw 'Cloudflare installed, but warp-cli.exe was not found. Restart Windows and run the launcher again.'
    }
}

$registered = Test-WarpRegistration $warpCli
if (-not $registered -and -not $accessConfig) {
    Write-Host "Your browser will open so you can join Cloudflare team '$($settings.teamName)'."
    & $warpCli registration new ([string]$settings.teamName)
    if ($LASTEXITCODE -ne 0) { throw 'Cloudflare WARP enrollment did not complete.' }
}
elseif (-not $registered) {
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $registered = Test-WarpRegistration $warpCli
    } while (-not $registered -and (Get-Date) -lt $deadline)
    if (-not $registered) { throw 'Automatic Cloudflare WARP enrollment did not complete.' }
}

if ($enrollmentConfig) { $enrollmentConfig.warpAuthClientSecret = '' }

& $warpCli connect | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cloudflare WARP could not connect.' }
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    $status = (& $warpCli status 2>&1) -join "`n"
    if ($status -match '(?im)^\s*Status:\s*Connected\s*$|^\s*Connected\s*$') { break }
} while ((Get-Date) -lt $deadline)
if ($status -notmatch '(?im)^\s*Status:\s*Connected\s*$|^\s*Connected\s*$') {
    throw 'Cloudflare WARP is not connected.'
}

$failed = @(Test-Project21ServerPorts ([string]$settings.serverAddress) 3000)
if ($failed.Count) {
    throw "LocalFUT is unreachable through Cloudflare on TCP port(s): $($failed -join ', ')."
}

@{ serverAddress = [string]$settings.serverAddress; connectTimeoutSeconds = 8 } |
    ConvertTo-Json -Compress |
    Set-Content -LiteralPath (Join-Path $Root 'remote-server.json') -Encoding UTF8

Write-Host 'Discord and Cloudflare access are ready.' -ForegroundColor Green
Write-Host 'Future launches will reconnect and verify the private route automatically.'
