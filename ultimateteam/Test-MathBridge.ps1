# Checks for the hosted client pack's maths bridge: the pinned ucrtbase.dll, the committed Aurora21-MathBridge.exe and
# its source, the bridge's switch and report logic against stand-ins (tests/MathBridgeHarness), and Start-Aurora21.ps1's
# helper plumbing with a stand-in helper. No FIFA, UAC, hosts file or outside network is touched: the stand-ins run
# under %TEMP%, and the only sockets are on 127.0.0.1.
[CmdletBinding()]
param([switch]$SkipHarness)

$ErrorActionPreference = 'Stop'
$script:checks = 0
function Assert-True([bool]$Condition, [string]$Reason) {
    if (-not $Condition) { throw "FAIL: $Reason" }
    $script:checks++
}
# Runs a program and returns its output lines; Windows PowerShell would turn its stderr into terminating errors.
function Invoke-Program([string]$Path, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = @(& $Path @Arguments 2>&1 | ForEach-Object { "$_" })
    $script:exitCode = $LASTEXITCODE
    return $output
}

$PinnedSha256 = '2E5FB14B7BF8540278F3614A12F0226E56A7CC9E64B81CBD976C6FCF2F71CBFB'
$PinnedVersion = '10.0.26100.1742'
$packRoot = Join-Path $PSScriptRoot 'client-pack'
$packLibrary = Join-Path $packRoot 'ucrt\ucrtbase.dll'
$helperExe = Join-Path $PSScriptRoot 'Aurora21-MathBridge.exe'
$helperSource = Join-Path $PSScriptRoot 'FIFA21-LocalMathBridge.cs'
$harnessSource = Join-Path $PSScriptRoot 'tests\MathBridgeHarness\MathBridgeHarness.cs'
$compiler = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$work = Join-Path ([IO.Path]::GetTempPath()) ('a21-math-bridge-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    # --- The pinned library and the helper that pins it -------------------------------------------------------------
    Assert-True (Test-Path -LiteralPath $packLibrary -PathType Leaf) 'The pack ships no ucrt\ucrtbase.dll.'
    $hash = (Get-FileHash -LiteralPath $packLibrary -Algorithm SHA256).Hash
    Assert-True ($hash -eq $PinnedSha256) "ucrt\ucrtbase.dll is not the pinned 26100.1742 build (SHA-256 $hash)."
    $info = (Get-Item -LiteralPath $packLibrary).VersionInfo
    $version = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    Assert-True ($version -eq $PinnedVersion) "ucrt\ucrtbase.dll reads version $version, not $PinnedVersion."
    $ucrtFiles = @(Get-ChildItem -LiteralPath (Join-Path $packRoot 'ucrt') -Force | ForEach-Object { $_.Name })
    Assert-True ($ucrtFiles.Count -eq 1 -and $ucrtFiles[0] -eq 'ucrtbase.dll') "ucrt\ must hold only ucrtbase.dll, not: $($ucrtFiles -join ', ')"
    $notice = Get-Content -LiteralPath (Join-Path $packRoot 'THIRD-PARTY-NOTICES.txt') -Raw
    Assert-True ($notice -match [regex]::Escape("ucrtbase.dll), version $PinnedVersion")) 'The Microsoft redistributable notice does not name the shipped ucrtbase.'
    $sourceText = Get-Content -LiteralPath $helperSource -Raw
    $pins = @([regex]::Matches(($sourceText -split 'PinnedUcrtSha256 = \{', 2)[1].Split('}')[0], '[0-9A-F]{64}') | ForEach-Object { $_.Value })
    Assert-True ($pins.Count -eq 1 -and $pins[0] -eq $PinnedSha256) "FIFA21-LocalMathBridge.cs must pin exactly the shipped ucrtbase ($($pins -join ', '))."
    Assert-True (Test-Path -LiteralPath $helperExe -PathType Leaf) 'Aurora21-MathBridge.exe is missing.'

    $selfTest = Invoke-Program $helperExe @('--self-test', '--dll', $packLibrary)
    Assert-True ($script:exitCode -eq 0) "The helper's self-test failed: $($selfTest -join ' | ')"
    Assert-True (($selfTest -join "`n") -match "PASS library: SHA-256 $PinnedSha256, all 50 exports present, version $([regex]::Escape($PinnedVersion))") 'The helper did not accept the pinned library.'
    $decoy = Join-Path $work 'decoy\ucrtbase.dll'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $decoy) | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\ucrtbase.dll') -Destination $decoy
    $refused = Invoke-Program $helperExe @('--self-test', '--dll', $decoy)
    Assert-True ($script:exitCode -ne 0 -and ($refused -join "`n") -match 'not the pinned Microsoft build') 'The helper accepted an unpinned ucrtbase.dll.'
    # The code is a bearer secret: no report without --code-stdin, and never over plain HTTP to another host.
    $noCode = Invoke-Program $helperExe @('--pid', '4', '--game', "$work\FIFA21.exe", '--dll', $packLibrary, '--report', 'https://21.aurorafut.com/client/launch/math')
    Assert-True ($script:exitCode -ne 0 -and ($noCode -join "`n") -match '--report needs --code-stdin') 'The helper accepted a report without a launch code.'
    $plain = Invoke-Program $helperExe @('--pid', '4', '--game', "$work\FIFA21.exe", '--dll', $packLibrary, '--code-stdin', '--report', 'http://21.aurorafut.com/client/launch/math')
    Assert-True ($script:exitCode -ne 0 -and ($plain -join "`n") -match 'must be HTTPS') 'The helper accepted a plain-HTTP report URL.'
    $wrongGame = Invoke-Program $helperExe @('--pid', '4', '--game', "$work\FIFA21.exe", '--dll', $packLibrary)
    Assert-True ($script:exitCode -ne 0 -and ($wrongGame -join "`n") -match 'Unsupported FIFA executable') 'The helper did not refuse an unsupported FIFA build.'

    # --- The switch and the report against stand-ins ---------------------------------------------------------------
    Assert-True ([bool]$compiler) 'The .NET Framework C# compiler was not found.'
    if (-not $SkipHarness) {
        $harness = Join-Path $work 'MathBridgeHarness.exe'
        $built = Invoke-Program $compiler @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/reference:System.dll', '/reference:System.Core.dll', '/main:MathBridgeHarness', ("/out:{0}" -f $harness), $helperSource, $harnessSource)
        Assert-True ($script:exitCode -eq 0) "The harness did not compile: $($built -join ' | ')"
        $run = Invoke-Program $harness @($packLibrary)
        $run | ForEach-Object { Write-Host "  $_" }
        Assert-True ($script:exitCode -eq 0 -and $run[-1] -eq 'ALL PASSED') 'The maths bridge harness failed.'
    }

    # --- Start-Aurora21.ps1 -------------------------------------------------------------------------------------------
    $script = Join-Path $packRoot 'Start-Aurora21.ps1'
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) 'The hosted start script does not parse.'
    $text = Get-Content -LiteralPath $script -Raw
    foreach ($gone in 'Deploy-MathBridge', 'Remove-MathBridge', 'Test-MathBridgeLoaded', 'Send-MathBridgeReport', 'cpu-features', 'api-ms-win-crt') {
        Assert-True ($text -notmatch [regex]::Escape($gone)) "The app-local ucrt flow is still in Start-Aurora21.ps1: $gone"
    }
    # Order: the helper starts before the trust patch; the code reaches it only after the auth bridge and the hosts
    # entry, right before [READY]; nothing waits for it.
    $offset = {
        param([string]$Pattern)
        $match = [regex]::Match($text, $Pattern)
        if ($match.Success) { $match.Index } else { -1 }
    }
    $start = & $offset '(?m)^\s*\$mathBridge = Start-MathBridge \$mathPid'
    $trust = & $offset '(?m)^\s*\$patch = Invoke-TrustPatchUntilReady'
    $auth = & $offset '(?m)^\s*try \{ Invoke-AuthBridge'
    $hosts = & $offset '(?m)^\s*try \{ Set-RedirectHostsEntry'
    $submit = & $offset '(?m)^Submit-MathBridgeCode \$mathBridge \$patch\.GamePid \$code'
    $ready = & $offset '(?m)^Write-Output "\[READY\] FIFA 21 \(PID'
    Assert-True ($start -gt 0 -and $start -lt $trust) 'The maths helper must start before the trust patch.'
    Assert-True ($auth -gt $trust -and $hosts -gt $auth -and $submit -gt $hosts -and $ready -gt $submit) 'The launch code must reach the maths helper only after the auth bridge and the hosts entry, before [READY].'
    Assert-True ($text -notmatch '\$(mathBridge|Bridge)\.Process\.WaitForExit') 'The hosted launch must never wait for the maths helper.'
    $runtime = Get-Content -LiteralPath (Join-Path $packRoot 'runtime.json') -Raw | ConvertFrom-Json
    Assert-True (($runtime.cleanup.args -join ' ') -notmatch '-GamePath') 'Cleanup has nothing of the maths bridge to remove; it takes no -GamePath.'

    # Load the helper functions, the native type and the pack paths, then drive them with a stand-in helper.
    $addType = $ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text -match "'UltimateTeamNativeProcess' -as \[type\]" }, $false)
    Assert-True ($null -ne $addType) 'The native process type is missing.'
    . ([scriptblock]::Create($addType.Extent.Text))
    foreach ($name in 'Get-MathBridgeArguments', 'Start-DetachedHelper', 'Start-MathBridge', 'Submit-MathBridgeCode', 'Format-MathBridgeResult', 'Get-MathBridgeStatus') {
        $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        Assert-True ($null -ne $definition) "Missing maths bridge function: $name"
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $PackRoot = $packRoot
    foreach ($name in 'MathBridgeHelper', 'MathBridgeLibrary', 'MathReportHosts') {
        $assignment = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq "`$$name" }, $false)
        Assert-True ($null -ne $assignment) "Missing `$$name"
        . ([scriptblock]::Create($assignment.Extent.Text))
    }
    # The image copies Aurora21-MathBridge.exe to the pack root, beside the script (deploy/Dockerfile).
    Assert-True ($MathBridgeHelper -eq (Join-Path $packRoot 'Aurora21-MathBridge.exe') -and $MathBridgeLibrary -eq $packLibrary) 'The start script must run the pack''s Aurora21-MathBridge.exe on its ucrt\ucrtbase.dll.'
    $dockerfile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'deploy\Dockerfile') -Raw
    Assert-True ($dockerfile -match 'COPY Aurora21-LocalAuthBridge\.exe Aurora21-MathBridge\.exe [^\r\n]*\\\r?\n\s+client-pack/THIRD-PARTY-NOTICES\.txt /pack/' -and
        $dockerfile -match '(?m)^COPY client-pack/ucrt/ucrtbase\.dll /pack/ucrt/\r?$') 'The image must put the helper, its notice and only ucrt/ucrtbase.dll into the pack.'

    $code = '0123456789abcdef0123456789abcdef01234567'
    $GamePath = 'C:\Games\FIFA 21\FIFA21.exe'
    $arguments = Get-MathBridgeArguments 4242 $GamePath 'C:\logs\math-bridge.log'
    Assert-True ($arguments.StartsWith('--pid 4242 --game "C:\Games\FIFA 21\FIFA21.exe" --dll "' + $MathBridgeLibrary + '" --log "C:\logs\math-bridge.log" --code-stdin')) "Unexpected helper arguments: $arguments"
    Assert-True ($MathReportHosts.Count -ge 1 -and ([regex]::Matches($arguments, '--report "https://[a-z0-9.-]+/client/launch/math"')).Count -eq $MathReportHosts.Count) 'Every report URL must be the HTTPS /client/launch/math of a server name.'

    $math = "v=1; ucrt=$PinnedVersion; sha256=2E5FB14B7BF85402; fma3=1"
    $ok = "[WAIT] x`r`n[SWITCHED] 50 maths pointers use the bundled ucrtbase $PinnedVersion; watching for 20 seconds.`r`n[READY] Maths bridge installed for PID 4242: 50 maths imports use ucrtbase $PinnedVersion (FMA3 on).`r`n[MATH] $math`r`n[SCOPE] x`r`n"
    Assert-True ((Format-MathBridgeResult ($ok + "[REPORTED] $math to 21.aurorafut.com: {`"ok`":true,`"group`":`"24h2`"}`r`n") 0) -eq "[INFO] maths bridge: FIFA uses the pinned ucrtbase ($math); reported") 'A reported switch is not described as such.'
    Assert-True ((Format-MathBridgeResult ($ok + "[WARN] Maths report not sent: x`r`n") 0) -match 'not reported; pairing uses the Windows build$') 'An unreported switch is not described as such.'
    Assert-True ((Format-MathBridgeResult "[ERROR] Unsupported FIFA executable. The maths bridge supports build 21.0.4750997.0 only.`r`n" 1) -eq '[INFO] maths bridge: Unsupported FIFA executable. The maths bridge supports build 21.0.4750997.0 only; pairing uses the Windows build') 'A refusal is not passed on.'
    Assert-True ((Format-MathBridgeResult '' 1) -eq '[INFO] maths bridge: exit 1; pairing uses the Windows build') 'A silent failure is not described.'
    Assert-True ((Format-MathBridgeResult ($ok -replace '\[READY\][^\r\n]*', '') 0) -match 'pairing uses the Windows build$') 'A [MATH] line without [READY] counted as a switch.'

    # A stand-in helper: logs its arguments and the launch code the real helper's reader (ReadLaunchCode) takes from
    # standard input, then waits until told to stop.
    $standIn = Join-Path $work 'Aurora21-MathBridge.exe'
    $standInSource = Join-Path $work 'StandIn.cs'
    Set-Content -LiteralPath $standInSource -Encoding ascii -Value @'
