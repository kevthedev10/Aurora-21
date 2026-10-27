# Fixture-only checks for the hosted start script's EA app helpers: FIFA's setup window switch (FIFASetup\config.ini
# AUTO_LAUNCH) and the hosts-file holder lookup. No game, UAC, hosts or EA app is touched.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'client-pack\Start-Aurora21.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'The hosted start script does not parse.' }
foreach ($name in @('Enable-FifaLauncherAutoLaunch', 'Get-FileHolders')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing hosted start function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Assert-True([bool]$Condition, [string]$Reason) {
    if (-not $Condition) { throw $Reason }
}

# fifaconfig.exe's own reader (Settings.AddSettingsFromIniFile): the same regex, one Dictionary.Add per matching line,
# which throws on a repeated key. Returns the settings it would load.
function Read-FifaConfig([string]$Path) {
    $settings = [Collections.Generic.Dictionary[string, string]]::new()
    $regex = [regex]::new('\s*(?<key>[\S=]+)\s*=\s*((''(?<value>.*)'')|(?<value>\".*\")|(?<value>\S+))\s*')
    foreach ($line in [IO.File]::ReadAllLines($Path, [Text.Encoding]::UTF8)) {
        $match = $regex.Match($line)
        if ($match.Success) { $settings.Add($match.Groups['key'].Value, $match.Groups['value'].Value) }
    }
    return $settings
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('a21-fifa-autolaunch-test-' + [Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'FIFASetup'))
    $game = Join-Path $root 'FIFA21.exe'
    [IO.File]::WriteAllBytes($game, [byte[]]@())
    $config = Join-Path $root 'FIFASetup\config.ini'
    $retail = "LAUNCH_EXE = fifa21.exe`r`nSETTING_FOLDER = 'FIFA 21'`r`n"

    Assert-True (-not (Enable-FifaLauncherAutoLaunch $game)) 'Without config.ini nothing may be created (fifaconfig would fall back to fifa17.exe).'
    Assert-True (-not (Test-Path -LiteralPath $config)) 'A missing config.ini must stay missing.'

    [IO.File]::WriteAllText($config, $retail)
    Assert-True (Enable-FifaLauncherAutoLaunch $game) 'The retail config.ini should gain AUTO_LAUNCH.'
    Assert-True ([IO.File]::ReadAllText($config) -ceq ($retail + "AUTO_LAUNCH = 1`r`n")) 'The retail lines and CRLF endings must survive.'
    $settings = Read-FifaConfig $config
    Assert-True ($settings['AUTO_LAUNCH'] -eq '1' -and $settings['LAUNCH_EXE'] -eq 'fifa21.exe' -and $settings['SETTING_FOLDER'] -eq 'FIFA 21') 'fifaconfig must read AUTO_LAUNCH 1 next to the retail values.'
    Assert-True (-not (Enable-FifaLauncherAutoLaunch $game)) 'A config.ini already set must not be rewritten.'

    [IO.File]::WriteAllText($config, "LAUNCH_EXE = fifa21.exe`r`nAUTO_LAUNCH = 0`r`nSETTING_FOLDER = 'FIFA 21'`r`n")
    Assert-True (Enable-FifaLauncherAutoLaunch $game) 'AUTO_LAUNCH = 0 should be switched on.'
    Assert-True ([IO.File]::ReadAllText($config) -ceq "LAUNCH_EXE = fifa21.exe`r`nAUTO_LAUNCH = 1`r`nSETTING_FOLDER = 'FIFA 21'`r`n") 'The existing line must be rewritten in place, not a second one added.'
    [void](Read-FifaConfig $config)

    [IO.File]::WriteAllText($config, "AUTO_LAUNCH=0`nLAUNCH_EXE = fifa21.exe`n  AUTO_LAUNCH = 2`n")
    Assert-True (Enable-FifaLauncherAutoLaunch $game) 'Repeated AUTO_LAUNCH lines should be repaired.'
    Assert-True ([IO.File]::ReadAllText($config) -ceq "AUTO_LAUNCH = 1`nLAUNCH_EXE = fifa21.exe`n") 'Repeats must collapse to one line (fifaconfig throws on a repeated key), keeping LF endings.'
    Assert-True ((Read-FifaConfig $config)['AUTO_LAUNCH'] -eq '1') 'The repaired file must load.'

    [IO.File]::WriteAllText($config, "LAUNCH_EXE = fifa21.exe`r`nSETTING_FOLDER = 'FIFA 21'")
    Assert-True (Enable-FifaLauncherAutoLaunch $game) 'A file without a final newline should gain AUTO_LAUNCH.'
    Assert-True ([IO.File]::ReadAllText($config) -ceq "LAUNCH_EXE = fifa21.exe`r`nSETTING_FOLDER = 'FIFA 21'`r`nAUTO_LAUNCH = 1`r`n") 'AUTO_LAUNCH must go on its own line.'

    [IO.File]::WriteAllText($config, "auto_launch = 0`r`nLAUNCH_EXE = fifa21.exe`r`n")
    Assert-True (Enable-FifaLauncherAutoLaunch $game) 'A differently cased key is another key to fifaconfig.'
    Assert-True ((Read-FifaConfig $config)['AUTO_LAUNCH'] -eq '1') 'AUTO_LAUNCH must be added beside auto_launch without a clash.'

    [IO.File]::WriteAllText($config, $retail)
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Archive)
    Assert-True (Enable-FifaLauncherAutoLaunch $game) 'A read-only config.ini should still be updated.'
    $attributes = [IO.File]::GetAttributes($config)
    Assert-True (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0 -and ($attributes -band [IO.FileAttributes]::Archive) -ne 0) 'The read-only and Archive flags must be restored.'
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal)

    $locked = Join-Path $root 'locked.txt'
    $stream = [IO.File]::Open($locked, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $holders = @(Get-FileHolders $locked)
        Assert-True (@($holders | Where-Object { $_ -match "\(PID $PID\)$" }).Count -eq 1) "The holder lookup should name this process (got: $($holders -join ', '))."
    } finally { $stream.Dispose() }
    Assert-True (@(Get-FileHolders $locked).Count -eq 0) 'A file nobody holds must report no holders.'

    Write-Output 'FIFA launcher auto-launch and file holder checks passed.'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
