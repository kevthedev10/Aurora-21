[CmdletBinding()]
param([switch]$LauncherOnly, [switch]$IfNeeded, [switch]$AuthBridgeOnly, [switch]$MathBridgeOnly)

$ErrorActionPreference = 'Stop'
if (@($LauncherOnly, $AuthBridgeOnly, $MathBridgeOnly | Where-Object { $_ }).Count -gt 1) {
    throw '-LauncherOnly, -AuthBridgeOnly and -MathBridgeOnly cannot be combined.'
}
$ProjectRoot = $PSScriptRoot
$LauncherProject = Join-Path $ProjectRoot 'UltimateTeamLauncher.csproj'
$LauncherOutput = Join-Path $ProjectRoot 'Aurora21-UltimateTeam-Launcher.exe'
$PublishedLauncher = Join-Path $ProjectRoot 'bin\Release\net10.0-windows\Aurora21-UltimateTeam-Launcher.exe'
$ServerRoot = Join-Path $ProjectRoot 'server'
$ServerProject = Join-Path $ServerRoot 'src\Aurora21.UltimateTeam.Server\Aurora21.UltimateTeam.Server.csproj'
$ServerPublishDirectory = Join-Path $ServerRoot 'publish\win-x64'
$ServerOutput = Join-Path $ProjectRoot 'Aurora21-UltimateTeam-Server.exe'
$AuthSource = Join-Path $ProjectRoot 'FIFA21-LocalAuthBridge.cs'
$AuthOutput = Join-Path $ProjectRoot 'Aurora21-LocalAuthBridge.exe'
# The hosted client pack's maths bridge (client-pack\Start-Aurora21.ps1 runs it in FIFA's process), committed prebuilt.
$MathSource = Join-Path $ProjectRoot 'FIFA21-LocalMathBridge.cs'
$MathOutput = Join-Path $ProjectRoot 'Aurora21-MathBridge.exe'

if (-not (Test-Path -LiteralPath $AuthSource -PathType Leaf)) { throw "Launcher source was not found: $AuthSource" }
if (-not (Test-Path -LiteralPath $MathSource -PathType Leaf)) { throw "Maths bridge source was not found: $MathSource" }
if (-not $LauncherOnly -and -not $MathBridgeOnly -and (Get-Process -Name 'Aurora21-LocalAuthBridge' -ErrorAction SilentlyContinue)) {
    throw 'Aurora21-LocalAuthBridge is running. Stop UltimateTeam before rebuilding the auth bridge.'
}

if (-not $AuthBridgeOnly -and -not $MathBridgeOnly) {
$launcherSources = @(
    $LauncherProject,
    (Join-Path $ProjectRoot 'UltimateTeamLauncher.App.xaml'),
    (Join-Path $ProjectRoot 'UltimateTeamLauncher.App.xaml.cs'),
    (Join-Path $ProjectRoot 'UltimateTeamLauncher.MainWindow.xaml'),
    (Join-Path $ProjectRoot 'UltimateTeamLauncher.MainWindow.xaml.cs'),
    (Join-Path $ProjectRoot 'UltimateTeamLauncher.RuntimeCompatibility.cs'),
    (Join-Path $ProjectRoot 'assets\FIFASetup.png')
)
foreach ($source in $launcherSources + @($ServerProject)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Launcher source was not found: $source" }
}

if (Get-Process -Name 'Aurora21-UltimateTeam-Launcher' -ErrorAction SilentlyContinue) {
    throw 'Aurora21-UltimateTeam-Launcher is running. Close the launcher before rebuilding it.'
}
if (-not $LauncherOnly -and (Get-Process -Name 'Aurora21-UltimateTeam-Server' -ErrorAction SilentlyContinue)) {
    throw 'Aurora21-UltimateTeam-Server is running. Stop UltimateTeam before rebuilding the server.'
}
$launcherNewest = ($launcherSources | Get-Item | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
& (Join-Path $ProjectRoot 'Build-LeaderboardClientTool.ps1') -IfNeeded:$IfNeeded
$launcherStale = -not (Test-Path -LiteralPath $LauncherOutput -PathType Leaf) -or
    (Get-Item -LiteralPath $LauncherOutput).LastWriteTimeUtc -lt $launcherNewest
if (-not $IfNeeded -or $launcherStale) {
    & dotnet build $LauncherProject -c Release --nologo
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $PublishedLauncher -PathType Leaf)) {
        throw "WPF launcher compilation failed with exit code $LASTEXITCODE."
    }
    foreach ($artifact in @(
        'Aurora21-UltimateTeam-Launcher.exe',
        'Aurora21-UltimateTeam-Launcher.dll',
        'Aurora21-UltimateTeam-Launcher.deps.json',
        'Aurora21-UltimateTeam-Launcher.runtimeconfig.json'
    )) {
        $publishedArtifact = Join-Path (Split-Path -Parent $PublishedLauncher) $artifact
        if (-not (Test-Path -LiteralPath $publishedArtifact -PathType Leaf)) {
            throw "WPF launcher build did not produce $artifact."
        }
        Copy-Item -LiteralPath $publishedArtifact -Destination (Join-Path $ProjectRoot $artifact) -Force
    }
    Write-Host "Built: $LauncherOutput"
}
if ($LauncherOnly) { return }

