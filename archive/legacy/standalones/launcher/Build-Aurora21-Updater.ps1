[CmdletBinding()]
param(
    [string]$Version = ('alpha-v1.' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')),
    [string]$PublicBaseUrl = 'https://project21.duckdns.org/downloads/launcher'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'Aurora21Updater\Aurora21Updater.csproj'
$launcherBuilder = Join-Path $root 'Build-Public-Release.ps1'
$releaseRoot = Join-Path $root 'public-release'
$launcherZip = Join-Path $releaseRoot 'Aurora21-Alpha-v1-Public-Release-win-x64.zip'
$workRoot = Join-Path $env:TEMP ('Aurora21-Updater-' + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $workRoot 'publish'
$deployRoot = Join-Path $releaseRoot 'vps-launcher-release'
$updaterOutput = Join-Path $releaseRoot 'Aurora21-Updater.exe'
$deployArchive = Join-Path $releaseRoot 'Aurora21-VPS-Launcher-Release.zip'

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (Get-Process -Name 'Aurora21-Launcher','Aurora21-Updater' -ErrorAction SilentlyContinue) {
    throw 'Close Aurora21-Launcher and Aurora21-Updater before building.'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Updater project missing: $project"
}
if (-not (Test-Path -LiteralPath $launcherBuilder -PathType Leaf)) {
    throw "Launcher builder missing: $launcherBuilder"
}
if (-not [Uri]::IsWellFormedUriString($PublicBaseUrl, [UriKind]::Absolute) -or
    -not $PublicBaseUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PublicBaseUrl must be an absolute HTTPS URL.'
}

try {
    foreach ($generatedPath in @(
        (Join-Path $root 'bin'),
        (Join-Path $root 'obj'),
        (Join-Path $root 'Aurora21Updater\bin'),
        (Join-Path $root 'Aurora21Updater\obj'),
        $releaseRoot
    )) {
        if (Test-Path -LiteralPath $generatedPath) {
            Remove-Item -LiteralPath $generatedPath -Recurse -Force
        }
    }

    & $launcherBuilder
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $launcherZip -PathType Leaf)) {
        throw 'The public launcher release build did not produce its expected ZIP.'
    }

    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    dotnet publish $project -c Release -r win-x64 --self-contained --nologo -o $publishRoot `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) {
        throw "Updater publish failed with exit code $LASTEXITCODE."
    }

    $publishedUpdater = Join-Path $publishRoot 'Aurora21-Updater.exe'
    if (-not (Test-Path -LiteralPath $publishedUpdater -PathType Leaf)) {
        throw 'Published updater executable missing.'
    }
    Copy-Item -LiteralPath $publishedUpdater -Destination $updaterOutput -Force

    New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
    $packageName = Split-Path -Leaf $launcherZip
    Copy-Item -LiteralPath $launcherZip -Destination (Join-Path $deployRoot $packageName) -Force
    Copy-Item -LiteralPath $updaterOutput -Destination (Join-Path $deployRoot 'Aurora21-Updater.exe') -Force

    $package = Get-Item -LiteralPath $launcherZip
    $packageHash = Get-Sha256Hex -Path $launcherZip
    $manifest = [ordered]@{
        version = $Version
        packageUrl = ($PublicBaseUrl.TrimEnd('/') + '/' + $packageName)
        sha256 = $packageHash
        size = $package.Length
        publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    [IO.File]::WriteAllText(
        (Join-Path $deployRoot 'latest.json'),
        (($manifest | ConvertTo-Json) + "`n"),
        [Text.UTF8Encoding]::new($false))

    foreach ($name in @('install-launcher-release.sh','project21.caddy','project21.caddy.baseline')) {
        $source = Join-Path $root ('updater-vps\' + $name)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "VPS support file missing: $source"
        }
        $destination = Join-Path $deployRoot $name
        $content = [IO.File]::ReadAllText($source).Replace("`r`n", "`n")
        [IO.File]::WriteAllText($destination, $content, [Text.UTF8Encoding]::new($false))
    }

    $sumLines = @(
        "$packageHash  $packageName",
        "$(Get-Sha256Hex -Path $updaterOutput)  Aurora21-Updater.exe",
        "$(Get-Sha256Hex -Path (Join-Path $deployRoot 'latest.json'))  latest.json"
    )
    [IO.File]::WriteAllText(
        (Join-Path $deployRoot 'SHA256SUMS'),
        (($sumLines -join "`n") + "`n"),
        [Text.UTF8Encoding]::new($false))

    Compress-Archive -Path (Join-Path $deployRoot '*') -DestinationPath $deployArchive -CompressionLevel Optimal

    Write-Host 'AURORA 21 UPDATER READY' -ForegroundColor Green
    Write-Host "Updater: $updaterOutput"
    Write-Host "VPS bundle: $deployArchive"
    Write-Host "Version: $Version"
    Write-Host "Launcher SHA256: $packageHash"
    Write-Host "Updater SHA256: $(Get-Sha256Hex -Path $updaterOutput)"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
