[CmdletBinding()]
param()

# Offline validation of the C# runtime: the bundled catalogs plus the in-process API test suite
# (a port of the original test_offline_api.py). It uses temporary state roots only, never the
# real club, and starts no listener, game, TLS, hosts or trust operation.
$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
$DataRoot = Join-Path $ProjectRoot 'runtime-source\ultimateteam21'
$TestProject = Join-Path $ProjectRoot 'server\tests\Aurora21.UltimateTeam.Tests\Aurora21.UltimateTeam.Tests.csproj'
$RuntimeRoot = Join-Path $ProjectRoot 'runtime'
$StatusPath = Join-Path $RuntimeRoot 'offline-status.json'
$LogPath = Join-Path $RuntimeRoot 'offline-launcher.log'

New-Item -ItemType Directory -Force -Path $RuntimeRoot | Out-Null

function Write-Log([string]$Message, [ConsoleColor]$Color = [ConsoleColor]::White) {
    $line = "[{0}] {1}" -f (Get-Date).ToString('s'), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
    Write-Host $line -ForegroundColor $Color
}

function Save-Status([object]$Status) {
    $Status.updatedAt = (Get-Date).ToString('o')
    $Status | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StatusPath -Encoding utf8
}

$status = [ordered]@{
    mode = 'OFFLINE_LOCAL_DATA_VALIDATION'
    catalogs = 'NOT STARTED'
    apiHealth = 'NOT STARTED'
    localServices = 'NOT STARTED (no listener is used)'
    final = 'FAILED'
    detail = @()
}

try {
    Set-Content -LiteralPath $LogPath -Value '' -Encoding utf8
    Write-Log 'Standalone offline local FUT validation started.' Cyan

    $required = @('config.example.json', 'players.json', 'managers.json', 'kits.json', 'stadiums.json', 'sbc.json') +
        @('cards', 'club', 'draft', 'market', 'objectives', 'onboarding', 'packs', 'sbc', 'seasons', 'squad_battles' | ForEach-Object { "rules\$_.json" })
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $DataRoot $_) -PathType Leaf) })
    if ($missing) { throw "Missing required offline files: $($missing -join ', ')" }
    if (-not (Test-Path -LiteralPath $TestProject -PathType Leaf)) { throw "Missing test project: $TestProject" }
    $status.catalogs = 'PASS (bundled files present)'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw 'The .NET SDK (dotnet) is required to run the offline test suite.' }

    # The tests run their own throwaway PostgreSQL cluster from the portable binaries.
    $postgres = Join-Path $ProjectRoot 'postgres\win-x64\bin\pg_ctl.exe'
    if (-not $env:ULTIMATETEAM_POSTGRES_ROOT -and -not (Test-Path -LiteralPath $postgres -PathType Leaf)) {
        $server = Join-Path $ProjectRoot 'Aurora21-UltimateTeam-Server.exe'
        if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
            throw 'PostgreSQL binaries are missing. Run Build-FIFA21-Launcher.ps1 first.'
        }
        Write-Log 'Installing the portable PostgreSQL build used by the tests ...' Cyan
        & $server postgres install
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL install failed with exit code $LASTEXITCODE." }
    }

    Write-Log 'Running the offline API/catalog/protocol test suite ...' Cyan
    $priorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $dotnet.Source test $TestProject --nologo 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $priorPreference
    }
    foreach ($line in $output) { Add-Content -LiteralPath $LogPath -Value ([string]$line) -Encoding utf8 }
    $output | Select-Object -Last 15 | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "Offline test suite failed with exit code $exitCode." }
    $status.apiHealth = 'PASS (in-process; isolated temporary club)'
    $status.final = 'OFFLINE LOCAL FUT READY'
    Save-Status $status

    Write-Host ''
    Write-Host 'OFFLINE LOCAL FUT STATUS' -ForegroundColor Cyan
    Write-Host 'Catalogs:           PASS' -ForegroundColor Green
    Write-Host 'API health:         PASS (in-process)' -ForegroundColor Green
    Write-Host 'Final status:       OFFLINE LOCAL FUT READY' -ForegroundColor Green
    exit 0
} catch {
    $status.final = 'FAILED'
    $status.detail += $_.Exception.Message
    Save-Status $status
    Write-Log ("ERROR: " + $_.Exception.Message) Red
    exit 1
}
