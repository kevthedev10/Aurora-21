param(
    [Parameter(Mandatory=$true)][string]$ReportRoot,
    [Parameter(Mandatory=$true)][string]$Revision,
    # Extra harness arguments for each arm, as one space-separated string (for example '--auto-prepare 100').
    [string]$ControlArgs = '',
    [string]$CandidateArgs = '',
    [ValidateRange(1,10)][int]$Pairs = 3,
    [ValidateRange(2,1000)][int]$Clubs = 100,
    [ValidateRange(1,5000)][int]$Inventory = 2000,
    [ValidateRange(10,600)][int]$Seconds = 60,
    [ValidateRange(1,500)][int]$Pool = 250,
    [ValidateRange(0.1,100)][double]$TargetRps = 2,
    # An already published harness folder; by default the script publishes one into the report root.
    [string]$Harness = '',
    # A separately published harness for the control arm, to compare two builds; defaults to -Harness. It loads the
    # rules above its own folder, so publish an older build inside a git worktree of its revision.
    [string]$ControlHarness = ''
)
# Screening comparison: alternating control/candidate runs of a small paced profile, judged by cost
# per request (PostgreSQL CPU, harness CPU) rather than throughput. The small profile does not
# saturate the host, so it cannot show capacity; confirm a passing candidate with the 1,000-club
# profile before changing a default. Allocation is recorded per run but not compared: the soak
# counter is unreliable under server GC. Measure allocation with collect-function-profile.ps1.
$ErrorActionPreference = 'Stop'
if ($Clubs % 2 -ne 0) { throw 'Clubs must be even.' }
$checkoutRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
Set-Location -LiteralPath $checkoutRoot
$screenRoot = [IO.Path]::GetFullPath($ReportRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $checkoutRoot 'outputs/performance')) + [IO.Path]::DirectorySeparatorChar
if (-not $screenRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ReportRoot must be a new directory under this checkout outputs/performance.'
}
if (Test-Path -LiteralPath $screenRoot) { throw 'ReportRoot already exists; preserve previous evidence.' }
New-Item -ItemType Directory -Path $screenRoot | Out-Null
if (-not $Harness) {
    $Harness = Join-Path $screenRoot 'harness'
    & dotnet publish ultimateteam/server/tools/Aurora21.UltimateTeam.Performance/Aurora21.UltimateTeam.Performance.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.0 --artifacts-path (Join-Path $screenRoot 'build') -o $Harness --nologo *> (Join-Path $screenRoot 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark publish failed; no runs started.' }
}
$exe = Join-Path ([IO.Path]::GetFullPath($Harness)) 'Aurora21.UltimateTeam.Performance.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Harness missing: $exe" }
$controlExe = if ($ControlHarness) { Join-Path ([IO.Path]::GetFullPath($ControlHarness)) 'Aurora21.UltimateTeam.Performance.exe' } else { $exe }
if (-not (Test-Path -LiteralPath $controlExe)) { throw "Control harness missing: $controlExe" }
# A harness reads rules and catalogs from the nearest ultimateteam/runtime-source/ultimateteam21 above its own folder
# (TestRuntime.DataRoot); only the schema is built in. A control build older than a rules change must therefore be
# published inside a git worktree of its own revision, or it loads the candidate's rules and may reject new keys.
function Resolve-DataRoot([string]$harnessExe) {
    for ($directory = Split-Path -Parent $harnessExe; $directory; $directory = Split-Path -Parent $directory) {
        $candidate = Join-Path $directory 'ultimateteam/runtime-source/ultimateteam21'
        if (Test-Path -LiteralPath (Join-Path $candidate 'players.json')) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    throw "No ultimateteam/runtime-source/ultimateteam21 above $harnessExe"
}
$dataRoot = Resolve-DataRoot $exe
$controlDataRoot = Resolve-DataRoot $controlExe
if ($ControlHarness -and $controlDataRoot -eq $dataRoot) {
    Write-Warning "The control harness reads the candidate's rules ($dataRoot). That is valid only if the rules did not change between the builds; otherwise publish the control harness inside a git worktree of its revision."
}
$env:DOTNET_gcServer = '1'
$env:DOTNET_GCServer = '1'
Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue

# Cumulative CPU of the harness's throwaway cluster only: its postmaster runs from a temporary
# ultimateteam21-pg-* data directory, and its backends are that postmaster's children. A real club
# cluster under %LOCALAPPDATA% is never counted.
$samplerScript = {
    param($csv, $stop)
    while (-not (Test-Path -LiteralPath $stop)) {
        try {
            $all = @(Get-CimInstance Win32_Process -Filter "Name='postgres.exe'" -Property ProcessId,ParentProcessId,CommandLine,KernelModeTime,UserModeTime,PrivatePageCount)
            $masters = @($all | Where-Object { $_.CommandLine -like '*ultimateteam21-pg-*' -and $_.CommandLine -notlike '*--fork*' })
            $ids = @($masters | ForEach-Object { $_.ProcessId })
            $members = @($all | Where-Object { $ids -contains $_.ProcessId -or $ids -contains $_.ParentProcessId })
            [uint64]$cpu = 0; [uint64]$private = 0
            foreach ($process in $members) { $cpu += [uint64]$process.KernelModeTime + [uint64]$process.UserModeTime; $private += [uint64]$process.PrivatePageCount }
            Add-Content -LiteralPath $csv -Encoding UTF8 -Value ('{0},{1},{2},{3},{4}' -f (Get-Date).ToUniversalTime().ToString('o'), $cpu, $members.Count, $ids.Count, $private)
        } catch { Add-Content -LiteralPath $csv -Encoding UTF8 -Value ('# sample error: ' + $_.Exception.Message) }
        Start-Sleep -Milliseconds 500
    }
}

function Split-Arguments([string]$text) { if ($text.Trim()) { return @($text.Trim() -split '\s+') } else { return @() } }

function Invoke-ScreenRun([string]$label, [string]$arm, [string[]]$extra) {
    $runExe = if ($arm -eq 'control') { $controlExe } else { $exe }
    $out = Join-Path $screenRoot $label
    $csv = Join-Path $screenRoot "$label-postgres.csv"
    $log = Join-Path $screenRoot "$label.log"
    $stop = Join-Path $screenRoot "$label.sampler-stop"
    'timestampUtc,postgresCpu100ns,postgresProcesses,postmasters,postgresPrivateBytes' | Set-Content -LiteralPath $csv -Encoding UTF8
    $sampler = Start-Job -ScriptBlock $samplerScript -ArgumentList $csv, $stop
    Write-Host "=== $label ($arm) start $(Get-Date -Format o)"
    try {
        $ErrorActionPreference = 'Continue'
        & $runExe --seconds $Seconds --samples 5 --peers $Clubs --pool $Pool --inventory $Inventory `
            --timings true --waits false --target-rps-per-player $TargetRps @extra `
            --revision $Revision --output $out 2>&1 | ForEach-Object {
                Add-Content -LiteralPath $log -Encoding UTF8 -Value ((Get-Date).ToUniversalTime().ToString('o') + ' ' + $_) }
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
    } finally {
        New-Item -ItemType File -Path $stop | Out-Null
        Wait-Job $sampler -Timeout 30 | Out-Null
        Remove-Job $sampler -Force
    }
    $runPath = Join-Path $out 'run.json'
    if (-not (Test-Path -LiteralPath $runPath)) { throw "No run.json for $label (exit $code); stopping." }
    $report = Get-Content -LiteralPath $runPath -Raw | ConvertFrom-Json
    if ($report.errors -ne 0 -or $report.serverGarbageCollection -ne $true -or $report.peers -ne $Clubs -or $report.clubPlayerCount -ne $Inventory) {
        throw "Run $label is not a valid screening sample (errors=$($report.errors), serverGC=$($report.serverGarbageCollection))."
    }
    $soakLine = Get-Content -LiteralPath $log | Where-Object { $_ -match ' Soak 0\.0 min' } | Select-Object -First 1
    if (-not $soakLine) { throw "No soak start in $log." }
    $soakStart = [DateTime]::Parse($soakLine.Split(' ')[0], $null, [Globalization.DateTimeStyles]::RoundtripKind)
    $soakEnd = $soakStart.AddSeconds([double]$report.measuredDurationSeconds)
    $samples = @(Import-Csv -LiteralPath $csv | Where-Object { $_.timestampUtc -notlike '#*' } | ForEach-Object {
        [pscustomobject]@{ Time = [DateTime]::Parse($_.timestampUtc, $null, [Globalization.DateTimeStyles]::RoundtripKind)
            Cpu = [double]$_.postgresCpu100ns; Masters = [int]$_.postmasters; Private = [double]$_.postgresPrivateBytes } })
    $first = $samples | Where-Object { $_.Time -ge $soakStart } | Select-Object -First 1
    $last = $samples | Where-Object { $_.Time -le $soakEnd } | Select-Object -Last 1
    $window = @($samples | Where-Object { $_.Time -ge $soakStart -and $_.Time -le $soakEnd })
    if (-not $first -or -not $last -or $last.Time -le $first.Time) { throw "Too few PostgreSQL samples in the soak of $label." }
    if (@($window | Where-Object { $_.Masters -ne 1 }).Count -gt 0) { throw "Expected exactly one throwaway postmaster during $label." }
    $requestsPerSecond = [double]$report.measuredRequests / [double]$report.measuredDurationSeconds
    $postgresCores = ($last.Cpu - $first.Cpu) / 1e7 / ($last.Time - $first.Time).TotalSeconds
    return [pscustomobject]@{
        label = $label; arm = $arm; arguments = ($extra -join ' '); exitCode = $code
        pairRounds = [long]$report.pairRounds; measuredRequests = [long]$report.measuredRequests
        requestsPerSecond = [math]::Round($requestsPerSecond, 1)
        postgresCores = [math]::Round($postgresCores, 3)
        postgresCpuMsPerRequest = [math]::Round($postgresCores * 1000 / $requestsPerSecond, 3)
        harnessCpuMsPerRequest = [math]::Round([double]$report.processCpuSecondsSoak * 1000 / [double]$report.measuredRequests, 3)
        # Recorded, not compared: under server GC identical runs reported 5.4 and 12.7 GB, and small runs
        # went negative (reported as unknown). Use collect-function-profile.ps1 to measure allocation.
        allocatedKBPerRequest = if ([double]$report.allocatedBytesSoak -ge 0) { [math]::Round([double]$report.allocatedBytesSoak / 1024 / [double]$report.measuredRequests, 1) } else { $null }
        postgresPrivateMBMax = [math]::Round((($window | Measure-Object Private -Maximum).Maximum) / 1MB, 0)
        gcPauseSeconds = [math]::Round([double]$report.gcPauseSecondsSoak, 2)
        maxAutoPrepare = $report.maxAutoPrepare
    }
}

$results = @()
$control = Split-Arguments $ControlArgs
$candidate = Split-Arguments $CandidateArgs
for ($pair = 1; $pair -le $Pairs; $pair++) {
    # Alternate the order (ABBA...) so slow host drift does not favour one arm.
    $order = if ($pair % 2 -eq 1) { @('control', 'candidate') } else { @('candidate', 'control') }
    foreach ($arm in $order) {
        $extra = if ($arm -eq 'control') { $control } else { $candidate }
        $label = '{0:D2}-{1}' -f ($results.Count + 1), $arm
        $results += Invoke-ScreenRun $label $arm $extra
        ConvertTo-Json -InputObject @($results) -Depth 4 | Set-Content -LiteralPath (Join-Path $screenRoot 'screen-runs.json') -Encoding UTF8
    }
}

function Median([double[]]$values) { $sorted = @($values | Sort-Object); $n = $sorted.Count
    if ($n % 2) { return $sorted[($n - 1) / 2] } else { return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 } }
$metrics = 'postgresCpuMsPerRequest', 'harnessCpuMsPerRequest', 'requestsPerSecond', 'postgresPrivateMBMax'
$summary = [ordered]@{ harness = $exe; controlHarness = $controlExe; dataRoot = $dataRoot; controlDataRoot = $controlDataRoot; revision = $Revision; clubs = $Clubs; inventory = $Inventory; seconds = $Seconds; pool = $Pool
    targetRequestsPerPlayerSecond = $TargetRps; pairs = $Pairs; controlArgs = $ControlArgs; candidateArgs = $CandidateArgs
    method = 'Medians per arm. controlSpreadPct = (max - min) / median of control runs; a candidate change smaller than that spread is not distinguishable. Small unsaturated profile: not a capacity result.' }
foreach ($metric in $metrics) {
    if (@($results | Where-Object { $null -eq $_.$metric }).Count -gt 0) { $summary[$metric] = 'invalid: at least one run reported no valid value'; continue }
    $controlValues = [double[]]@($results | Where-Object arm -eq 'control' | ForEach-Object { $_.$metric })
    $candidateValues = [double[]]@($results | Where-Object arm -eq 'candidate' | ForEach-Object { $_.$metric })
    $controlMedian = Median $controlValues; $candidateMedian = Median $candidateValues
    $summary[$metric] = [ordered]@{
        control = [math]::Round($controlMedian, 3); candidate = [math]::Round($candidateMedian, 3)
        changePct = if ($controlMedian) { [math]::Round(100 * ($candidateMedian - $controlMedian) / $controlMedian, 1) } else { $null }
        controlSpreadPct = if ($controlMedian) { [math]::Round(100 * (($controlValues | Measure-Object -Maximum).Maximum - ($controlValues | Measure-Object -Minimum).Minimum) / $controlMedian, 1) } else { $null }
    }
}
ConvertTo-Json -InputObject $summary -Depth 4 | Set-Content -LiteralPath (Join-Path $screenRoot 'screen-summary.json') -Encoding UTF8
$results | Format-Table label, arm, pairRounds, requestsPerSecond, postgresCpuMsPerRequest, harnessCpuMsPerRequest, postgresPrivateMBMax -AutoSize | Out-String -Width 200 | Write-Host
foreach ($metric in $metrics) { if ($summary[$metric] -is [string]) { Write-Host "${metric}: $($summary[$metric])"; continue }; Write-Host ('{0}: control {1}, candidate {2}, change {3}%, control spread {4}%' -f $metric,
    $summary[$metric].control, $summary[$metric].candidate, $summary[$metric].changePct, $summary[$metric].controlSpreadPct) }
