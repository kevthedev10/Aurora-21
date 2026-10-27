[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Files,

    [string]$Bucket = 'project21-media',

    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'media-manifest.json'
}
$publisher = Join-Path $PSScriptRoot 'Publish-CloudMedia.ps1'
if (-not (Test-Path -LiteralPath $publisher -PathType Leaf)) {
    throw "The cloud media publisher is missing: $publisher"
}

function ConvertTo-CloudMediaId {
    param([Parameter(Mandatory = $true)][string]$Name)

    $id = [Text.RegularExpressions.Regex]::Replace($Name, '[^A-Za-z0-9._-]+', '-').Trim([char[]]'._-')
    if ([string]::IsNullOrWhiteSpace($id)) { $id = 'track' }
    if ($id[0] -notmatch '[A-Za-z0-9]') { $id = 'track-' + $id }
    if ($id.Length -gt 72) { $id = $id.Substring(0, 72).TrimEnd([char[]]'._-') }
    return $id.ToLowerInvariant()
}

$resolvedFiles = @()
foreach ($inputFile in $Files) {
    $resolved = (Resolve-Path -LiteralPath $inputFile -ErrorAction Stop).Path
    $item = Get-Item -LiteralPath $resolved
    if ($item.PSIsContainer) { throw "Folders cannot be uploaded: $resolved" }
    if ([IO.Path]::GetExtension($resolved) -ine '.mp3') { throw "Only MP3 files are accepted: $resolved" }
    $resolvedFiles += $resolved
}
if ($resolvedFiles.Count -eq 0) { throw 'No MP3 files were supplied.' }

$usedIds = @{}
$queue = foreach ($resolved in $resolvedFiles) {
    $baseId = ConvertTo-CloudMediaId ([IO.Path]::GetFileNameWithoutExtension($resolved))
    $id = $baseId
    $suffix = 2
    while ($usedIds.ContainsKey($id)) {
        $id = '{0}-{1}' -f $baseId, $suffix
        $suffix++
    }
    $usedIds[$id] = $true
    [pscustomobject]@{ File = $resolved; Id = $id }
}

Write-Host "Publishing $($queue.Count) Project 21 cloud music track(s)..."
Write-Host ''
$published = 0
$failures = @()
foreach ($entry in $queue) {
    Write-Host "[$($published + $failures.Count + 1)/$($queue.Count)] $([IO.Path]::GetFileName($entry.File)) -> $($entry.Id)"
    try {
        & $publisher -Type music -Id $entry.Id -File $entry.File -Bucket $Bucket -ManifestPath $ManifestPath
        $published++
    }
    catch {
        $failures += "$($entry.File): $($_.Exception.Message)"
        Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ''
}

Write-Host "Published $published of $($queue.Count) track(s)."
if ($failures.Count -gt 0) {
    Write-Host 'Failures:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
