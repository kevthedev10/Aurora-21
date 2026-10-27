[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$ReleaseRoot = Join-Path $Root 'release'
$PackageDirectory = Join-Path $ReleaseRoot 'Project21-Player-Launcher'
$ArchivePath = Join-Path $ReleaseRoot 'Project21-Player-Launcher.zip'

function Assert-SafeReleasePath([string]$Path) {
    $resolvedRelease = [IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRelease, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the launcher release folder: $resolvedPath"
    }
}

& (Join-Path $Root 'Build-FIFA21-Launcher.ps1')

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
Assert-SafeReleasePath $PackageDirectory
Assert-SafeReleasePath $ArchivePath
if (Test-Path -LiteralPath $PackageDirectory) {
    Remove-Item -LiteralPath $PackageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
    Remove-Item -LiteralPath $ArchivePath -Force
}

New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null
$RootFiles = @(
    'control-plane.json',
    'Ensure-Player-Access.ps1',
    'Ensure-Python.ps1',
    'Ensure-WireGuard-Connection.ps1',
    'First-Run-WireGuard.ps1',
    'FIFA21.ico',
    'Launch-LocalFUT.ps1',
    'launcher-settings.example.json',
    'Player-Access-Common.ps1',
    'Project21-LocalAuthBridge.exe',
    'Project21-LocalFUT-Launcher.deps.json',
    'Project21-LocalFUT-Launcher.dll',
    'Project21-LocalFUT-Launcher.exe',
    'Project21-LocalFUT-Launcher.runtimeconfig.json',
    'README.md',
    'remote-server.json',
    'RUN FIFA 21 HERE.lnk',
    'RUN-FIFA21.cmd',
    'Stop-LocalFUT.ps1',
    'User-Settings.ps1',
    'Verify-WireGuardDownload.ps1',
    'wireguard-client.json',
    'WireGuard-Common.ps1'
)

foreach ($relativePath in $RootFiles) {
    $source = Join-Path $Root $relativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required player file is missing: $relativePath"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $PackageDirectory $relativePath)
}

$RuntimeDestination = Join-Path $PackageDirectory 'runtime-source'
$RelayDestination = Join-Path $RuntimeDestination 'localfut21'
New-Item -ItemType Directory -Path $RelayDestination -Force | Out-Null
$RuntimeFiles = @(
    'generate_local_cert.py',
    'gos2015-original-modulus.bin',
    'gs2019-original-modulus.bin',
    'PATCH_RUNNING_FIFA21_TRUST.py',
    'redirector_tls.py',
    'RESTORE_RUNNING_FIFA21_TRUST.py'
)
foreach ($relativePath in $RuntimeFiles) {
    $source = Join-Path (Join-Path $Root 'runtime-source') $relativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required connection file is missing: runtime-source\$relativePath"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $RuntimeDestination $relativePath)
}
Copy-Item -LiteralPath (Join-Path $Root 'runtime-source\localfut21\server.py') -Destination $RelayDestination

$ForbiddenFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File | Where-Object {
    $_.Name -match '(?i)(^|[._-])(secrets?|backup|private)([._-]|$)' -or
    $_.Extension -match '(?i)^\.(db|sqlite|sqlite3|bak|pem|key|pfx|p12)$'
})
if ($ForbiddenFiles.Count -gt 0) {
    throw "Sensitive file detected in player package: $($ForbiddenFiles.FullName -join ', ')"
}

$TextFiles = Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File | Where-Object {
    $_.Extension -match '(?i)^\.(cmd|json|md|ps1|py)$'
}
foreach ($file in $TextFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ($text -match '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
        $text -match '(?i)CF-Access-Client-Secret\s*:' -or
        $text -match '(?i)DISCORD_CLIENT_SECRET\s*[=:]' -or
        $text -match '(?i)HOST_AGENT_SECRET\s*[=:]' -or
        $text -match '(?im)^\s*PrivateKey\s*=\s*[A-Za-z0-9+/]{42}[A-Za-z0-9+/=]{2}') {
        throw "Credential material detected in player package: $($file.FullName)"
    }
}

Compress-Archive -Path (Join-Path $PackageDirectory '*') -DestinationPath $ArchivePath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256
Write-Output "Built player package: $ArchivePath"
Write-Output "SHA256: $($hash.Hash)"
Write-Output "Files: $(@(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File).Count)"
