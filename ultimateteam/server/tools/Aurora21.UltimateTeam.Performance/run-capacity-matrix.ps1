param(
    [Parameter(Mandatory=$true)][string]$ReportRoot,
    [Parameter(Mandatory=$true)][string]$Revision,
    [ValidateRange(1,500)][int]$Pool = 250,
    [ValidateRange(1,1000)][int]$Samples = 50
)
$ErrorActionPreference = 'Stop'
$checkoutRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
Set-Location -LiteralPath $checkoutRoot
$matrixRoot = [IO.Path]::GetFullPath($ReportRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $checkoutRoot 'outputs/performance')) + [IO.Path]::DirectorySeparatorChar
if (-not $matrixRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ReportRoot must be a new directory under this checkout outputs/performance.'
}
if (Test-Path -LiteralPath $matrixRoot) { throw 'ReportRoot already exists; preserve previous evidence.' }
New-Item -ItemType Directory -Path $matrixRoot | Out-Null
$harnessRoot = Join-Path $matrixRoot 'harness'
& dotnet publish ultimateteam/server/tools/Aurora21.UltimateTeam.Performance/Aurora21.UltimateTeam.Performance.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.0 --artifacts-path (Join-Path $matrixRoot 'build') -o $harnessRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Benchmark publish failed; no tests started.' }
$env:DOTNET_gcServer = '1'
$env:DOTNET_GCServer = '1'
Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
$benchmarkExe = Join-Path $harnessRoot 'Aurora21.UltimateTeam.Performance.exe'
$results = @()
foreach ($clubs in @(100,500,1000)) {
    $durations = if ($clubs -eq 100) { @(300) } else { @(300,1200) }
    foreach ($seconds in $durations) {
        $label = "clubs-$clubs-cards-2000-seconds-$seconds"
        $profileRoot = Join-Path $matrixRoot $label
        $profileLog = Join-Path $matrixRoot "$label.log"
        Write-Host "Starting $label pool=$Pool samples=$Samples; fail-fast enabled"
        $ErrorActionPreference = 'Continue'
        & $benchmarkExe --seconds $seconds --samples $Samples --peers $clubs --pool $Pool --inventory 2000 --timings true --waits true --functions true --fail-fast true --revision $Revision --output $profileRoot 2>&1 | Tee-Object -FilePath $profileLog
        $profileExit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        $runPath = Join-Path $profileRoot 'run.json'
        $passed = $false
        if (Test-Path -LiteralPath $runPath) {
            $report = Get-Content -LiteralPath $runPath -Raw | ConvertFrom-Json
            $passed = $profileExit -eq 0 -and $report.passed -eq $true -and $report.errors -eq 0 -and
                $report.peers -eq $clubs -and $report.clubPlayerCount -eq 2000 -and
                $report.poolMaximum -eq $Pool -and $report.requestedDurationSeconds -eq $seconds -and
                $report.measuredDurationSeconds -ge $seconds -and $report.storageBackend -eq 'relational' -and
                $report.framework -eq '.NET 10.0.0' -and $report.serverGarbageCollection -eq $true -and
                $report.failFast -eq $true -and $report.sourceRevision -eq $Revision
        }
        $results += [pscustomobject]@{ clubs=$clubs; cards=2000; seconds=$seconds; exitCode=$profileExit; passed=$passed; completedReport=(Test-Path -LiteralPath $runPath); failureReport=(Test-Path -LiteralPath (Join-Path $profileRoot 'failure.json')); checkpoint=(Test-Path -LiteralPath (Join-Path $profileRoot 'checkpoint.json')); log=$profileLog }
        ConvertTo-Json -InputObject @($results) -Depth 5 | Set-Content -LiteralPath (Join-Path $matrixRoot 'matrix-status.json') -Encoding UTF8
        if (-not $passed) { throw "Stopped at $label. Inspect its log, failure.json/checkpoint.json or run.json before resuming. No later profiles started." }
        Write-Host "Passed $label; reports saved."
    }
}
Write-Host 'All five capacity profiles passed their current router/invariant/latency gates.'
