param(
    [Parameter(Mandatory = $true)][string]$ReportRoot,
    [Parameter(Mandatory = $true)][string]$Revision,
    [ValidateSet('0', '1')][string]$GcMode = '1',
    [ValidateRange(0, 3600)][int]$Seconds = 20,
    [ValidateRange(1, 1000)][int]$Samples = 100,
    [ValidateRange(2, 100)][int]$Peers = 80,
    [ValidateRange(1, 500)][int]$Pool = 10,
    # Synthetic cards per club; 0 keeps the configured default.
    [ValidateRange(0, 5000)][int]$Inventory = 0
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$reportPath = [IO.Path]::GetFullPath($ReportRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'outputs/performance')) + [IO.Path]::DirectorySeparatorChar
if (-not $reportPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must be inside this checkout outputs/performance.' }
if (Test-Path -LiteralPath $reportPath) { throw 'Preserve existing profiling output; choose a fresh directory.' }
if ($Peers % 2 -ne 0) { throw 'Peers must be even.' }
$null = New-Item -ItemType Directory -Path $reportPath
$toolsPath = Join-Path $repoRoot 'outputs/performance/dotnet-tools'
$traceTool = Join-Path $toolsPath 'dotnet-trace.exe'
Push-Location $repoRoot
$priorGcMode = $env:DOTNET_gcServer
try {
    if (-not (Test-Path -LiteralPath $traceTool)) {
        & dotnet tool install dotnet-trace --version 10.0.745401 --tool-path $toolsPath
        if ($LASTEXITCODE -ne 0) { throw 'dotnet-trace install failed.' }
    }
    $harnessRoot = Join-Path $reportPath 'harness'
    & dotnet publish ultimateteam/server/tools/Aurora21.UltimateTeam.Performance/Aurora21.UltimateTeam.Performance.csproj -c Release -r win-x64 --self-contained true -o $harnessRoot
    if ($LASTEXITCODE -ne 0) { throw 'Profiling tool publish failed.' }
    $tracePath = Join-Path $reportPath 'functions.nettrace'
    $runPath = Join-Path $reportPath 'run'
    $env:DOTNET_gcServer = $GcMode
    $inventoryArgs = if ($Inventory -gt 0) { @('--inventory', $Inventory) } else { @() }
    & $traceTool collect --profile gc-verbose,dotnet-sampled-thread-time --providers 'Aurora21-Performance:0xFFFFFFFFFFFFFFFF:4' --buffersize 256 --output $tracePath --show-child-io -- `
        (Join-Path $harnessRoot 'Aurora21.UltimateTeam.Performance.exe') --seconds $Seconds --samples $Samples --peers $Peers --pool $Pool @inventoryArgs --timings true --waits false --revision $Revision --output $runPath
    if ($LASTEXITCODE -gt 1 -or -not (Test-Path (Join-Path $runPath 'run.json'))) { throw 'Collection or workload failed before reporting.' }
    $run = Get-Content -Raw -LiteralPath (Join-Path $runPath 'run.json') | ConvertFrom-Json
    if ($run.framework -ne '.NET 10.0.0' -or $run.serverGarbageCollection -ne ($GcMode -eq '1')) { throw 'Runtime/GC mode mismatch.' }
    & dotnet run --project ultimateteam/server/tools/Aurora21.UltimateTeam.TraceAnalysis/Aurora21.UltimateTeam.TraceAnalysis.csproj -c Release -- $tracePath (Join-Path $reportPath 'analysis.json')
    if ($LASTEXITCODE -ne 0) { throw 'Analysis failed or reported event loss; retain evidence for review.' }
} finally {
    $env:DOTNET_gcServer = $priorGcMode
    Pop-Location
}
