[CmdletBinding()]
param([string]$LauncherLogPath = '', [string]$UserConfigRoot = '')

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
. (Join-Path $ProjectRoot 'User-Settings.ps1')
$UserConfigRoot = Resolve-UltimateTeamUserConfigRoot $UserConfigRoot
$env:ULTIMATETEAM21_USER_CONFIG_ROOT = $UserConfigRoot
$RuntimeRoot = Join-Path $ProjectRoot 'runtime'
$PidPath = Join-Path $RuntimeRoot 'launcher-processes.json'
$ServerExecutable = Join-Path $ProjectRoot 'Aurora21-UltimateTeam-Server.exe'
$HostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
. (Join-Path $ProjectRoot 'Local-Routing.ps1')

function Test-ProcessOwnsServerCommand([object]$ProcessInfo, [string]$ExecutablePath, [string]$Command) {
    if (-not $ProcessInfo -or -not $ProcessInfo.ExecutablePath -or -not $ProcessInfo.CommandLine) { return $false }
    try {
        $expected = [IO.Path]::GetFullPath($ExecutablePath).Replace('/', '\')
        if (-not [string]::Equals(([string]$ProcessInfo.ExecutablePath).Replace('/', '\'), $expected, [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        $escaped = [regex]::Escape($expected)
        $commandLine = ([string]$ProcessInfo.CommandLine).Replace('/', '\')
        return $commandLine -match ('^\s*(?:"' + $escaped + '"|' + $escaped + ')\s+' + [regex]::Escape($Command) + '(?:\s|$)')
    } catch {
        return $false
    }
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ProcessOwnsLaunchWorker([object]$ProcessInfo, [string]$ScriptPath) {
    if (-not $ProcessInfo.ExecutablePath -or -not $ProcessInfo.CommandLine) { return $false }
    $shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not [string]::Equals([string]$ProcessInfo.ExecutablePath, $shell, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    $script = [regex]::Escape([IO.Path]::GetFullPath($ScriptPath))
    return $ProcessInfo.CommandLine -match ('(?i)(?:^|\s)-File\s+(?:"' + $script + '"|' + $script + ')(?:\s|$)')
}

if (-not (Test-Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    $arguments += @('-UserConfigRoot', ('"{0}"' -f $UserConfigRoot))
    if ($LauncherLogPath) { $arguments += @('-LauncherLogPath', ('"{0}"' -f $LauncherLogPath)) }
    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    exit $elevated.ExitCode
}

$transcribing = $false
try {
if ($LauncherLogPath) { Start-Transcript -LiteralPath $LauncherLogPath -Append -Force | Out-Null; $transcribing = $true }
# Cancel this checkout's launch workers before stopping services. Otherwise a
# worker waiting for FIFA/EA App can continue launching after Stop completes.
$launchScript = Join-Path $ProjectRoot 'Launch-UltimateTeam.ps1'
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
    Test-ProcessOwnsLaunchWorker $_ $launchScript
} | ForEach-Object {
    $workerId = $_.ProcessId
    $clientCompiler = Join-Path $ProjectRoot 'client-mod-tools\Aurora21-LeaderboardUiTool.exe'
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$workerId" | Where-Object {
        $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $clientCompiler, [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object {
        & taskkill.exe /PID $_.ProcessId /T /F | Out-Null
        Write-Host "Cancelled client mod preparation $($_.ProcessId)."
    }
    Stop-Process -Id $workerId -Force -ErrorAction SilentlyContinue
    Write-Host "Cancelled launch worker $($_.ProcessId)."
}
if (Test-Path -LiteralPath $PidPath) {
    $tracked = Get-Content -LiteralPath $PidPath -Raw | ConvertFrom-Json
    $trackedCommands = @(
        [pscustomobject]@{ Name = 'serverPid'; Command = 'serve' },
        [pscustomobject]@{ Name = 'redirectorPid'; Command = 'redirector' }
    )
    foreach ($trackedCommand in $trackedCommands) {
        $id = [int]$tracked.($trackedCommand.Name)
        $processInfo = if ($id -gt 0) { Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue } else { $null }
        if ($processInfo -and (Test-ProcessOwnsServerCommand $processInfo $ServerExecutable $trackedCommand.Command)) {
            Stop-Process -Id $id -Force
            Write-Host "Stopped tracked $($trackedCommand.Name) process $id."
        }
    }
    Remove-Item -LiteralPath $PidPath -Force
} else {
    Write-Host "No tracked launcher processes were found; checking for this launcher's orphaned local-runtime processes."
}

# A failed start can occur after the services are created but before their PIDs
# are saved. Restrict fallback cleanup to this checkout's exact server executable
# running one of its two long-lived commands.
Get-CimInstance Win32_Process -Filter "Name='Aurora21-UltimateTeam-Server.exe'" | ForEach-Object {
    $processInfo = $_
    $owned = @('serve', 'redirector') | Where-Object { Test-ProcessOwnsServerCommand $processInfo $ServerExecutable $_ }
    if ($owned) {
        Stop-Process -Id $processInfo.ProcessId -Force
        Write-Host "Stopped orphaned local-runtime process $($processInfo.ProcessId)."
    }
}

# The server is stopped forcibly above, so it cannot shut its club database down itself. This
# stops only the managed cluster in this user's runtime folder (pg_ctl -D), never another PostgreSQL.
if (Test-Path -LiteralPath $ServerExecutable -PathType Leaf) {
    $databaseState = & $ServerExecutable postgres stop --data-root (Join-Path $ProjectRoot 'runtime-source\ultimateteam21') 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Local club database: $databaseState"
    } else {
        Write-Host "WARNING: could not stop the local club database: $databaseState"
    }
}

Remove-RedirectHostsEntry
# Refresh the status consumed by the launcher; old READY state must not survive Stop.
$statusPath = Join-Path $RuntimeRoot 'status.json'
if (Test-Path -LiteralPath $statusPath) {
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    $status.server = 'STOPPED'
    $status.connection = 'STOPPED'
    $status.updatedAt = (Get-Date).ToString('o')
    $status | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $statusPath -Encoding UTF8
}
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
}
