[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [Parameter(Mandatory = $true)][string]$FmtRoot,
    [Parameter(Mandatory = $true)][string]$InventoryPath,
    [string]$OutputRoot,
    [string]$Python = 'python',
    [int]$Limit = 0
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot ('outputs\FIFA21-FUT-UI-Art-' + (Get-Date -Format 'yyyy-MM-dd')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$FmtRoot = [IO.Path]::GetFullPath($FmtRoot)
$InventoryPath = [IO.Path]::GetFullPath($InventoryPath)
$expectedRevision = '67b35957725fc961e4262afb8be08e9cd1df239e'
$revision = & git -C $FmtRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision.Trim() -cne $expectedRevision) { throw "Use the reviewed FMT revision $expectedRevision." }
$fmtBin = Join-Path $FmtRoot 'FrostbiteModdingUI\bin\Debug\net7.0-windows'
foreach ($file in @('FrostySdk.dll', 'SDK\FIFA21SDK.dll', 'Plugins\FIFA21Plugin.dll', '_GameCaches\fifa21.cache')) {
    if (-not (Test-Path -LiteralPath (Join-Path $fmtBin $file) -PathType Leaf)) { throw "Missing existing FMT dependency/cache: $file. Cold game scans are outside this script." }
}
$tool = Join-Path $fmtBin 'Aurora21.UiArtExport.exe'
$ownedProcess = Get-CimInstance Win32_Process -Filter "Name = 'Aurora21.UiArtExport.exe'" |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $tool }
if ($ownedProcess) { throw 'This FMT installation is already running the exporter. Wait before rebuilding.' }
$workRoot = Join-Path $repoRoot 'outputs\verification\fifa21-ui-art-tool'
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$intermediate = (Join-Path $workRoot 'obj') + '\'
& dotnet build (Join-Path $PSScriptRoot 'Fifa21UiArt.csproj') "-p:FmtBin=$fmtBin" "-p:BaseIntermediateOutputPath=$intermediate" -o $fmtBin --nologo
if ($LASTEXITCODE -ne 0) { throw 'Exporter build failed.' }
$selection = Join-Path $workRoot 'selection.json'
$gallery = Join-Path $PSScriptRoot 'build_gallery.py'
& $Python $gallery --inventory $InventoryPath --output $selection
if ($LASTEXITCODE -ne 0) { throw 'Asset selection failed.' }
$native = Join-Path $workRoot 'native'
$exportArgs = @($GameRoot, $native, $selection)
if ($Limit -gt 0) { $exportArgs += [string]$Limit }
& $tool @exportArgs
if ($LASTEXITCODE -ne 0) { throw "Native extraction failed; review $native\native-manifest.json." }
& $Python $gallery --native $native --output $OutputRoot
if ($LASTEXITCODE -ne 0) { throw "Conversion incomplete; review $OutputRoot\index.json." }
Write-Host "Artwork gallery: $OutputRoot\index.html"
