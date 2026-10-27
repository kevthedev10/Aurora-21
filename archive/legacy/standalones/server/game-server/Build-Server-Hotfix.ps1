[CmdletBinding()]
param(
    [string]$ServerSourcePath
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$ReleaseRoot = Join-Path $Root 'release'
$StageRoot = Join-Path $ReleaseRoot 'Project21-Server-Hotfix'
$ArchivePath = Join-Path $ReleaseRoot 'Project21-Server-Hotfix.zip'

function Assert-SafeReleasePath([string]$Path) {
    $releasePrefix = [IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the server release directory: $resolved"
    }
}

$Files = @(
    'README.md',
    'runtime-source\authenticated_gateway.py',
    'runtime-source\host_control_agent.py',
    'runtime-source\localfut21\admin_actions.py',
    'runtime-source\localfut21\shared_market.py',
    'runtime-source\localfut21\server.py',
    'test_admin_card_grant.py',
    'test_game_presence.py',
    'test_matchmaking_start_response.py',
    'test_rivals_pairing.py',
    'test_rivals_relay.py',
    'test_online_transfer_market.py',
    'ubuntu\install.sh',
    'ubuntu\rivals-relay.sh',
    'ubuntu\kick-all-players.sh',
    'ubuntu\project21-kick-all.in',
    'ubuntu\reset-club.sh',
    'ubuntu\project21-reset-club.in',
    'ubuntu\watch-server-traffic.sh',
    'ubuntu\project21-live-logs.in',
    'ubuntu\project21-gateway.service.in',
    'ubuntu\project21-agent.service.in'
)

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
Assert-SafeReleasePath $StageRoot
Assert-SafeReleasePath $ArchivePath
if (Test-Path -LiteralPath $StageRoot) {
    Remove-Item -LiteralPath $StageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
    Remove-Item -LiteralPath $ArchivePath -Force
}

foreach ($relativePath in $Files) {
    $source = if ($relativePath -eq 'runtime-source\localfut21\server.py' -and $ServerSourcePath) {
        [IO.Path]::GetFullPath($ServerSourcePath)
    } else {
        Join-Path $Root $relativePath
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required hotfix file is missing: $relativePath"
    }
    $destination = Join-Path $StageRoot $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relativePath in $Files) {
        $source = Join-Path $StageRoot $relativePath
        $entryName = $relativePath.Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $source,
            $entryName,
            [IO.Compression.CompressionLevel]::Optimal
        ) | Out-Null
    }
} finally {
    $archive.Dispose()
}
$hash = Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256
Write-Output "Built server hotfix: $ArchivePath"
Write-Output "SHA256: $($hash.Hash)"
Write-Output "Files: $($Files.Count)"