using System; using System.IO; using System.Threading;
static class StandIn {
    static int Main(string[] args) {
        string log = null;
        for (int i = 0; i + 1 < args.Length; i++) { if (args[i] == "--log") log = args[i + 1]; }
        File.WriteAllText(log, "ARGS " + string.Join(" ", args) + Environment.NewLine);
        string code;
        using (var input = Console.OpenStandardInput()) code = LocalMathBridge.ReadLaunchCode(input);
        File.AppendAllText(log, "STDIN " + (code ?? "(none)") + Environment.NewLine);
        while (!File.Exists(log + ".stop")) Thread.Sleep(20);
        File.AppendAllText(log, "[ERROR] stand-in stopped." + Environment.NewLine);
        return 3;
    }
}
'@
    $built = Invoke-Program $compiler @('/nologo', '/target:exe', '/platform:x64', '/reference:System.dll', '/reference:System.Core.dll', '/main:StandIn',
        ("/out:{0}" -f $standIn), $standInSource, $helperSource)
    Assert-True ($script:exitCode -eq 0) "The stand-in helper did not compile: $($built -join ' | ')"
    $MathBridgeHelper = $standIn
    $PackRoot = $work
    $waitFor = {
        param([string]$Path, [string]$Pattern)
        $until = (Get-Date).AddSeconds(15)
        $content = ''
        do {
            $content = [string](Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue)
            if ($content -match $Pattern) { return $content }
            Start-Sleep -Milliseconds 50
        } while ((Get-Date) -lt $until)
        throw "FAIL: timed out waiting for '$Pattern' in $Path (got: $content)"
    }
    $inheritFlags = {
        foreach ($standard in -10, -11, -12) {
            [uint32]$flags = 0
            [void][UltimateTeamNativeProcess]::GetHandleInformation([UltimateTeamNativeProcess]::GetStdHandle($standard), [ref]$flags)
            $flags
        }
    }

    $inheritBefore = @(& $inheritFlags) -join ','
    $matchedLog = Join-Path $work 'math-bridge.log'
    $otherLog = Join-Path $work 'math-bridge-4343.log'
    $matched = $null
    $other = $null
    try {
        $matched = Start-MathBridge 4242 $matchedLog
        $other = Start-MathBridge 4343 $otherLog
        $inheritAfter = @(& $inheritFlags) -join ','
        Assert-True ($inheritBefore -eq $inheritAfter) "Starting the helper must leave this script's own handles as they were ($inheritBefore -> $inheritAfter)."
        $started = & $waitFor $matchedLog '(?m)^ARGS '
        Assert-True ($started -notmatch $code) 'The launch code reached the helper''s command line.'
        Assert-True ((Get-MathBridgeStatus $matched) -match '^\[INFO\] maths bridge: switching FIFA''s maths') 'A running helper is not described as still switching.'
        Add-Content -LiteralPath $matchedLog -Value "[READY] Maths bridge installed for PID 4242: 50 maths imports use ucrtbase $PinnedVersion (FMA3 on).", "[MATH] $math"
        Assert-True ((Get-MathBridgeStatus $matched) -eq "[INFO] maths bridge: FIFA uses the pinned ucrtbase ($math); the helper is reporting it (see math-bridge.log)") 'A switched helper waiting for its code is not described as reporting.'
        Submit-MathBridgeCode $other 4242 $code     # the helper of another FIFA process gets no code
        Submit-MathBridgeCode $matched 4242 $code
        $received = & $waitFor $matchedLog '(?m)^STDIN '
        Assert-True ($received -match "(?m)^STDIN $code\r?$") "The patched process's helper did not get the code: $received"
        $closed = & $waitFor $otherLog '(?m)^STDIN '
        Assert-True ($closed -match '(?m)^STDIN \(none\)\r?$') "Another process's helper got the code: $closed"
        Set-Content -LiteralPath ($matchedLog + '.stop') -Value 'stop'
        Assert-True ($matched.Process.WaitForExit(10000)) 'The stand-in helper did not stop.'
        Assert-True ((Get-MathBridgeStatus $matched) -eq '[INFO] maths bridge: stand-in stopped; pairing uses the Windows build') 'A finished helper''s error is not passed on.'
    } finally {
        foreach ($bridge in $matched, $other) {
            if ($bridge) {
                Set-Content -LiteralPath ($bridge.Log + '.stop') -Value 'stop' -ErrorAction SilentlyContinue
                if (-not $bridge.Process.WaitForExit(10000)) { $bridge.Process.Kill() }
            }
        }
    }
    Submit-MathBridgeCode $null 4242 $code   # no helper: nothing to do, no error
    $MathBridgeHelper = Join-Path $work 'missing\Aurora21-MathBridge.exe'
    $missing = $null
    try { [void](Start-MathBridge 4242 (Join-Path $work 'never.log')) } catch { $missing = $_.Exception.Message }
    Assert-True ($missing -eq 'Aurora21-MathBridge.exe is not in this client pack') "A pack without the helper is not refused cleanly: $missing"

    $harnessNote = if ($SkipHarness) { 'harness skipped' } else { 'stand-in harness' }
    Write-Output "PASS maths bridge: $script:checks checks (pinned ucrtbase $PinnedVersion, helper self-test, $harnessNote, start-script plumbing)"
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
