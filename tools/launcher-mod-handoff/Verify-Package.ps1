[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$index = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'checksums.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
foreach ($entry in $index.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $entry.path))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid package path: $($entry.path)" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package file: $($entry.path)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
        throw "Package file differs: $($entry.path)"
    }
}
Write-Host ('Verified package files: ' + @($index.files).Count)
