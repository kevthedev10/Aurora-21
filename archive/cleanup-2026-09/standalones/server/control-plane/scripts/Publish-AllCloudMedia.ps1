[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$MediaRoot,
  [string]$Bucket = 'project21-media'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $MediaRoot -ErrorAction Stop).Path
$publisher = Join-Path $PSScriptRoot 'Publish-CloudMedia.ps1'
$files = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
  $_.Extension.ToLowerInvariant() -in @('.mp3','.mp4','.m4v','.wmv','.avi','.mov')
}
if ($files.Count -eq 0) { throw "No supported media files found under $root." }
foreach ($file in $files) {
  $type = if ($file.Extension -ieq '.mp3') { 'music' } else { 'video' }
  # Stable, manifest-safe ID based on the relative path; duplicate names cannot collide.
  $rootUri = [Uri]($root.TrimEnd('\') + '\')
  $relative = [Uri]::UnescapeDataString($rootUri.MakeRelativeUri([Uri]$file.FullName).ToString())
  $id = [IO.Path]::GetFileNameWithoutExtension($relative) -replace '[^A-Za-z0-9._-]', '-'
  if ($id.Length -gt 80) { $id = $id.Substring(0,80) }
  if ($id -notmatch '^[A-Za-z0-9]') { $id = 'media-' + $id }
  Write-Host "Publishing $type/$id ..."
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publisher -Type $type -Id $id -File $file.FullName -Bucket $Bucket
  if ($LASTEXITCODE -ne 0) { throw "Failed to publish $($file.FullName)." }
}
Write-Host "Published $($files.Count) file(s). manifest.json was updated after each upload."
