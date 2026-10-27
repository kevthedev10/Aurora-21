[CmdletBinding()]
param(
    [string]$PackageRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $PSScriptRoot 'release\Aurora21-Player-Launcher'
}
$resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$launchScript = Join-Path $resolvedPackageRoot 'Launch-LocalFUT.ps1'
$helperPath = Join-Path $resolvedPackageRoot 'Aurora21-AuthBridge.exe'
$sourcePath = Join-Path $resolvedPackageRoot 'FIFA21-LocalAuthBridge.cs'

if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Packaged auth bridge is missing: $helperPath"
}
if (Test-Path -LiteralPath $sourcePath) {
    throw "Player package must not contain auth bridge source: $sourcePath"
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($launchScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Packaged launch script did not parse: $($parseErrors.Message -join '; ')"
}

$functionAst = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Ensure-LocalAuthBridge'
}, $true)
if (-not $functionAst) {
    throw 'Ensure-LocalAuthBridge was not found in the packaged launch script.'
}

Invoke-Expression $functionAst.Extent.Text
$script:ProjectRoot = $resolvedPackageRoot
$resolvedHelper = Ensure-LocalAuthBridge
if ([IO.Path]::GetFullPath($resolvedHelper) -ne [IO.Path]::GetFullPath($helperPath)) {
    throw "Packaged auth bridge resolved to an unexpected path: $resolvedHelper"
}

Write-Output 'PASS: player package uses the bundled auth bridge without shipping its C# source.'
