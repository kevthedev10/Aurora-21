[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ZipPath, [Parameter(Mandatory=$true)][string]$ExtractPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
$ExtractPath = [IO.Path]::GetFullPath($ExtractPath)
if (Test-Path -LiteralPath $ExtractPath) { throw 'Use a fresh extraction directory.' }
[IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $ExtractPath)
Push-Location ([IO.Path]::GetTempPath())
try {
    & (Join-Path $ExtractPath 'Prepare-ClientMods.ps1') -ValidateOnly
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Fresh package check failed.' }
    foreach ($name in @('Prepare-ClientMods.ps1', 'Verify-Package.ps1')) {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $ExtractPath $name), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw "Invalid PowerShell: $name" }
    }
    $readme = Join-Path $ExtractPath 'START-HERE.md'
    $original = [IO.File]::ReadAllBytes($readme)
    try {
        [IO.File]::AppendAllText($readme, 'tamper fixture')
        $rejected = $false
        try { & (Join-Path $ExtractPath 'Verify-Package.ps1') }
        catch {
            if ($_.Exception.Message -notmatch 'Package file differs') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'Modified package was accepted.' }
    } finally { [IO.File]::WriteAllBytes($readme, $original) }
    & (Join-Path $ExtractPath 'Verify-Package.ps1')
    Write-Host 'PASS: fresh ZIP extraction, foreign working directory, paths with spaces, packaged compiler self-tests, script parsing, modified-file rejection and restored checksums.'
} finally { Pop-Location }
