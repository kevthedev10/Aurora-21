[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$serverDll = Join-Path $repo 'server\src\Aurora21.UltimateTeam.Server\bin\Debug\net10.0\Aurora21-UltimateTeam-Server.dll'
$dataRoot = Join-Path $repo 'runtime-source\ultimateteam21'
$ca = Join-Path $repo 'runtime-source\certs\local-gos2015-ca-cert.pem'
if (-not (Test-Path -LiteralPath $serverDll -PathType Leaf)) { throw "Build the server Debug project first: $serverDll" }
if (-not (Test-Path -LiteralPath $ca -PathType Leaf)) { throw "Existing GOS2015 CA certificate is missing: $ca" }

function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

$httpPort = Get-FreeLoopbackPort
$tlsPort = Get-FreeLoopbackPort
while ($tlsPort -eq $httpPort) { $tlsPort = Get-FreeLoopbackPort }
$root = Join-Path $env:TEMP ('Aurora21-TlsProbe-' + [guid]::NewGuid().ToString('N'))
$configRoot = Join-Path $root 'config'
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
$stdout = Join-Path $root 'server.stdout.log'
$stderr = Join-Path $root 'server.stderr.log'
$previousRuntime = $env:ULTIMATETEAM_RUNTIME_ROOT
$previousConfig = $env:ULTIMATETEAM21_USER_CONFIG_ROOT
$child = $null
$expectedDotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
try {
    $env:ULTIMATETEAM_RUNTIME_ROOT = $root
    $env:ULTIMATETEAM21_USER_CONFIG_ROOT = $configRoot
    $arguments = @('"' + $serverDll + '"', 'serve', '--host', '127.0.0.1', '--lsx-mode', 'off',
        '--http-only-test-port', $httpPort, '--tls-probe-mode', 'fut', '--tls-probe-port', $tlsPort,
        '--data-root', '"' + $dataRoot + '"')
    $child = Start-Process -FilePath $expectedDotnet -ArgumentList $arguments -WorkingDirectory $repo `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru

    $status = $null
    $deadline = (Get-Date).AddSeconds(90)
    do {
        if ($child.HasExited) { throw "Isolated server exited with code $($child.ExitCode): $(Get-Content -LiteralPath $stderr -Raw)" }
        try { $status = Invoke-RestMethod -Uri "http://127.0.0.1:$httpPort/ultimateteam/test/status" -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 200 }
    } while (-not $status -and (Get-Date) -lt $deadline)
    if (-not $status -or $status.testMode -ne $true -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$status.runtimeRoot), [IO.Path]::GetFullPath($root),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Isolated HTTP status did not attest the new temporary runtime.'
    }

    $hostname = 'spring18.gosredirector.ea.com'
    $url = "https://${hostname}:$tlsPort/ultimateteam/auth/bootstrap"
    $resolved = "${hostname}:${tlsPort}:127.0.0.1"
    # This synthetic local CA has no published CRL. Schannel still verifies the CA and hostname.
    $responseText = & curl.exe --silent --show-error --fail --noproxy '*' --tlsv1.2 --ssl-no-revoke `
        --cacert $ca --resolve $resolved $url 2>&1
    if ($LASTEXITCODE -ne 0) { throw "CA-verified HTTPS request failed: $responseText" }
    $profile = ($responseText -join "`n") | ConvertFrom-Json -ErrorAction Stop
    if ($profile.version -ne 1 -or $profile.serverImplementation -cne 'csharp') {
        throw 'HTTPS bootstrap did not return the expected server profile.'
    }
    $log = Get-Content -LiteralPath (Join-Path $root 'logs\ultimateteam.log') -Raw -ErrorAction Stop
    if ($log -notmatch 'FUT TLS PROBE request protocol=Tls12.*method=GET path=/ultimateteam/auth/bootstrap') {
        throw 'The server did not log a TLS 1.2 request on the probe listener.'
    }
    Write-Output "PASS: CA-verified TLS 1.2 request reached the isolated FUT probe port $tlsPort; temporary runtime $root"
}
finally {
    if ($child -and -not $child.HasExited) {
        $owned = Get-CimInstance Win32_Process -Filter "ProcessId=$($child.Id)" -ErrorAction SilentlyContinue
        if ($owned -and [string]::Equals([IO.Path]::GetFullPath($owned.ExecutablePath), [IO.Path]::GetFullPath($expectedDotnet),
                [StringComparison]::OrdinalIgnoreCase) -and
            $owned.CommandLine.IndexOf($serverDll, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Stop-Process -Id $child.Id -Force -ErrorAction Stop
            Wait-Process -Id $child.Id -Timeout 10 -ErrorAction SilentlyContinue
        } else {
            Write-Warning "Isolated server PID $($child.Id) was not stopped because its command line could not be verified."
        }
    }
    if ($child) { $child.Dispose() }
    if (Test-Path -LiteralPath (Join-Path $root 'postgres\data-18') -PathType Container) {
        & $expectedDotnet $serverDll postgres stop --data-root $dataRoot | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Check the isolated PostgreSQL cluster under $root; stop returned $LASTEXITCODE." }
    }
    if ($null -eq $previousRuntime) { Remove-Item Env:ULTIMATETEAM_RUNTIME_ROOT -ErrorAction SilentlyContinue }
    else { $env:ULTIMATETEAM_RUNTIME_ROOT = $previousRuntime }
    if ($null -eq $previousConfig) { Remove-Item Env:ULTIMATETEAM21_USER_CONFIG_ROOT -ErrorAction SilentlyContinue }
    else { $env:ULTIMATETEAM21_USER_CONFIG_ROOT = $previousConfig }
}
