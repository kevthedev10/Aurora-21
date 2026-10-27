[CmdletBinding()]
param(
    [string]$OutputRoot = 'C:\Users\ghoul\Downloads\server21',
    [string]$ReleaseName = 'Project21-VPS-Media-Seed-20260911'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sourceRoot = Join-Path $root 'sources'
$release = Join-Path $OutputRoot $ReleaseName
$mediaRoot = Join-Path $release 'media'
$zip = Join-Path $OutputRoot ($ReleaseName + '.zip')

function ConvertTo-MediaId([string]$Name) {
    $id = [regex]::Replace([IO.Path]::GetFileNameWithoutExtension($Name), '[^A-Za-z0-9._-]+', '-').Trim([char[]]'._-').ToLowerInvariant()
    if (-not $id) { $id = 'track' }
    if ($id[0] -notmatch '[A-Za-z0-9]') { $id = 'track-' + $id }
    if ($id.Length -gt 72) { $id = $id.Substring(0, 72).TrimEnd([char[]]'._-') }
    return $id
}

if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $mediaRoot 'music'), (Join-Path $mediaRoot 'video') | Out-Null

$items = @()
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File | Sort-Object Name) {
    if ($file.Extension -ieq '.mp3') {
        $id = ConvertTo-MediaId $file.Name
        $relative = 'music/' + $id + '.mp3'
        $type = 'music'
    } elseif ($file.Extension -ieq '.mp4') {
        $id = 'main-hero'
        $relative = 'video/main-hero.mp4'
        $type = 'video'
    } else {
        continue
    }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $mediaRoot $relative)
    $items += [ordered]@{
        id = $id
        type = $type
        path = $relative
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
        bytes = $file.Length
        enabled = $true
    }
}

[ordered]@{ version = 1; items = $items } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $mediaRoot 'manifest.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'self-hosted-vps\install-media.sh') -Destination $release

Get-ChildItem -LiteralPath $mediaRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($release.Length + 1).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS') -Encoding ascii

Compress-Archive -LiteralPath $release -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
