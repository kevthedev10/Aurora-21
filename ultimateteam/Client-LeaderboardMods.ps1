# Dot-sourcing only defines helpers. No services, game launch, routing or elevation.
function Get-UltimateTeamLeaderboardModManifest {
    $path = Join-Path $PSScriptRoot 'leaderboard-client-profile.json'
    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ($manifest.schema -ne 1 -or $manifest.game -ne 'FIFA21' -or $manifest.revision -cnotmatch '^[a-f0-9]{64}$' -or
        @($manifest.captions).Count -ne 2 -or @($manifest.captions | Where-Object { $_.top -ne 25 }).Count -ne 0 -or
        @($manifest.captions | Where-Object { $_.mode -eq 'champions' }).Count -ne 1 -or
        @($manifest.captions | Where-Object { $_.mode -eq 'squadBattles' }).Count -ne 1) {
        throw 'Invalid bundled Top 25 client profile.'
    }
    return $manifest
}

function Get-UltimateTeamLeaderboardModPath {
    param([string]$GamePath, [string]$UserConfigRoot, [object]$Manifest, [string]$ToolRoot)
    $gameDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($GamePath))
    if ($Manifest.schema -ne 1 -or $Manifest.game -ne 'FIFA21' -or $Manifest.revision -cnotmatch '^[a-f0-9]{64}$') {
        throw 'Invalid leaderboard client manifest.'
    }
    foreach ($entry in @(@('FIFA21.exe', 'gameExecutableSha256'), @('CardsDLL_Win64_retail.dll', 'cardsLibrarySha256'))) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $gameDirectory $entry[0]) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $Manifest.profile.($entry[1])) { throw "Unsupported game file for leaderboard mods: $($entry[0])." }
    }
    $cache = Join-Path $UserConfigRoot 'client-mods\leaderboards'
    $profile = Join-Path $cache $Manifest.revision
    $markerPath = Join-Path $profile 'compiled-profile.json'
    $artworkCount = @($Manifest.artwork | ForEach-Object { $_.name } | Select-Object -Unique).Count
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        if ($marker.schema -eq 1 -and $marker.revision -ceq $Manifest.revision -and $marker.verifiedLanguages -eq 21 -and
            $marker.gameDirectory -eq $gameDirectory -and $marker.gameExecutableSha256 -ceq $Manifest.profile.gameExecutableSha256 -and
            $marker.cardsLibrarySha256 -ceq $Manifest.profile.cardsLibrarySha256 -and $marker.modDataRelativePath -eq 'compiler/ModData' -and
            ($artworkCount -eq 0 -or $marker.verifiedArtworkAssets -eq $artworkCount)) {
            $modData = Join-Path $profile 'compiler\ModData'
            if ((Test-Path -LiteralPath (Join-Path $modData 'Data\layout.toc') -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $modData 'Patch\layout.toc') -PathType Leaf)) {
                Write-Host 'Leaderboards, Aurora Tokens and Aurora Champions: reusing verified client profile.'
                return $modData
            }
        }
        throw "Cached leaderboard profile is incomplete or belongs to another game installation: $profile"
    }
    if (Test-Path -LiteralPath $profile) { throw "Incomplete leaderboard profile: $profile" }
    $tool = Join-Path $ToolRoot 'Aurora21-LeaderboardUiTool.exe'
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw 'Leaderboard client tools are missing. Run Build-LeaderboardClientTool.ps1 before launching.'
    }
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $staging = Join-Path $cache ('.building-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $manifestPath = Join-Path $staging 'leaderboards.json'
    [IO.File]::WriteAllText($manifestPath, ($Manifest | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
    Write-Host 'Leaderboards, Aurora Tokens and Aurora Champions: preparing the combined client mod. The first build copies the game data.'
    & $tool $gameDirectory $manifestPath $staging '--compile' | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $staging 'compiled-profile.json') -PathType Leaf)) {
        throw "Leaderboard mod preparation failed. The previous profile is preserved. Diagnostic output: $staging"
    }
    [IO.Directory]::Move($staging, $profile)
    return Get-UltimateTeamLeaderboardModPath -GamePath $GamePath -UserConfigRoot $UserConfigRoot -Manifest $Manifest -ToolRoot $ToolRoot
}
