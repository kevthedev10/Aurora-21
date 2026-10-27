$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Client-LeaderboardMods.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Aurora21 client mods ' + [Guid]::NewGuid().ToString('N'))
try {
    $bundled = Get-UltimateTeamLeaderboardModManifest
    if (@($bundled.languages).Count -ne 21 -or @($bundled.textEdits).Count -eq 0 -or
        @($bundled.textAdditions | Where-Object { $_.stringId -eq 'FUT_STORE_CAT_KITS_NAME' -and $_.target -eq 'TOKENS' }).Count -ne 1) {
        throw 'The fixed profile lost its languages or Aurora Tokens localisation.'
    }
    $game = Join-Path $fixture 'Game with spaces'
    New-Item -ItemType Directory -Path $game -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $game 'FIFA21.exe'), 'synthetic game')
    [IO.File]::WriteAllText((Join-Path $game 'CardsDLL_Win64_retail.dll'), 'synthetic cards')
    $revision = 'a' * 64
    $manifest = [pscustomobject]@{ schema = 1; game = 'FIFA21'; revision = $revision; profile = [pscustomobject]@{
        gameExecutableSha256 = (Get-FileHash (Join-Path $game 'FIFA21.exe')).Hash.ToLowerInvariant()
        cardsLibrarySha256 = (Get-FileHash (Join-Path $game 'CardsDLL_Win64_retail.dll')).Hash.ToLowerInvariant()
    } }
    $config = Join-Path $fixture 'Settings with spaces'
    $profile = Join-Path $config "client-mods\leaderboards\$revision"
    $modData = Join-Path $profile 'compiler\ModData'
    foreach ($folder in @('Data', 'Patch')) {
        New-Item -ItemType Directory -Path (Join-Path $modData $folder) -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $modData "$folder\layout.toc"), 'fixture layout')
    }
    $marker = @{ schema = 1; revision = $revision; gameDirectory = $game; verifiedLanguages = 21;
        gameExecutableSha256 = $manifest.profile.gameExecutableSha256; cardsLibrarySha256 = $manifest.profile.cardsLibrarySha256;
        modDataRelativePath = 'compiler/ModData' }
    $marker | ConvertTo-Json | Set-Content (Join-Path $profile 'compiled-profile.json') -Encoding UTF8
    $resolved = Get-UltimateTeamLeaderboardModPath -GamePath (Join-Path $game 'FIFA21.exe') -UserConfigRoot $config -Manifest $manifest -ToolRoot (Join-Path $fixture 'no tool installed')
    if ($resolved -ne $modData) { throw 'A complete cached profile was not reused.' }
    $manifest | Add-Member -NotePropertyName artwork -NotePropertyValue @(
        [pscustomobject]@{ name = 'data/ui/external/ion_fut/fixture.big'; member = '2' },
        [pscustomobject]@{ name = 'data/ui/external/ion_fut/fixture.big'; member = '3' }
    )
    $marker.verifiedArtworkAssets = 1
    $marker | ConvertTo-Json | Set-Content (Join-Path $profile 'compiled-profile.json') -Encoding UTF8
    $resolved = Get-UltimateTeamLeaderboardModPath -GamePath (Join-Path $game 'FIFA21.exe') -UserConfigRoot $config -Manifest $manifest -ToolRoot $fixture
    if ($resolved -ne $modData) { throw 'A verified artwork profile was not reused; members must count as one native asset.' }
    $marker.Remove('verifiedArtworkAssets')
    $marker | ConvertTo-Json | Set-Content (Join-Path $profile 'compiled-profile.json') -Encoding UTF8
    $refused = $false
    try { Get-UltimateTeamLeaderboardModPath -GamePath (Join-Path $game 'FIFA21.exe') -UserConfigRoot $config -Manifest $manifest -ToolRoot $fixture | Out-Null }
    catch { if ($_.Exception.Message -notmatch 'incomplete') { throw }; $refused = $true }
    if (-not $refused) { throw 'An artwork profile without compiled artwork verification was accepted.' }
    $manifest.artwork = @()
    $manifest.revision = 'b' * 64
    $refused = $false
    try { Get-UltimateTeamLeaderboardModPath -GamePath (Join-Path $game 'FIFA21.exe') -UserConfigRoot $config -Manifest $manifest -ToolRoot (Join-Path $fixture 'no tool installed') | Out-Null }
    catch { if ($_.Exception.Message -notmatch 'tools are missing') { throw }; $refused = $true }
    if (-not $refused -or -not (Test-Path (Join-Path $profile 'compiled-profile.json'))) { throw 'A changed revision must not reuse or discard the previous profile.' }
    $manifest.revision = $revision
    Remove-Item -LiteralPath (Join-Path $modData 'Patch\layout.toc')
    $refused = $false
    try { Get-UltimateTeamLeaderboardModPath -GamePath (Join-Path $game 'FIFA21.exe') -UserConfigRoot $config -Manifest $manifest -ToolRoot $fixture | Out-Null }
    catch { if ($_.Exception.Message -notmatch 'incomplete') { throw }; $refused = $true }
    if (-not $refused) { throw 'An incomplete profile was accepted.' }
    foreach ($file in @('Client-LeaderboardMods.ps1', 'Build-LeaderboardClientTool.ps1', 'Launch-UltimateTeam.ps1')) {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw "Cannot parse $file" }
    }
    $worker = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Launch-UltimateTeam.ps1'))
    if ($worker -notmatch "if \(\`$UpdateLeaderboardClientMods\) \{ \`$argList \+= '-UpdateLeaderboardClientMods' \}") { throw 'The automatic-mod option is not preserved over elevation.' }
    if (-not $worker.Contains('$clientManifest = Get-UltimateTeamLeaderboardModManifest') -or
        $worker.Contains('/panel/api/content/modes/leaderboards')) { throw 'Automatic mods must use the bundled profile without fetching leaderboard sizes.' }
    $gui = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'UltimateTeamLauncher.MainWindow.xaml.cs'))
    if (-not $gui.Contains('servicesOnly ? ["-NoGameLaunch"] : ["-UpdateLeaderboardClientMods"]')) { throw 'Normal launcher Play must enable combined client mods.' }
    $entrypoint = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'RUN-FIFA21.cmd'))
    if (-not $entrypoint.Contains('-File "%BUILDER%" -IfNeeded') -or -not $entrypoint.Contains('start "" "%LAUNCHER%"')) { throw 'RUN-FIFA21 must build updated resources and enter the mod-enabled launcher.' }
    Write-Host 'PASS: complete profile reused; artwork verification required and counts native assets; new revision refuses stale cache; prior profile preserved; incomplete profile rejected; fixed Top 25 profile includes Aurora Tokens; no leaderboard API fetch; scripts parse; elevation flag preserved.'
} finally { if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force } }
