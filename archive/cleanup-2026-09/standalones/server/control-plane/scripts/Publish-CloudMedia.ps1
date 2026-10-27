[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('music', 'video')]
    [string]$Type,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string]$Id,

    [Parameter(Mandatory = $true)]
    [string]$File,

    [string]$Bucket = 'project21-media',

    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'media-manifest.json'
}
$resolvedFile = (Resolve-Path -LiteralPath $File -ErrorAction Stop).Path
$extension = [IO.Path]::GetExtension($resolvedFile).ToLowerInvariant()
$allowedExtensions = if ($Type -eq 'music') { @('.mp3') } else { @('.mp4', '.m4v', '.wmv', '.avi', '.mov') }
if ($allowedExtensions -notcontains $extension) {
    throw "Unsupported $Type extension '$extension'. Allowed: $($allowedExtensions -join ', ')."
}

$fileInfo = Get-Item -LiteralPath $resolvedFile
$maximumBytes = if ($Type -eq 'music') { 80MB } else { 512MB }
if ($fileInfo.Length -le 0 -or $fileInfo.Length -gt $maximumBytes) {
    throw "The file must be between 1 byte and $maximumBytes bytes."
}

$contentTypes = @{
    '.mp3' = 'audio/mpeg'
    '.mp4' = 'video/mp4'
    '.m4v' = 'video/mp4'
    '.wmv' = 'video/x-ms-wmv'
    '.avi' = 'video/x-msvideo'
    '.mov' = 'video/quicktime'
}

$sha256 = [Security.Cryptography.SHA256]::Create()
$stream = [IO.File]::OpenRead($resolvedFile)
try {
    $hash = (($sha256.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '').ToUpperInvariant()
}
finally {
    $stream.Dispose()
    $sha256.Dispose()
}

$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
if (Test-Path -LiteralPath $manifestFile -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($null -eq $manifest -or [int]$manifest.version -ne 1 -or $null -eq $manifest.items) {
        throw "The media manifest is malformed: $manifestFile"
    }
}
else {
    $manifest = [pscustomobject]@{ version = 1; items = @() }
}

$objectKey = "$Type/$Id$extension"
$objectTarget = "$Bucket/$objectKey"
Write-Host "Uploading $objectKey..."
& npx wrangler r2 object put $objectTarget --file $resolvedFile --content-type $contentTypes[$extension] --remote
if ($LASTEXITCODE -ne 0) { throw "R2 media upload failed with exit code $LASTEXITCODE." }

$items = @($manifest.items | Where-Object { -not [string]::Equals([string]$_.id, $Id, [StringComparison]::OrdinalIgnoreCase) })
$items += [ordered]@{
    id = $Id
    type = $Type
    path = $objectKey
    sha256 = $hash
    bytes = [long]$fileInfo.Length
    enabled = $true
}
$updatedManifest = [ordered]@{ version = 1; items = $items }

$manifestDirectory = Split-Path -Parent $manifestFile
if (-not (Test-Path -LiteralPath $manifestDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
}
$temporaryManifest = "$manifestFile.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporaryManifest,
        (($updatedManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryManifest -Destination $manifestFile -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryManifest) { Remove-Item -LiteralPath $temporaryManifest -Force }
}

Write-Host 'Publishing manifest.json...'
& npx wrangler r2 object put "$Bucket/manifest.json" --file $manifestFile --content-type 'application/json' --remote
if ($LASTEXITCODE -ne 0) { throw "R2 manifest upload failed with exit code $LASTEXITCODE." }

Write-Host "Published '$Id' ($($fileInfo.Length) bytes, SHA-256 $hash)."
