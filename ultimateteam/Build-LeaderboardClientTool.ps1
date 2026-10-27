[CmdletBinding()]
param([string]$FmtRoot = $env:AURORA21_FMT_ROOT, [switch]$IfNeeded)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repository 'tools\fifa21-leaderboard-ui'
$destination = Join-Path $PSScriptRoot 'client-mod-tools'
$expectedFmtRevision = '67b35957725fc961e4262afb8be08e9cd1df239e'
$sourceFiles = @('Program.cs', 'CaptionMod.cs', 'CompiledMod.cs', 'CurrencyArt.cs', 'LocalisationTextEdit.cs', 'ChampionsTextEdit.cs', 'ZipDependencySelfTest.cs', 'Aurora21.LeaderboardUiTool.csproj', 'ZipDependency.targets', 'Prepare-FmtWarningFixes.ps1', 'FmtWarningFixes.json', 'FmtWarningSelfTest.cs')
$sourceDigest = ($sourceFiles | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $source $_) -Algorithm SHA256).Hash }) -join ':'
$artFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'assets\currency') -File -Recurse | Sort-Object FullName)
$artFiles += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'assets\champions') -File -Recurse | Sort-Object FullName)
$sourceDigest += ':' + (($artFiles | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ':')
$stampPath = Join-Path $destination 'build-stamp.json'
if ($IfNeeded -and (Test-Path -LiteralPath $stampPath -PathType Leaf)) {
    $stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
    if ($stamp.sourceDigest -ceq $sourceDigest -and $stamp.fmtRevision -ceq $expectedFmtRevision -and
        (Test-Path -LiteralPath (Join-Path $destination 'Aurora21-LeaderboardUiTool.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $destination 'ProDotNetZip.dll') -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $destination 'DotNetZip.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $destination 'SDK\FIFA21SDK.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $destination 'Plugins\FIFA21Plugin.dll') -PathType Leaf)) { return }
}
if (Get-Process -Name 'Aurora21-LeaderboardUiTool' -ErrorAction SilentlyContinue) { throw 'The leaderboard client tool is running. Wait for preparation to finish before rebuilding.' }
if (-not $FmtRoot) {
    $candidates = @(
        (Join-Path (Split-Path -Parent $repository) 'FrostbiteModdingTool'),
        (Join-Path $repository 'outputs\verification\champions-top25-20261003\fmt-reference')
    )
    $FmtRoot = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Libraries\FrostySdk\FrostySdk.csproj') -PathType Leaf } | Select-Object -First 1
}
if (-not $FmtRoot) { throw 'Set AURORA21_FMT_ROOT to the reviewed FrostbiteModdingTool checkout with its built FIFA21 plugin and SDK. See tools/fifa21-leaderboard-ui/README.md.' }
$FmtRoot = [IO.Path]::GetFullPath($FmtRoot)
$revision = & git -C $FmtRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision.Trim() -cne $expectedFmtRevision) { throw "Use FrostbiteModdingTool revision $expectedFmtRevision." }
$built = Join-Path $FmtRoot 'FrostbiteModdingUI\bin\Debug\net7.0-windows'
foreach ($required in @('Plugins\FIFA21Plugin.dll', 'SDK\FIFA21SDK.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $built $required) -PathType Leaf)) { throw "Missing reviewed FMT dependency: $required" }
}
$zipOverride = Join-Path $source 'ZipDependency.targets'
& dotnet build (Join-Path $source 'Aurora21.LeaderboardUiTool.csproj') "-p:FmtRoot=$FmtRoot" "-p:CustomAfterMicrosoftCommonTargets=$zipOverride" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Leaderboard client tool build failed.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# Copy the dependency closure beside its apphost. Exclude caches, logs, other tools and generated game data.
Get-ChildItem -LiteralPath $built -File -Filter '*.dll' |
    Where-Object { $_.Name -ne 'DotNetZip.dll' } | Copy-Item -Destination $destination -Force
# An upgrade must not leave the vulnerable assembly shipped by an earlier build.
$legacyZip = Join-Path $destination 'DotNetZip.dll'
if (Test-Path -LiteralPath $legacyZip -PathType Leaf) { Remove-Item -LiteralPath $legacyZip -Force }
foreach ($file in @('Aurora21-LeaderboardUiTool.exe', 'Aurora21-LeaderboardUiTool.deps.json', 'Aurora21-LeaderboardUiTool.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $built $file) -Destination (Join-Path $destination $file) -Force
}
foreach ($folder in @('SDK', 'Plugins')) { New-Item -ItemType Directory -Path (Join-Path $destination $folder) -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $built 'SDK\FIFA21SDK.dll') -Destination (Join-Path $destination 'SDK\FIFA21SDK.dll') -Force
Copy-Item -LiteralPath (Join-Path $built 'Plugins\FIFA21Plugin.dll') -Destination (Join-Path $destination 'Plugins\FIFA21Plugin.dll') -Force
foreach ($folder in @('FrostbiteProfiles', 'ThirdParty')) {
    if (Test-Path -LiteralPath (Join-Path $built $folder)) { Copy-Item -LiteralPath (Join-Path $built $folder) -Destination $destination -Recurse -Force }
}
& (Join-Path $destination 'Aurora21-LeaderboardUiTool.exe') --self-test-zip
if ($LASTEXITCODE -ne 0) { throw 'Packaged leaderboard ZIP dependency self-test failed.' }
& (Join-Path $destination 'Aurora21-LeaderboardUiTool.exe') --self-test-localisation
if ($LASTEXITCODE -ne 0) { throw 'Packaged localisation edit self-test failed.' }
& (Join-Path $destination 'Aurora21-LeaderboardUiTool.exe') --self-test-currency-art
if ($LASTEXITCODE -ne 0) { throw 'Packaged currency artwork self-test failed.' }
& (Join-Path $destination 'Aurora21-LeaderboardUiTool.exe') --self-test-fmt
if ($LASTEXITCODE -ne 0) { throw 'Packaged FMT warning corrections self-test failed.' }
@{ sourceDigest = $sourceDigest; fmtRevision = $expectedFmtRevision } | ConvertTo-Json | Set-Content -LiteralPath $stampPath -Encoding UTF8
Write-Host "Built leaderboard client tools: $destination"
