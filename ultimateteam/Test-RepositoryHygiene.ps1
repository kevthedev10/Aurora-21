[CmdletBinding()]
param()

# Read-only: checks ignore rules and the Git index; never launches the game.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$script:checks = 0

function Assert-Hygiene($Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
}

try {
    $artifacts = @(
        'ultimateteam/Aurora21-UltimateTeam-Launcher.exe',
        'ultimateteam/Aurora21-LocalAuthBridge.exe',
        'ultimateteam/Aurora21-MathBridge.exe'
    )
    # The client pack's one Microsoft DLL: the pinned ucrtbase.dll the maths bridge loads into FIFA.
    $packLibraries = @('ultimateteam/client-pack/ucrt/ucrtbase.dll')
    $required = $artifacts + $packLibraries + @(
        'ultimateteam/FIFA21-UltimateTeam-Launcher.cs', 'ultimateteam/FIFA21-LocalAuthBridge.cs',
        'ultimateteam/FIFA21-LocalMathBridge.cs', 'ultimateteam/tests/MathBridgeHarness/MathBridgeHarness.cs',
        'ultimateteam/client-pack/Start-Aurora21.ps1', 'ultimateteam/client-pack/runtime.json',
        'ultimateteam/client-pack/THIRD-PARTY-NOTICES.txt',
        'ultimateteam/LauncherSettings.cs', 'ultimateteam/LauncherTheme.cs',
        'ultimateteam/UltimateTeamLauncher.csproj', 'ultimateteam/UltimateTeamLauncher.App.xaml',
        'ultimateteam/UltimateTeamLauncher.App.xaml.cs', 'ultimateteam/UltimateTeamLauncher.MainWindow.xaml',
        'ultimateteam/UltimateTeamLauncher.MainWindow.xaml.cs', 'ultimateteam/UltimateTeamLauncher.RuntimeCompatibility.cs',
        'ultimateteam/Build-FIFA21-Launcher.ps1', 'ultimateteam/Launch-UltimateTeam.ps1',
        'ultimateteam/RUN-FIFA21.cmd', 'ultimateteam/User-Settings.ps1',
        'ultimateteam/launcher-settings.example.json',
        'ultimateteam/runtime-source/ultimateteam21/config.example.json',
        'ultimateteam/runtime-source/ultimateteam21/store_packs.json',
        'ultimateteam/runtime-source/ultimateteam21/players.json',
        'ultimateteam/runtime-source/gos2015-original-modulus.bin',
        'ultimateteam/runtime-source/gs2019-original-modulus.bin',
        'ultimateteam/runtime/mined/tables/cards_ng_db/players.json',
        'ultimateteam/runtime/mined/tables/fifa_ng_db/players.json',
        'ultimateteam/native-data/player-catalog-manifest.json',
        'archive/README.md',
        'ultimateteam/server/Aurora21.UltimateTeam.sln',
        'ultimateteam/server/src/Aurora21.UltimateTeam.Server/Aurora21.UltimateTeam.Server.csproj',
        'ultimateteam/server/tests/Aurora21.UltimateTeam.Tests/Aurora21.UltimateTeam.Tests.csproj'
    )
    $required += @('cards', 'club', 'draft', 'market', 'objectives', 'onboarding', 'packs', 'sbc', 'seasons', 'squad_battles',
        'rivals_rewards', 'champions_rewards', 'sqbt_rewards', 'sqbt_totw', 'staff', 'pack_odds', 'tradeability' |
        ForEach-Object { "ultimateteam/runtime-source/ultimateteam21/rules/$_.json" })
    $required += @('champions', 'rivals', 'squad_battles', 'draft' | ForEach-Object { "ultimateteam/runtime-source/ultimateteam21/rewards/$_.json" })
    $required += @('play_arrow', 'stop', 'settings', 'folder_open', 'description', 'lock' |
        ForEach-Object { "ultimateteam/assets/material/$_.svg" })
    $ignored = @(
        'ultimateteam/runtime/server.log', 'ultimateteam/runtime/server.log.1',
        'ultimateteam/runtime/status.json', 'ultimateteam/runtime/offline-status.json',
        'ultimateteam/runtime/launcher-processes.json', 'ultimateteam/runtime/server.pid',
        'ultimateteam/runtime/captures/session.log', 'ultimateteam/runtime/captures/frame.bin',
        'ultimateteam/runtime-source/ultimateteam21/lsx_capture.log',
        'ultimateteam/runtime-source/ultimateteam21/__pycache__/server.cpython-314.pyc',
        'ultimateteam/launcher-settings.json',
        'ultimateteam/runtime-source/ultimateteam21/config.json',
        'ultimateteam/bin/Release/net10.0-windows/Aurora21-UltimateTeam-Launcher.dll', 'ultimateteam/obj/project.assets.json',
        'ultimateteam/ultimateteam.sqlite3', 'ultimateteam/ultimateteam.sqlite3-wal',
        'ultimateteam/ultimateteam.sqlite3-shm', 'FIFA21.exe', 'ultimateteam/FIFA21.exe',
        'ultimateteam/other-helper.exe', 'ultimateteam/game.dll',
        # Only the pinned ucrtbase.dll: no forwarders, other runtimes or copies elsewhere in the pack.
        'ultimateteam/client-pack/ucrt/api-ms-win-crt-math-l1-1-0.dll', 'ultimateteam/client-pack/ucrt/vcruntime140.dll',
        'ultimateteam/client-pack/ucrtbase.dll', 'ultimateteam/client-pack/Aurora21-MathBridge.exe',
        # The C# server is a build output (self-contained, published by Build-FIFA21-Launcher.ps1).
        'ultimateteam/Aurora21-UltimateTeam-Server.exe', 'ultimateteam/club.sqlite3', 'ultimateteam/club.sqlite3-wal',
        'ultimateteam/postgres/win-x64/bin/postgres.exe', 'ultimateteam/postgres/win-x64/VERSION',
        'ultimateteam/postgres/win-x64/share/postgresql.conf.sample',
        'ultimateteam/server/publish/win-x64/Aurora21-UltimateTeam-Server.exe',
        'ultimateteam/server/src/Aurora21.UltimateTeam.Server/bin/Debug/net10.0/Aurora21-UltimateTeam-Server.dll',
        'ultimateteam/server/src/Aurora21.UltimateTeam.Core/obj/project.assets.json',
        # The new panel's npm dependencies and scratch output stay local; its build in Resources/panel is committed.
        'ultimateteam/panel-web/node_modules/vite/package.json', 'ultimateteam/panel-web/dist/index.html',
        'ultimateteam/panel-web/coverage/index.html',
        'ultimateteam/deploy/restart-requests/restart-server.request'
    )
    $generatedCertificates = @(
        'ultimateteam/runtime-source/local-gos2015-modulus.bin',
        'ultimateteam/runtime-source/local-gs2019-modulus.bin',
        'ultimateteam/runtime-source/certs/local-gos2015-ca-key.pem',
        'ultimateteam/runtime-source/certs/local-gos2015-ca-cert.pem',
        'ultimateteam/runtime-source/certs/local-gs2019-ca-key.pem',
        'ultimateteam/runtime-source/certs/local-gs2019-ca-cert.pem',
        'ultimateteam/runtime-source/certs/spring18-key.pem',
        'ultimateteam/runtime-source/certs/spring18-cert.pem',
        'ultimateteam/runtime-source/certs/spring18-chain.pem',
        'ultimateteam/runtime-source/certs/spring18-gs2019-key.pem',
        'ultimateteam/runtime-source/certs/spring18-gs2019-cert.pem',
        'ultimateteam/runtime-source/certs/spring18-gs2019-chain.pem'
    )
    $ignored += $generatedCertificates
    foreach ($path in $ignored + $required) {
        & git -C $repoRoot check-ignore --no-index --quiet -- $path
        $result = $LASTEXITCODE
        Assert-Hygiene ($result -in @(0, 1)) "Git ignore check failed: $path"
        Assert-Hygiene (($result -eq 0) -eq ($path -in $ignored)) "Wrong ignore policy: $path"
    }
    $tracked = @(& git -C $repoRoot ls-files)
    Assert-Hygiene ($LASTEXITCODE -eq 0) 'Cannot read Git index'
    foreach ($path in $required) {
        Assert-Hygiene ($path -in $tracked) "Required shared file is not staged/tracked: $path"
        Assert-Hygiene (Test-Path -LiteralPath (Join-Path $repoRoot $path) -PathType Leaf) "Missing local file: $path"
    }
    $unwanted = @($tracked | Where-Object {
        $_ -match '(?i)(\.log(?:\..*)?$|\.jsonl$|\.pid$|\.py[cod]$|\.sqlite3(?:-wal|-shm)?$|/__pycache__/)' -or
        $_ -match '(?i)(?:^|/)[^/]*(?:-key\.pem|\.key|\.pfx)$' -or
        $_ -like 'ultimateteam/runtime-source/certs/*' -or
        $_ -like 'ultimateteam/runtime/captures/*' -or $_ -in $ignored
    })
    Assert-Hygiene ($unwanted.Count -eq 0) "Generated files still tracked: $($unwanted -join ', ')"
    $unexpectedExe = @($tracked | Where-Object { $_ -like '*.exe' -and $_ -notin $artifacts })
    Assert-Hygiene ($unexpectedExe.Count -eq 0) "Unexpected EXEs tracked: $($unexpectedExe -join ', ')"
    $unexpectedPackDll = @($tracked | Where-Object { $_ -like 'ultimateteam/client-pack/*' -and $_ -like '*.dll' -and $_ -notin $packLibraries })
    Assert-Hygiene ($unexpectedPackDll.Count -eq 0) "Unexpected DLLs tracked in the client pack: $($unexpectedPackDll -join ', ')"
    Write-Host "PASS: $script:checks repository hygiene checks; shared data/defaults, exactly three helper artifacts and the client pack's one pinned DLL are tracked."
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
