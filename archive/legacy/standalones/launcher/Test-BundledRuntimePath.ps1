[CmdletBinding()]
param(
    [string]$LauncherRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($LauncherRoot)) {
    $LauncherRoot = $PSScriptRoot
}

$launchScript = Join-Path $LauncherRoot 'Launch-LocalFUT.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $launchScript,
    [ref]$tokens,
    [ref]$parseErrors
)
if ($parseErrors.Count -gt 0) {
    throw "Launch-LocalFUT.ps1 did not parse: $($parseErrors.Message -join '; ')"
}

$definition = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Ensure-WorkingCopy'
}, $true)
if (-not $definition) {
    throw 'Ensure-WorkingCopy was not found.'
}
Invoke-Expression $definition.Extent.Text

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Aurora21-BundledRuntime-' + [Guid]::NewGuid().ToString('N'))
$launcherCopy = Join-Path $testRoot 'launcher'
$foreignWorkingDirectory = Join-Path $testRoot 'foreign-working-directory'
$VendorRoot = Join-Path $launcherCopy 'runtime-source'
$requiredFiles = @(
    'localfut21\server.py',
    'redirector_tls.py',
    'PATCH_RUNNING_FIFA21_TRUST.py',
    'certs\spring18-cert.pem',
    'certs\spring18-chain.pem',
    'certs\spring18-key.pem',
    'gos2015-original-modulus.bin',
    'local-gos2015-modulus.bin'
)

try {
    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $VendorRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        New-Item -ItemType File -Path $path -Force | Out-Null
    }
    New-Item -ItemType Directory -Path $foreignWorkingDirectory -Force | Out-Null
    Push-Location $foreignWorkingDirectory
    try {
        Ensure-WorkingCopy ''
        Ensure-WorkingCopy (Join-Path $testRoot 'stale-old-source')
    }
    finally {
        Pop-Location
    }

    Remove-Item -LiteralPath (Join-Path $VendorRoot 'localfut21\server.py') -Force
    $missingDetected = $false
    try {
        Ensure-WorkingCopy ''
    }
    catch {
        $missingDetected = $_.Exception.Message -like '*localfut21\server.py*'
    }
    if (-not $missingDetected) {
        throw 'Bundled server.py absence was not detected.'
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).Path
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected test path: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host 'PASS launcher always validates bundled runtime-source from a foreign working directory.'
