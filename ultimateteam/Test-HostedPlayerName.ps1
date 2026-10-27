# Fixture-only checks for the hosted direct-copy Username sync. No game, UAC, hosts or profile is touched.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'client-pack\Start-Aurora21.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'The hosted start script does not parse.' }
foreach ($name in @('Take-HostedDiscordUsername', 'Sync-DirectGameUsername')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing hosted name function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Assert-True([bool]$Condition, [string]$Reason) {
    if (-not $Condition) { throw $Reason }
}
function Assert-Bytes([byte[]]$Actual, [byte[]]$Expected, [string]$Reason) {
    Assert-True ([Convert]::ToBase64String($Actual) -ceq [Convert]::ToBase64String($Expected)) $Reason
}
function Assert-Throws([scriptblock]$Action, [string]$Reason) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-True $threw $Reason
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('a21-hosted-name-test-' + [Guid]::NewGuid().ToString('N'))
$allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$previous = [Environment]::GetEnvironmentVariable('AURORA21_DISCORD_USERNAME', 'Process')
try {
    [void][IO.Directory]::CreateDirectory($root)
    $game = Join-Path $root 'FIFA21.exe'
    [IO.File]::WriteAllBytes($game, [byte[]]@())
    $config = Join-Path $root 'anadius.cfg'
    $backup = $config + '.aurora21.bak'
    $name = 'discord.name_123'
    $comment = 'caf' + [char]0xE9
    $body = "// $comment`r`n{`r`n`"Username`"    `"voices38`"`r`n`"Other`" `"unchanged`"`r`n}`r`n"
    $bom = [byte[]]@(0xEF, 0xBB, 0xBF)
    $original = [byte[]]($bom + [Text.Encoding]::UTF8.GetBytes($body))
    [IO.File]::WriteAllBytes($config, $original)

    Assert-True (Sync-DirectGameUsername $game $name) 'First rename should update the local profile.'
    $expected = [byte[]]($bom + [Text.Encoding]::UTF8.GetBytes($body.Replace('voices38', $name)))
    Assert-Bytes ([IO.File]::ReadAllBytes($config)) $expected 'UTF-8 BOM, comments, unrelated fields and CRLF must survive.'
    Assert-Bytes ([IO.File]::ReadAllBytes($backup)) $original 'The first update must keep the original bytes.'
    Assert-True (-not (Sync-DirectGameUsername $game $name)) 'An unchanged name should not rewrite the profile.'
    Assert-True (Sync-DirectGameUsername $game 'second_name') 'A later verified name should replace the local value.'
    Assert-Bytes ([IO.File]::ReadAllBytes($backup)) $original 'The first backup must remain intact after later renames.'
    Assert-Bytes ([IO.File]::ReadAllBytes($config)) ([byte[]]($bom + [Text.Encoding]::UTF8.GetBytes($body.Replace('voices38', 'second_name')))) 'A repeat update must change only the Username bytes.'
    $longName = 'a' * 32
    Assert-True (Sync-DirectGameUsername $game $longName) 'A full-length Discord username must be accepted without truncation.'
    Assert-Bytes ([IO.File]::ReadAllBytes($config)) ([byte[]]($bom + [Text.Encoding]::UTF8.GetBytes($body.Replace('voices38', $longName)))) 'The full 32-character username must be preserved.'

    $readonlyRoot = Join-Path $root 'readonly'
    [void][IO.Directory]::CreateDirectory($readonlyRoot)
    $readonlyGame = Join-Path $readonlyRoot 'FIFA21.exe'
    $readonlyConfig = Join-Path $readonlyRoot 'anadius.cfg'
    [IO.File]::WriteAllBytes($readonlyGame, [byte[]]@())
    [IO.File]::WriteAllBytes($readonlyConfig, $original)
    [IO.File]::SetAttributes($readonlyConfig, [IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Archive)
    try {
        try { $readonlyChanged = Sync-DirectGameUsername $readonlyGame 'readonly_user' }
        catch {
            $cause = $_.Exception.GetBaseException()
            throw ('ReadOnly fixture update failed: ' + $cause.GetType().Name +
                ' HResult=0x' + $cause.HResult.ToString('X8'))
        }
        Assert-True $readonlyChanged 'A read-only direct-copy profile must update when its single Username differs.'
        Assert-Bytes ([IO.File]::ReadAllBytes($readonlyConfig)) ([byte[]]($bom + [Text.Encoding]::UTF8.GetBytes($body.Replace('voices38', 'readonly_user')))) 'The read-only update must preserve unrelated bytes.'
        Assert-Bytes ([IO.File]::ReadAllBytes($readonlyConfig + '.aurora21.bak')) $original 'The first read-only backup must keep the original bytes.'
        $attributes = [IO.File]::GetAttributes($readonlyConfig)
        Assert-True (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) 'The read-only flag must be restored after updating.'
        Assert-True (($attributes -band [IO.FileAttributes]::Archive) -ne 0) 'The Archive flag must be preserved after updating.'
        $backupAttributes = [IO.File]::GetAttributes($readonlyConfig + '.aurora21.bak')
        Assert-True (($backupAttributes -band [IO.FileAttributes]::ReadOnly) -ne 0) 'The original backup must retain its read-only flag.'
        Assert-True (-not (Sync-DirectGameUsername $readonlyGame 'readonly_user')) 'An unchanged read-only profile must not be rewritten.'

        # A reader that does not share DELETE lets the byte checks pass but refuses File.Replace.
        # This proves finally restores the original flags after a late filesystem failure.
        $beforeLocked = [IO.File]::ReadAllBytes($readonlyConfig)
        $held = [IO.File]::Open($readonlyConfig, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { Assert-Throws { Sync-DirectGameUsername $readonlyGame 'blocked_user' } 'A locked read-only profile must fail safely.' }
        finally { $held.Dispose() }
        Assert-Bytes ([IO.File]::ReadAllBytes($readonlyConfig)) $beforeLocked 'A failed replacement must leave the profile bytes unchanged.'
        Assert-True ([IO.File]::GetAttributes($readonlyConfig) -eq $attributes) 'A failed replacement must restore ReadOnly and Archive exactly.'
        Assert-True ((Get-ChildItem -LiteralPath $readonlyRoot -Filter 'anadius.cfg.aurora21.*.tmp' -File).Count -eq 0) 'A failed replacement must remove temporary files.'
        Assert-Bytes ([IO.File]::ReadAllBytes($readonlyConfig + '.aurora21.bak')) $original 'A failed later update must preserve the original backup.'
    } finally {
        if (Test-Path -LiteralPath $readonlyConfig) { [IO.File]::SetAttributes($readonlyConfig, [IO.FileAttributes]::Normal) }
    }

    foreach ($invalid in @('a', ('x' * 33), 'bad"name', "bad`nname", 'bad-name', ('nam' + [char]0xE9))) {
        $before = [IO.File]::ReadAllBytes($config)
        Assert-Throws { Sync-DirectGameUsername $game $invalid } 'Invalid usernames must be refused.'
        Assert-Bytes ([IO.File]::ReadAllBytes($config)) $before 'Invalid usernames must not alter the profile.'
    }

    $duplicate = "`"Username`" `"one`"`r`n`"Username`" `"two`"`r`n"
    [IO.File]::WriteAllBytes($config, [Text.Encoding]::UTF8.GetBytes($duplicate))
    Assert-Throws { Sync-DirectGameUsername $game 'valid_user' } 'Duplicate Username keys must be refused.'
    Assert-Bytes ([IO.File]::ReadAllBytes($config)) ([Text.Encoding]::UTF8.GetBytes($duplicate)) 'Ambiguous profiles must stay unchanged.'
    $malformed = "`"Username`" = `"one`"`r`n"
    [IO.File]::WriteAllBytes($config, [Text.Encoding]::UTF8.GetBytes($malformed))
    Assert-Throws { Sync-DirectGameUsername $game 'valid_user' } 'Non-VDF Username syntax must be refused.'

    [IO.File]::Delete($config)
    Assert-True (-not (Sync-DirectGameUsername $game 'valid_user')) 'A direct copy without anadius.cfg must be left alone.'
    [Environment]::SetEnvironmentVariable('AURORA21_DISCORD_USERNAME', 'valid_user', 'Process')
    Assert-True ((Take-HostedDiscordUsername) -ceq 'valid_user') 'The scoped username should be captured once.'
    Assert-True ($null -eq [Environment]::GetEnvironmentVariable('AURORA21_DISCORD_USERNAME', 'Process')) 'The scoped username must be cleared before child processes.'
    Assert-True ($null -eq (Take-HostedDiscordUsername)) 'An absent environment value should be a no-op.'
    Write-Output 'PASS hosted direct-copy username fixtures'
} finally {
    [Environment]::SetEnvironmentVariable('AURORA21_DISCORD_USERNAME', $previous, 'Process')
    $resolved = [IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