# The UltimateTeam server: a self-contained single-file executable, so players need no .NET runtime.
$serverSources = @(Get-ChildItem -LiteralPath (Join-Path $ServerRoot 'src') -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$serverSources += @(Get-Item -LiteralPath (Join-Path $ServerRoot 'Directory.Build.props'))
# The Core assembly embeds these setup artifacts from outside src/. Changes must
# invalidate -IfNeeded just like changes to embedded response templates.
$setupRoot = Join-Path $ServerRoot 'database-setup'
$setupManifestPath = Join-Path $setupRoot 'manifest.json'
$setupManifest = Get-Content -LiteralPath $setupManifestPath -Raw | ConvertFrom-Json
$serverSources += @(Get-Item -LiteralPath $setupManifestPath)
foreach ($setupFile in @($setupManifest.executionOrder)) {
    $serverSources += @(Get-Item -LiteralPath (Join-Path $setupRoot $setupFile))
}
# Catalogue migrations are embedded separately from the base setup manifest.
# A SQL-only fix must refresh the shipped server on the next RUN-FIFA21 invocation.
$serverSources += @(Get-ChildItem -LiteralPath (Join-Path $setupRoot 'migrations') -Filter '*.sql' -File)
$serverNewest = ($serverSources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
$serverStale = -not (Test-Path -LiteralPath $ServerOutput -PathType Leaf) -or
    (Get-Item -LiteralPath $ServerOutput).LastWriteTimeUtc -lt $serverNewest
if (-not $IfNeeded -or $serverStale) {
    & dotnet publish $ServerProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $ServerPublishDirectory --nologo
    $publishedServer = Join-Path $ServerPublishDirectory 'Aurora21-UltimateTeam-Server.exe'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $publishedServer -PathType Leaf)) {
        throw "UltimateTeam server publish failed with exit code $LASTEXITCODE."
    }
    Copy-Item -LiteralPath $publishedServer -Destination $ServerOutput -Force
    Write-Host "Built: $ServerOutput"
}

