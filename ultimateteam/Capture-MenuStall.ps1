[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerIp,
    [ValidateRange(1, 30)][int]$Minutes = 10,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot '..\outputs\menu-stall'
}
$address = $null
if (-not [Net.IPAddress]::TryParse($ServerIp, [ref]$address) -or
    $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'ServerIp must be the hosted server IPv4 address.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from Administrator PowerShell. Packet Monitor requires elevation; this script does not trigger UAC.'
}
$pktmon = (Get-Command pktmon.exe -ErrorAction Stop).Source
$game = @(Get-CimInstance Win32_Process -Filter "Name='FIFA21.exe'")
if ($game.Count -ne 1 -or -not $game[0].ExecutablePath) {
    throw 'Exactly one running FIFA21.exe with a readable executable path is required.'
}
$gameId = [int]$game[0].ProcessId
$gameStarted = (Get-Process -Id $gameId).StartTime.ToUniversalTime()
$root = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root | Out-Null
$toolLog = Join-Path $root 'capture-tool.log'

function Invoke-PacketMonitor([string[]]$Arguments) {
    $lines = @(& $pktmon @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    Add-Content -LiteralPath $toolLog -Value ($lines -join "`r`n")
    if ($exitCode -ne 0) { throw "Packet Monitor failed ($exitCode): $($lines -join ' ')" }
    return ($lines -join "`n")
}

# Fail closed on an existing capture/filter set. PktMon is machine-wide; don't overwrite another diagnostic.
$status = Invoke-PacketMonitor -Arguments @('status')
$filters = Invoke-PacketMonitor -Arguments @('filter', 'list')
if ($status -notmatch '(?i)(not running|stopped|not active)' -or $filters -notmatch '(?i)(no (packet )?filters|none)') {
    throw "Packet Monitor must be stopped with no existing filters. No capture settings were changed. Status: $status Filters: $filters"
}

Add-Type -Path (Join-Path $PSScriptRoot 'diagnostics\menu-stall\TraceReader.cs')
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class Aurora21MenuKeys {
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
}
'@
$startedUtc = [DateTime]::UtcNow
@{ version = 1; startedUtc = $startedUtc.ToString('o'); serverIp = $ServerIp;
    gamePid = $gameId; gamePath = $game[0].ExecutablePath; gameStartedUtc = $gameStarted.ToString('o');
    minutes = $Minutes; timeZone = [TimeZoneInfo]::Local.Id;
    ports = @(18443, 18444, 18445, 18446, 42230); packetSnapshotBytes = 96
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'metadata.json') -Encoding UTF8

$job = $null
$captureStarted = $false
$addedFilters = @()
$ownedFilterState = $null
$filterPrefix = 'AuroraMenu-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$markers = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::StartNew()
$action = 0
$openAction = 0
$lastDown = @{}
$failure = $null
$etl = Join-Path $root 'packets.etl'
try {
    foreach ($port in @(18443, 18444, 18445, 18446, 42230)) {
        $name = "$filterPrefix-$port"
        Invoke-PacketMonitor -Arguments @('filter', 'add', $name, '-i', $ServerIp, '-t', 'TCP', '-p', [string]$port) | Out-Null
        $addedFilters += $name
        $ownedFilterState = Invoke-PacketMonitor -Arguments @('filter', 'list')
    }
    Invoke-PacketMonitor -Arguments @('start', '--capture', '--comp', 'nics', '--pkt-size', '96',
        '--file-name', $etl, '--file-size', '128', '--log-mode', 'circular') | Out-Null
    $captureStarted = $true
    $job = Start-Job -ArgumentList $gameId, $gameStarted, $root -ScriptBlock {
        param($gameId, $gameStarted, $root)
        $ErrorActionPreference = 'Stop'
        $previousCpu = $null
        $previousAt = $null
        while ($true) {
            $now = [DateTime]::UtcNow
            $p = Get-Process -Id $gameId -ErrorAction SilentlyContinue
            if (-not $p -or $p.StartTime.ToUniversalTime() -ne $gameStarted) { break }
            $cpu = $p.TotalProcessorTime.TotalMilliseconds
            $cpuMs = if ($null -eq $previousCpu) { 0 } else { $cpu - $previousCpu }
            $interval = if ($null -eq $previousAt) { 0 } else { ($now - $previousAt).TotalMilliseconds }
            [pscustomobject]@{ Utc = $now.ToString('o'); CpuMs = $cpuMs; SampleMs = $interval;
                ThreadCount = $p.Threads.Count; WorkingSetBytes = $p.WorkingSet64
            } | Export-Csv -LiteralPath (Join-Path $root 'process.csv') -NoTypeInformation -Append
            $previousCpu = $cpu
            $previousAt = $now
            # Record ALL FIFA TCP destinations: an unrelated EA endpoint could also block the game.
            @(Get-NetTCPConnection -OwningProcess $gameId -ErrorAction SilentlyContinue) | ForEach-Object {
                [pscustomobject]@{ Utc = $now.ToString('o'); LocalAddress = $_.LocalAddress; LocalPort = $_.LocalPort;
                    RemoteAddress = $_.RemoteAddress; RemotePort = $_.RemotePort; State = [string]$_.State }
            } | Export-Csv -LiteralPath (Join-Path $root 'sockets.csv') -NoTypeInformation -Append
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Host ''
    Write-Host 'Capture running. Return to FIFA; you do not need to keep this window focused.'
    Write-Host 'F8: mark START just before the slow menu action.'
    Write-Host 'F9: mark FINISHED when the screen appears.'
    Write-Host 'F10: stop and generate the bundle. Automatic stop after the selected duration.'
    Write-Host 'Record a quick action too for comparison. Only F8/F9/F10 are observed; no other keys are recorded.'
    Write-Host "Output: $root"
    while ($watch.Elapsed.TotalMinutes -lt $Minutes) {
        if ($job.State -in @('Failed', 'Completed', 'Stopped')) {
            throw 'The FIFA socket sampler stopped; check sampler-errors.log in the bundle.'
        }
        $stop = $false
        foreach ($key in @(119, 120, 121)) {
            $down = ([Aurora21MenuKeys]::GetAsyncKeyState($key) -band 0x8000) -ne 0
            if ($down -and -not $lastDown[$key]) {
                $kind = $null
                if ($key -eq 119 -and $openAction -eq 0) { $action++; $openAction = $action; $kind = 'start' }
                elseif ($key -eq 120 -and $openAction -ne 0) { $kind = 'finished' }
                elseif ($key -eq 121) { $stop = $true }
                if ($kind) {
                    $marker = [pscustomobject]@{ Utc = [DateTime]::UtcNow.ToString('o'); ElapsedMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 1);
                        Action = $openAction; Event = $kind }
                    $markers.Add($marker)
                    $marker | Export-Csv -LiteralPath (Join-Path $root 'markers.csv') -NoTypeInformation -Append
                    Write-Host "Action $openAction $kind at $($marker.Utc)"
                    if ($kind -eq 'finished') { $openAction = 0 }
                }
            }
            $lastDown[$key] = $down
        }
        if ($stop) { break }
        Start-Sleep -Milliseconds 50
    }
}
catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    if ($captureStarted) {
        try {
            $currentStatus = Invoke-PacketMonitor -Arguments @('status')
            if ($currentStatus.IndexOf($etl, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw 'The active Packet Monitor log no longer matches this capture. It was not stopped.'
            }
            Invoke-PacketMonitor -Arguments @('stop') | Out-Null
        }
        catch { $failure = "Capture stop failed: $($_.Exception.Message)"; Write-Warning $failure }
    }
    if ($job) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        $job.ChildJobs | ForEach-Object { $_.Error | Out-String } | Set-Content -LiteralPath (Join-Path $root 'sampler-errors.log')
        Remove-Job -Job $job
    }
    if ($addedFilters.Count -gt 0) {
        try {
            $current = Invoke-PacketMonitor -Arguments @('filter', 'list')
            # Never remove filters another diagnostic added during this capture.
            if ($null -ne $ownedFilterState -and [string]::Equals($current.Trim(), $ownedFilterState.Trim(), [StringComparison]::Ordinal)) {
                Invoke-PacketMonitor -Arguments @('filter', 'remove') | Out-Null
            } else { Write-Warning 'Filter ownership could not be confirmed. Filters were left in place; see capture-tool.log.' }
        } catch { Write-Warning "Could not clean up this capture's filters: $($_.Exception.Message)" }
    }
}

$report = [Collections.Generic.List[string]]::new()
$report.Add('Aurora 21 menu stall timing report')
$report.Add("Server: $ServerIp | Started UTC: $($startedUtc.ToString('o'))")
$report.Add('Markers are approximate human timing. PC time is used throughout; UTC avoids VPS/Windows timezone differences.')
$report.Add('TCP acknowledgement means receipt by the TCP stack, not consumption by FIFA. TLS request paths are not decoded.')
$report.Add('Repeated data sequences can be retransmissions or NIC capture duplicates; they are not automatically classified as packet loss.')
$report.Add('Only the configured hosted game ports are packet-captured. sockets.csv also lists other FIFA TCP endpoints.')
if ($failure) { $report.Add("INCOMPLETE: $failure") }
try {
    if (-not $captureStarted -or -not (Test-Path -LiteralPath $etl)) { throw 'No packet capture was produced.' }
    $pcap = Join-Path $root 'packets.pcapng'
    Invoke-PacketMonitor -Arguments @('etl2pcap', $etl, '--out', $pcap) | Out-Null
    $packets = @([Aurora21.MenuDiagnostics.TraceReader]::Read($pcap, $ServerIp))
    $packets | Select-Object @{n='Utc'; e={$_.Utc.ToString('o')}}, Direction, LocalAddress, LocalPort, ServerPort,
        Sequence, Acknowledgement, PayloadBytes, Window, Flags, RepeatedData |
        Export-Csv -LiteralPath (Join-Path $root 'packets.csv') -NoTypeInformation
    $report.Add("Parsed TCP packets: $($packets.Count)")
    foreach ($begin in @($markers | Where-Object Event -eq 'start')) {
        $finish = $markers | Where-Object { $_.Action -eq $begin.Action -and $_.Event -eq 'finished' } | Select-Object -First 1
        $from = [DateTime]::Parse($begin.Utc).ToUniversalTime()
        $to = if ($finish) { [DateTime]::Parse($finish.Utc).ToUniversalTime() } else { [DateTime]::UtcNow }
        $window = @($packets | Where-Object { $_.Utc -ge $from -and $_.Utc -le $to })
        $report.Add('')
        $report.Add("ACTION $($begin.Action): $($begin.Utc) -> $($to.ToString('o')) | duration $([math]::Round(($to-$from).TotalSeconds,2)) s | finished=$([bool]$finish)")
        foreach ($port in @(18443, 18444, 18445, 18446, 42230)) {
            $traffic = @($window | Where-Object ServerPort -eq $port)
            if ($traffic.Count -eq 0) { continue }
            $payloadTimes = @($from) + @($traffic | Where-Object PayloadBytes -gt 0 | ForEach-Object { $_.Utc }) + @($to)
            $longestGap = 0.0
            $gapStart = $from
            $gapEnd = $from
            for ($i = 1; $i -lt $payloadTimes.Count; $i++) {
                $gap = ($payloadTimes[$i] - $payloadTimes[$i - 1]).TotalSeconds
                if ($gap -gt $longestGap) { $longestGap = $gap; $gapStart = $payloadTimes[$i - 1]; $gapEnd = $payloadTimes[$i] }
            }
            $report.Add("  Port $port longest interval without captured payload: $([math]::Round($longestGap,3)) s (+$([math]::Round(($gapStart-$from).TotalSeconds,3)) -> +$([math]::Round(($gapEnd-$from).TotalSeconds,3)) s)")
            foreach ($direction in @('PC->server', 'server->PC')) {
                $data = @($traffic | Where-Object { $_.Direction -eq $direction -and $_.PayloadBytes -gt 0 })
                if ($data.Count -gt 0) {
                    $report.Add("  Port $port $direction first data +$([math]::Round(($data[0].Utc-$from).TotalSeconds,3)) s; last +$([math]::Round(($data[-1].Utc-$from).TotalSeconds,3)) s; repeated sequences=$(@($data | Where-Object RepeatedData).Count)")
                }
            }
            foreach ($packet in @($traffic | Where-Object { $_.Flags -match 'SYN|FIN|RST' })) {
                $report.Add("  +$([math]::Round(($packet.Utc-$from).TotalSeconds,3)) s: $($packet.Direction) local port $($packet.LocalPort) server port $port $($packet.Flags)")
            }
        }
        if ($window.Count -eq 0) { $report.Add('  No packets observed to/from the captured server ports during this action.') }
    }
    if ($markers.Count -eq 0) { $report.Add('No action markers recorded. The packet/socket timelines can still be inspected, but click-to-screen timing is unavailable.') }
}
catch { $failure = "Packet export/analysis failed: $($_.Exception.Message)"; $report.Add($failure); Write-Warning $failure }
$report.Add('')
$report.Add('Share menu-stall-report.zip. Raw ETL/pcapng are retained locally and intentionally excluded from that zip.')
$report | Set-Content -LiteralPath (Join-Path $root 'report.txt') -Encoding UTF8
$share = @('report.txt', 'metadata.json', 'markers.csv', 'sockets.csv', 'process.csv', 'packets.csv', 'capture-tool.log', 'sampler-errors.log') |
    ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ }
$zip = Join-Path $root 'menu-stall-report.zip'
Compress-Archive -LiteralPath $share -DestinationPath $zip
Write-Host "Capture finished. Share: $zip"
if ($failure) { throw "Capture incomplete: $failure. The partial bundle was retained." }
