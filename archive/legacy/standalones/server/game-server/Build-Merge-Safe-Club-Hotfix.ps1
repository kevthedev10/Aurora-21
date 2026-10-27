[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$VpsCodeArchive,
    [Parameter(Mandatory = $true)][string]$MergedServerPath
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$ReleaseRoot = Join-Path $Root 'release'
$StageRoot = Join-Path $ReleaseRoot 'Project21-Merge-Safe-Club-Hotfix'
$ArchivePath = Join-Path $ReleaseRoot 'Project21-Merge-Safe-Club-Hotfix.zip'
$resolvedRelease = [IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\') + '\'
foreach ($candidate in @($StageRoot, $ArchivePath)) {
    if (-not [IO.Path]::GetFullPath($candidate).StartsWith($resolvedRelease, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe release path: $candidate"
    }
}
if (-not (Test-Path -LiteralPath $VpsCodeArchive -PathType Leaf)) { throw 'VPS code archive not found.' }
if (-not (Test-Path -LiteralPath $MergedServerPath -PathType Leaf)) { throw 'Merged server.py not found.' }

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $StageRoot) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }
if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath -Force }
New-Item -ItemType Directory -Path (Join-Path $StageRoot 'runtime-source/localfut21') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $StageRoot 'runtime-source') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $StageRoot 'ubuntu') -Force | Out-Null

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('project21-vps-overlay-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    & tar -xzf $VpsCodeArchive -C $temporary runtime-source/localfut21/gameplay_relay.py
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract gameplay_relay.py from the VPS archive.' }
    Copy-Item -LiteralPath $MergedServerPath -Destination (Join-Path $StageRoot 'runtime-source/localfut21/server.py')
    Copy-Item -LiteralPath (Join-Path $temporary 'runtime-source/localfut21/gameplay_relay.py') -Destination (Join-Path $StageRoot 'runtime-source/localfut21/gameplay_relay.py')
    Copy-Item -LiteralPath (Join-Path $Root 'runtime-source/localfut21/admin_actions.py') -Destination (Join-Path $StageRoot 'runtime-source/localfut21/admin_actions.py')
    Copy-Item -LiteralPath (Join-Path $Root 'runtime-source/authenticated_gateway.py') -Destination (Join-Path $StageRoot 'runtime-source/authenticated_gateway.py')
    Copy-Item -LiteralPath (Join-Path $Root 'runtime-source/host_control_agent.py') -Destination (Join-Path $StageRoot 'runtime-source/host_control_agent.py')
    Copy-Item -LiteralPath (Join-Path $Root 'ubuntu/install.sh') -Destination (Join-Path $StageRoot 'ubuntu/install.sh')
    Copy-Item -LiteralPath (Join-Path $Root 'ubuntu/reset-club.sh') -Destination (Join-Path $StageRoot 'ubuntu/reset-club.sh')
    Copy-Item -LiteralPath (Join-Path $Root 'MERGE-SAFE-CLUB-HOTFIX.md') -Destination (Join-Path $StageRoot 'README.md')
    Compress-Archive -Path (Join-Path $StageRoot '*') -DestinationPath $ArchivePath -CompressionLevel Optimal
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $systemTemporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporary.StartsWith($systemTemporary, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$hash = Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256
Write-Output "Built merge-safe server hotfix: $ArchivePath"
Write-Output "SHA256: $($hash.Hash)"
