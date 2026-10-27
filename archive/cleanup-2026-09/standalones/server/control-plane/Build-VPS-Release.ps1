[CmdletBinding()]
param(
    [string]$OutputRoot = 'C:\Users\ghoul\Downloads\server21',
    [string]$ReleaseName = 'Project21-VPS-Control-Plane-20260911'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$release = Join-Path $OutputRoot $ReleaseName
$zip = Join-Path $OutputRoot ($ReleaseName + '.zip')

& npm --prefix $root run check
if ($LASTEXITCODE -ne 0) { throw 'Control-plane type check failed.' }
& npm --prefix $root run build:selfhost
if ($LASTEXITCODE -ne 0) { throw 'Self-hosted control-plane build failed.' }

if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path $release | Out-Null

Copy-Item -LiteralPath (Join-Path $root 'dist-selfhost') -Destination $release -Recurse
Copy-Item -LiteralPath (Join-Path $root 'migrations') -Destination $release -Recurse
Copy-Item -LiteralPath (Join-Path $root 'package.json') -Destination $release
Copy-Item -LiteralPath (Join-Path $root 'package-lock.json') -Destination $release
Get-ChildItem -LiteralPath (Join-Path $root 'self-hosted-vps') | Where-Object Name -ne 'install-media.sh' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $release -Recurse
}

Get-ChildItem -LiteralPath $release -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($release.Length + 1).Replace('\', '/')
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS') -Encoding ascii

Compress-Archive -LiteralPath $release -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
