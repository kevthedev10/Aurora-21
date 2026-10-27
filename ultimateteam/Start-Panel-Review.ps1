# Start an isolated server and its embedded React panel for manual review.
# Run with Windows PowerShell; no game or browser is launched.
[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$InfoPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($InfoPath)) {
    $InfoPath = Join-Path ([IO.Path]::GetTempPath()) 'aurora-panel-review-info.json'
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$reviewRoot = Join-Path ([IO.Path]::GetTempPath()) ('Aurora21-panel-review-' + [Guid]::NewGuid().ToString('N'))
$reviewConfig = Join-Path $reviewRoot 'config'
New-Item -ItemType Directory -Path $reviewConfig -Force | Out-Null
$project = Join-Path $ProjectRoot 'ultimateteam/server/src/Aurora21.UltimateTeam.Server/Aurora21.UltimateTeam.Server.csproj'
$buildLog = Join-Path $reviewRoot 'build.log'
& dotnet.exe build $project --no-restore *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content $buildLog -Tail 30; throw 'Server build failed' }
$server = Join-Path $ProjectRoot 'ultimateteam/server/src/Aurora21.UltimateTeam.Server/bin/Debug/net10.0/Aurora21-UltimateTeam-Server.exe'
$data = Join-Path $ProjectRoot 'ultimateteam/runtime-source/ultimateteam21'
$env:ULTIMATETEAM_RUNTIME_ROOT = $reviewRoot
$env:ULTIMATETEAM21_USER_CONFIG_ROOT = $reviewConfig
$env:ULTIMATETEAM_POSTGRES_CONNECTION = $null
$env:ULTIMATETEAM_POSTGRES_ROOT = Join-Path $ProjectRoot 'ultimateteam/postgres'
$env:ULTIMATETEAM_DATA_ROOT = $data
$env:ULTIMATETEAM_DISCORD_BOT_TOKEN = $null
$env:ULTIMATETEAM21_MARKET_TARGET = '0'
$preferences = Get-Content (Join-Path $data 'config.example.json') -Raw | ConvertFrom-Json
$preferences.display_name = 'Panel Review'
$preferences.club_name = 'Review FC'
$preferences | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $reviewConfig 'config.json') -Encoding UTF8
$arguments = @('serve', '--http-only-test-port', '18549', '--panel-api-port', '18448', '--lsx-mode', 'off', '--data-root', ('"' + $data + '"'))
$assets = Join-Path $ProjectRoot 'ultimateteam/panel-assets'
if (Test-Path $assets -PathType Container) { $arguments += @('--panel-assets-dir', ('"' + $assets + '"')) }
$process = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $ProjectRoot -PassThru -RedirectStandardOutput (Join-Path $reviewRoot 'stdout.log') -RedirectStandardError (Join-Path $reviewRoot 'stderr.log')
$info = [ordered]@{ processId = $process.Id; executable = $server; runtimeRoot = $reviewRoot; configRoot = $reviewConfig; panelPort = 18448; testPort = 18549; assets = $assets; buildSha256 = (Get-FileHash $server -Algorithm SHA256).Hash }
$info | ConvertTo-Json | Set-Content $InfoPath -Encoding UTF8
$info | ConvertTo-Json -Compress
Write-Host 'Panel URL (once startup finishes): http://127.0.0.1:18448/panel/'
Write-Host "Test runtime and logs: $reviewRoot"
Write-Host "Process information: $InfoPath"
