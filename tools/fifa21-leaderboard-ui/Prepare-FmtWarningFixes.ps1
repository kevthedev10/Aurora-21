[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('FMT.FileTools', 'FrostySdk', 'CSIL')][string]$ProjectName,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FmtWarningFixes.json') -Raw | ConvertFrom-Json
$encoding = New-Object System.Text.UTF8Encoding($false)
$compileFiles = @()

function Write-ChangedSource([string]$Path, [string]$Text) {
    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and [IO.File]::ReadAllText($Path) -ceq $Text) { return }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $encoding)
}

foreach ($file in $manifest.files | Where-Object { $_.project -ceq $ProjectName }) {
    $inputPath = Join-Path $ProjectDirectory $file.path
    $text = [IO.File]::ReadAllText($inputPath).Replace("`r`n", "`n")
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($hasher.ComputeHash($encoding.GetBytes($text)))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    if ($hash -cne $file.sha256) {
        throw "Reviewed FMT source differs: $ProjectName/$($file.path). Expected revision $($manifest.revision); refusing to apply warning fixes."
    }
    foreach ($edit in $file.edits) {
        if ([regex]::Matches($text, [regex]::Escape($edit.before)).Count -ne 1) {
            throw "Warning fix context differs: $ProjectName/$($file.path)."
        }
        $text = $text.Replace($edit.before, $edit.after)
    }
    Write-ChangedSource (Join-Path (Join-Path $OutputRoot $ProjectName) $file.path) $text
    $compileFiles += $file.path.Replace('/', '\')
}
if ($ProjectName -ceq 'CSIL') {
    # CSIL disables generated assembly info, including the SDK's platform annotation.
    Write-ChangedSource (Join-Path (Join-Path $OutputRoot $ProjectName) 'Aurora21.WindowsPlatform.cs') "[assembly: System.Runtime.Versioning.SupportedOSPlatform(`"windows7.0`")]`n"
    $compileFiles += 'Aurora21.WindowsPlatform.cs'
}
# Compile only the current manifest's files, excluding stale copies from earlier fixes.
Write-ChangedSource (Join-Path (Join-Path $OutputRoot $ProjectName) 'compile-files.txt') (($compileFiles -join "`n") + "`n")