# A clean checkout contains only FIFA's original trust-reference moduli. Local CA/leaf
# keys, certificates and replacement moduli are generated per installation, not shipped.
# The generator refuses a partial or existing set, so never repair one with --force here.
$certificateRoot = Join-Path $ProjectRoot 'runtime-source'
$generatedCertificates = @(
    'local-gos2015-modulus.bin', 'local-gs2019-modulus.bin',
    'certs\local-gos2015-ca-key.pem', 'certs\local-gos2015-ca-cert.pem',
    'certs\local-gs2019-ca-key.pem', 'certs\local-gs2019-ca-cert.pem',
    'certs\spring18-key.pem', 'certs\spring18-cert.pem', 'certs\spring18-chain.pem',
    'certs\spring18-gs2019-key.pem', 'certs\spring18-gs2019-cert.pem', 'certs\spring18-gs2019-chain.pem'
)
$missingCertificates = @($generatedCertificates | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $certificateRoot $_) -PathType Leaf)
})
if ($missingCertificates.Count -gt 0) {
    $existingCertificates = @($generatedCertificates | Where-Object {
        Test-Path -LiteralPath (Join-Path $certificateRoot $_) -PathType Leaf
    })
    if ($existingCertificates.Count -gt 0) {
        throw "Local certificates are incomplete under $certificateRoot. Preserve the existing files and repair this installation explicitly; the builder will not rotate its CA. Missing: $($missingCertificates -join ', ')"
    }
    $dataRoot = Join-Path $certificateRoot 'ultimateteam21'
    $previousCertificateRoot = $env:ULTIMATETEAM_CERTIFICATE_ROOT
    try {
        # An inherited hosted-server override must not redirect a local build's keys.
        $env:ULTIMATETEAM_CERTIFICATE_ROOT = $certificateRoot
        & $ServerOutput generate-certs --data-root $dataRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create this installation's local certificates (exit $LASTEXITCODE)."
        }
    } finally {
        $env:ULTIMATETEAM_CERTIFICATE_ROOT = $previousCertificateRoot
    }
    Write-Host "Generated local certificates: $certificateRoot"
}

# The club database: the server's pinned portable PostgreSQL build (checksum-verified download
# into postgres\win-x64, not committed). The server skips the download when it is current.
$postgresMarker = Join-Path $ProjectRoot 'postgres\win-x64\VERSION'
if (-not $IfNeeded -or $serverStale -or -not (Test-Path -LiteralPath $postgresMarker -PathType Leaf)) {
    & $ServerOutput postgres install
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL install failed with exit code $LASTEXITCODE."
    }
}
}

$authStale = -not (Test-Path -LiteralPath $AuthOutput -PathType Leaf) -or
    (Get-Item -LiteralPath $AuthOutput).LastWriteTimeUtc -lt (Get-Item -LiteralPath $AuthSource).LastWriteTimeUtc
$mathStale = -not (Test-Path -LiteralPath $MathOutput -PathType Leaf) -or
    (Get-Item -LiteralPath $MathOutput).LastWriteTimeUtc -lt (Get-Item -LiteralPath $MathSource).LastWriteTimeUtc
$buildAuth = -not $MathBridgeOnly -and (-not $IfNeeded -or $authStale)
$buildMath = -not $AuthBridgeOnly -and (-not $IfNeeded -or $mathStale)
if (-not $buildAuth -and -not $buildMath) { return }

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = @($compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)[0]
if (-not $compiler) { throw 'The .NET Framework C# compiler was not found.' }

if ($buildAuth) {
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /reference:System.Web.Extensions.dll /reference:System.dll /reference:System.Core.dll ("/out:{0}" -f $AuthOutput) $AuthSource
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $AuthOutput -PathType Leaf)) {
        throw "Local auth bridge compilation failed with exit code $LASTEXITCODE."
    }
    Write-Host "Built: $AuthOutput"
}
if ($buildMath) {
    # A hosted launch runs it for about 20 s after FIFA starts; its exe cannot be replaced meanwhile.
    if (Get-Process -Name 'Aurora21-MathBridge' -ErrorAction SilentlyContinue) {
        throw 'Aurora21-MathBridge is running. Wait for it to finish (or close FIFA) before rebuilding the maths bridge.'
    }
    # The same compiler and flags as the auth bridge.
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /reference:System.Web.Extensions.dll /reference:System.dll /reference:System.Core.dll ("/out:{0}" -f $MathOutput) $MathSource
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $MathOutput -PathType Leaf)) {
        throw "Maths bridge compilation failed with exit code $LASTEXITCODE."
    }
    Write-Host "Built: $MathOutput"
}
