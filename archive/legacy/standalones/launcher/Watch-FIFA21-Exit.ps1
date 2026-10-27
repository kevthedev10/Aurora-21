[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int]$FifaProcessId,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedGamePath,
    [string]$LauncherLogPath = ''
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
$RuntimeRoot = Join-Path $ProjectRoot 'runtime'
$StopScript = Join-Path $ProjectRoot 'Stop-LocalFUT.ps1'
$WatchLog = Join-Path $RuntimeRoot 'game-exit-watch.log'

function Write-WatchLog([string]$Message) {
    New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
    Add-Content -LiteralPath $WatchLog -Encoding utf8 -Value "$(Get-Date -Format o) $Message"
}

try {
    $expected = [IO.Path]::GetFullPath($ExpectedGamePath)
    $game = Get-Process -Id $FifaProcessId -ErrorAction SilentlyContinue
    if ($game) {
        $actual = $game.Path
        if (-not $actual -or -not [IO.Path]::GetFullPath($actual).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
            Write-WatchLog "Refused to monitor PID $FifaProcessId because its executable did not match '$expected'."
            exit 2
        }
        Write-WatchLog "Monitoring FIFA 21 PID $FifaProcessId at '$expected'."
        $game.WaitForExit()
    } else {
        Write-WatchLog "FIFA 21 PID $FifaProcessId had already exited before the watcher attached."
    }

    Write-WatchLog "FIFA 21 PID $FifaProcessId exited; stopping this launcher's tracked runtime."
    & $StopScript -LauncherLogPath $LauncherLogPath
    $stopCode = $LASTEXITCODE
    if ($null -eq $stopCode) { $stopCode = 0 }
    Write-WatchLog "Runtime stop completed with exit code $stopCode."
    exit $stopCode
} catch {
    Write-WatchLog "Automatic runtime stop failed: $($_.Exception.Message)"
    exit 1
}
