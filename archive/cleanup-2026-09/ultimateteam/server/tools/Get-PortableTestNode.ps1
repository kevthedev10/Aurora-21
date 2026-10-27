[CmdletBinding()]
param()

# Test dependency only: no system install or PATH changes. Verify against the published checksum.
$ErrorActionPreference = 'Stop'
$version = 'v22.22.3'
$archive = "node-$version-win-x64.zip"
$downloadRoot = Join-Path ([IO.Path]::GetTempPath()) ('aurora21-node-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloadRoot | Out-Null
$baseUri = "https://nodejs.org/dist/$version/"
$checksums = Join-Path $downloadRoot 'SHASUMS256.txt'
$zip = Join-Path $downloadRoot $archive
Invoke-WebRequest -UseBasicParsing -Uri ($baseUri + 'SHASUMS256.txt') -OutFile $checksums
Invoke-WebRequest -UseBasicParsing -Uri ($baseUri + $archive) -OutFile $zip
$lines = @(Get-Content -LiteralPath $checksums | Where-Object { $_.EndsWith($archive) })
if ($lines.Count -ne 1) { throw 'Node archive checksum entry missing or ambiguous.' }
$expected = ($lines[0] -split '\s+')[0]
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'Node archive checksum mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath $downloadRoot
Join-Path $downloadRoot "node-$version-win-x64\node.exe"
