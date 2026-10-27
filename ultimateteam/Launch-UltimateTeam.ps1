[CmdletBinding()]
param(
    [string]$GamePath = "",
    [string]$ClientModDataPath = "",
    [switch]$UpdateLeaderboardClientMods,
    [switch]$NoGameLaunch,
    [switch]$SafeGameLaunch,
    [switch]$WaitForExternalGame,
    [ValidateSet('auto', 'ea-app', 'replacement')]
    [string]$LSXMode = "",
    [string]$UserConfigRoot = "",
    [string]$LauncherLogPath = ""
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
. (Join-Path $ProjectRoot 'User-Settings.ps1')
$UserConfigRoot = Resolve-UltimateTeamUserConfigRoot $UserConfigRoot
$SettingsPath = Join-Path $UserConfigRoot 'launcher-settings.json'
# Resolve before elevation and forward explicitly, including over-the-shoulder UAC.
$env:ULTIMATETEAM21_USER_CONFIG_ROOT = $UserConfigRoot
$settings = Get-UltimateTeamUserSettings $UserConfigRoot
# Explicit opt-in for client asset tests; an ordinary launcher start uses original data.
if ($ClientModDataPath) {
    if ($ClientModDataPath.Contains('"')) { throw 'ClientModDataPath cannot contain a quotation mark.' }
    if (-not [IO.Path]::IsPathRooted($ClientModDataPath)) { $ClientModDataPath = Join-Path $ProjectRoot $ClientModDataPath }
    $ClientModDataPath = [IO.Path]::GetFullPath($ClientModDataPath).TrimEnd('\', '/')
    foreach ($required in @('Data\layout.toc', 'Patch\layout.toc')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ClientModDataPath $required) -PathType Leaf)) {
            throw "ClientModDataPath must contain compiled game data: missing $required."
        }
    }
    if ($WaitForExternalGame) { throw 'ClientModDataPath requires this worker to launch the game with the mod arguments.' }
}
$clientModGameArguments = if ($ClientModDataPath) { '-dataPath "{0}"' -f $ClientModDataPath } else { '' }
if ($null -ne $settings.localSessionTest -and $settings.localSessionTest -isnot [bool]) {
    throw "launcher-settings.json localSessionTest must be true or false."
}
$localSessionTest = $settings.localSessionTest -eq $true
$tlsProbeMode = if ($settings.tlsProbeMode) { [string]$settings.tlsProbeMode } else { 'off' }
if ($tlsProbeMode -cnotin @('off', 'fut', 'nucleus', 'both', 'all')) {
    throw "launcher-settings.json tlsProbeMode must be off, fut, nucleus, both, or all."
}
$tlsProbePort = 18443
# 'all' also moves EASW and SSF to TLS, on the probe port + 2 and + 3 (the server's TlsProbeOptions).
$tlsProbeExtraPorts = if ($tlsProbeMode -eq 'all') { @(($tlsProbePort + 2), ($tlsProbePort + 3)) } else { @() }
if ($null -ne $settings.blazeTlsProbe -and $settings.blazeTlsProbe -isnot [bool]) {
    throw "launcher-settings.json blazeTlsProbe must be true or false."
}
# Loopback experiment: Blaze over TLS on its own port, advertised by the redirector with <secure>1</secure>.
$blazeTlsProbe = $settings.blazeTlsProbe -eq $true
$blazeTlsProbePort = 18444
# The operator panel, loopback only: where rules and content are edited, at http://127.0.0.1:18448/panel/.
$panelPort = 18448
if ($null -ne $settings.advertiseHostname -and $settings.advertiseHostname -isnot [bool]) {
    throw "launcher-settings.json advertiseHostname must be true or false."
}
# Blaze replies name spring18.gosredirector.ea.com (the hosts entry) instead of 127.0.0.1 for QoS, telemetry and ticker.
$advertiseHostname = $settings.advertiseHostname -eq $true
# A hosted server's exported trust anchors (Aurora21-UltimateTeam-Server export-trust-anchors); empty trusts the local CAs.
$trustAnchorsPath = ''
if ($settings.trustAnchorsPath) {
    $trustAnchorsPath = [string]$settings.trustAnchorsPath
    if (-not [IO.Path]::IsPathRooted($trustAnchorsPath)) { $trustAnchorsPath = Join-Path $ProjectRoot $trustAnchorsPath }
    foreach ($anchor in @('local-gos2015-modulus.bin', 'local-gs2019-modulus.bin')) {
        $anchorFile = Get-Item -LiteralPath (Join-Path $trustAnchorsPath $anchor) -ErrorAction SilentlyContinue
        if (-not $anchorFile -or $anchorFile.Length -ne 256) {
            throw "launcher-settings.json trustAnchorsPath '$trustAnchorsPath' must contain the 256-byte $anchor exported by the server."
        }
    }
}
$script:localTestCode = $null
$VendorRoot = Join-Path $ProjectRoot 'runtime-source'
$DataRoot = Join-Path $VendorRoot 'ultimateteam21'
# The C# runtime (built by Build-FIFA21-Launcher.ps1). It hosts every local service;
# the TLS redirector and trust bridge are subcommands of the same executable.
$ServerExecutable = Join-Path $ProjectRoot 'Aurora21-UltimateTeam-Server.exe'
$RuntimeRoot = Join-Path $ProjectRoot 'runtime'
$StatusPath = Join-Path $RuntimeRoot 'status.json'
$PidPath = Join-Path $RuntimeRoot 'launcher-processes.json'
$HostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
. (Join-Path $ProjectRoot 'Local-Routing.ps1')
$RequiredPorts = @(10853, 42230, 44321, 8090)

if (-not ('UltimateTeamNativeProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct UltimateTeamStartupInfo
{
    public uint cb;
    public string lpReserved;
    public string lpDesktop;
    public string lpTitle;
    public uint dwX;
    public uint dwY;
    public uint dwXSize;
    public uint dwYSize;
    public uint dwXCountChars;
    public uint dwYCountChars;
    public uint dwFillAttribute;
    public uint dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
public struct UltimateTeamProcessInformation
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

public static class UltimateTeamNativeProcess
{
    public const uint CreateSuspended = 0x00000004;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref UltimateTeamStartupInfo startupInfo,
        out UltimateTeamProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@
}

function Write-StatusLine([string]$Label, [string]$Value, [ConsoleColor]$Color = [ConsoleColor]::White) {
    Write-Host ('{0,-12} {1}' -f ($Label + ':'), $Value) -ForegroundColor $Color
}

function Save-Status([hashtable]$Status) {
    New-Item -ItemType Directory -Force -Path $RuntimeRoot | Out-Null
    $Status.updatedAt = (Get-Date).ToString('o')
    $Status | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StatusPath -Encoding utf8
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Start-SuspendedGame([string]$Path, [string]$GameArguments = '') {
    $startup = [UltimateTeamStartupInfo]::new()
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf([type][UltimateTeamStartupInfo])
    $processInfo = [UltimateTeamProcessInformation]::new()
    $commandLine = [Text.StringBuilder]::new(('"{0}"' -f $Path))
    if ($GameArguments) { [void]$commandLine.Append(' ').Append($GameArguments) }
    $workingDirectory = Split-Path -Path $Path -Parent
    $started = [UltimateTeamNativeProcess]::CreateProcessW(
        $Path,
        $commandLine,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        $false,
        [UltimateTeamNativeProcess]::CreateSuspended,
        [IntPtr]::Zero,
        $workingDirectory,
        [ref]$startup,
        [ref]$processInfo
    )
    if (-not $started) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    return [pscustomobject]@{
        ProcessId = [int]$processInfo.dwProcessId
        ProcessHandle = $processInfo.hProcess
        ThreadHandle = $processInfo.hThread
    }
}

function Close-SuspendedGameHandles([object]$SuspendedGame) {
    if (-not $SuspendedGame) { return }
    if ($SuspendedGame.ThreadHandle -ne [IntPtr]::Zero) {
        [void][UltimateTeamNativeProcess]::CloseHandle($SuspendedGame.ThreadHandle)
        $SuspendedGame.ThreadHandle = [IntPtr]::Zero
    }
    if ($SuspendedGame.ProcessHandle -ne [IntPtr]::Zero) {
        [void][UltimateTeamNativeProcess]::CloseHandle($SuspendedGame.ProcessHandle)
        $SuspendedGame.ProcessHandle = [IntPtr]::Zero
    }
}

function Resume-SuspendedGame([object]$SuspendedGame) {
    $previousSuspendCount = [UltimateTeamNativeProcess]::ResumeThread($SuspendedGame.ThreadHandle)
    if ($previousSuspendCount -eq [uint32]::MaxValue) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    Close-SuspendedGameHandles $SuspendedGame
}

function Stop-SuspendedGame([object]$SuspendedGame) {
    if (-not $SuspendedGame) { return }
    Stop-Process -Id $SuspendedGame.ProcessId -Force -ErrorAction SilentlyContinue
    Close-SuspendedGameHandles $SuspendedGame
}

function Resolve-ServerExecutable {
    if (-not (Test-Path -LiteralPath $ServerExecutable -PathType Leaf)) {
        throw "The UltimateTeam server was not found at '$ServerExecutable'. Run RUN-FIFA21.cmd (or Build-FIFA21-Launcher.ps1) to build it."
    }
    $postgres = Join-Path $ProjectRoot 'postgres\win-x64\bin\pg_ctl.exe'
    if (-not $env:ULTIMATETEAM_POSTGRES_ROOT -and -not $env:ULTIMATETEAM_POSTGRES_CONNECTION -and -not (Test-Path -LiteralPath $postgres -PathType Leaf)) {
        throw "The club database (PostgreSQL) was not found at '$postgres'. Run RUN-FIFA21.cmd (or Build-FIFA21-Launcher.ps1) to install it."
    }
    return [IO.Path]::GetFullPath($ServerExecutable)
}

function Get-Listener([int]$Port) {
    @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -First 1)
}

function Test-ProcessOwnsServerCommand([object]$ProcessInfo, [string]$ExecutablePath, [string]$Command) {
    if (-not $ProcessInfo -or -not $ProcessInfo.ExecutablePath -or -not $ProcessInfo.CommandLine) { return $false }
    $expected = [IO.Path]::GetFullPath($ExecutablePath).Replace('/', '\')
    if (-not [string]::Equals(([string]$ProcessInfo.ExecutablePath).Replace('/', '\'), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    # Require this checkout's exact executable running the requested subcommand as its first argument,
    # not a copy from another folder or a different command of the same binary.
    $escaped = [regex]::Escape($expected)
    $commandLine = ([string]$ProcessInfo.CommandLine).Replace('/', '\')
    return $commandLine -match ('^\s*(?:"' + $escaped + '"|' + $escaped + ')\s+' + [regex]::Escape($Command) + '(?:\s|$)')
}

function Wait-ForPort([int]$Port, [int]$TimeoutSeconds = 15) {
    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Get-Listener $Port) { return $true }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $until)
    return $false
}

function Wait-ForGameProcess([int]$TimeoutSeconds = 180) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $process = Get-Process -Name @('FIFA21', 'FIFA21_Trial') -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($process) {
            try {
                if (-not $process.HasExited) {
                    return $process
                }
            } catch { }
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    throw "FIFA21.exe did not remain running within $TimeoutSeconds seconds after launch."
}

function Test-HealthyLocalMockServer([string]$ServerBuildHash) {
    try {
        $profile = Invoke-RestMethod -Uri 'http://127.0.0.1:8090/ultimateteam/auth/bootstrap' -TimeoutSec 3 -MaximumRedirection 0
        if ($profile.version -ne 1 -or [string]$profile.serverImplementation -cne 'csharp') { return $false }
        # The running build must be the executable on disk (the hash comparison is case-insensitive).
        if ([string]$profile.serverBuildSha256 -ne $ServerBuildHash) { return $false }
        $expectedClientIds = @('ultimateteam21-blaze-server', 'ultimateteam21-blaze-sdk', 'ultimateteam21-ssf')
        $clientIds = @($profile.clientIds)
        if ($clientIds.Count -ne $expectedClientIds.Count) { return $false }
        for ($index = 0; $index -lt $expectedClientIds.Count; $index++) {
            if ($clientIds[$index] -isnot [string] -or $clientIds[$index] -cne $expectedClientIds[$index]) { return $false }
        }
        $expectedConfig = @{
            blazeServerClientId = $expectedClientIds[0]
            blazeSdkClientId = $expectedClientIds[1]
            blazeSdkClientSecret = ''
            nucleusConnect = 'http://127.0.0.1:8090/'
            identityRedirectUri = 'http://127.0.0.1:8090/ultimateteam/callback'
        }
        foreach ($key in $expectedConfig.Keys) {
            $value = $profile.config.$key
            if ($value -isnot [string] -or $value -cne $expectedConfig[$key]) { return $false }
        }
        return $true
    } catch { return $false }
}

function Ensure-WorkingCopy([string]$SourceRoot) {
    if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
        $SourceRoot = $VendorRoot
    } elseif (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        if (Test-Path -LiteralPath $VendorRoot -PathType Container) {
            Write-Warning "Configured sourcePath '$SourceRoot' does not exist; using the repository-local runtime-source directory."
            $SourceRoot = $VendorRoot
        } else {
            throw "Configured sourcePath '$SourceRoot' does not exist, and the repository-local runtime-source directory is unavailable."
        }
    }

    $mustExist = @('ultimateteam21\config.example.json', 'ultimateteam21\players.json', 'ultimateteam21\managers.json',
                   'ultimateteam21\kits.json', 'ultimateteam21\stadiums.json', 'ultimateteam21\sbc.json',
                   'certs\spring18-cert.pem', 'certs\spring18-chain.pem', 'certs\spring18-key.pem',
                   'gos2015-original-modulus.bin', 'local-gos2015-modulus.bin') +
                 @('cards', 'club', 'draft', 'market', 'objectives', 'onboarding', 'packs', 'sbc', 'seasons', 'squad_battles' | ForEach-Object { "ultimateteam21\rules\$_.json" })
    $missing = @($mustExist | Where-Object { -not (Test-Path -LiteralPath (Join-Path $SourceRoot $_) -PathType Leaf) })
    if ($missing) {
        throw "Source project is incomplete. Missing: $($missing -join ', ')"
    }
    New-Item -ItemType Directory -Force -Path $VendorRoot | Out-Null
    if ((Resolve-Path -LiteralPath $SourceRoot).Path -ne (Resolve-Path -LiteralPath $VendorRoot).Path) {
        # This copies into this new project only; it never writes to SourceRoot.
        Get-ChildItem -LiteralPath $SourceRoot -Force | Copy-Item -Destination $VendorRoot -Recurse -Force
    }
}

function Stop-StartedRuntime([object]$ServerProcess, [object]$RedirectorProcess) {
    foreach ($process in @($ServerProcess, $RedirectorProcess)) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # A rejected foreign listener must never cause a process-name/relative-path
    # sweep; only children started by this invocation are cleanup targets.
}

function Ensure-LocalAuthBridge {
    $sourcePath = Join-Path $ProjectRoot 'FIFA21-LocalAuthBridge.cs'
    $outputPath = Join-Path $ProjectRoot 'Aurora21-LocalAuthBridge.exe'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Local auth bridge source was not found: $sourcePath"
    }
    $source = Get-Item -LiteralPath $sourcePath
    $output = Get-Item -LiteralPath $outputPath -ErrorAction SilentlyContinue
    foreach ($process in @(Get-Process -Name 'Aurora21-LocalAuthBridge' -ErrorAction SilentlyContinue)) {
        if (-not $process.Path -or [IO.Path]::GetFullPath($process.Path) -eq $outputPath) {
            throw "The local auth bridge is still running or its path cannot be verified (PID $($process.Id)). Wait for it to finish before retrying; do not interrupt an active patch."
        }
    }
    if ($output -and $output.LastWriteTimeUtc -ge $source.LastWriteTimeUtc) { return $outputPath }
    $compiler = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) { throw 'The .NET Framework C# compiler required by the local auth bridge was not found.' }
    $compilerArguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+',
        '/reference:System.Web.Extensions.dll', '/reference:System.dll', '/reference:System.Core.dll',
        ('/out:{0}' -f $outputPath), $sourcePath)
    $compilerOutput = & $compiler @compilerArguments 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        throw "Local auth bridge compilation failed (exit $LASTEXITCODE): $($compilerOutput -join [Environment]::NewLine)"
    }
    return $outputPath
}

function Invoke-LocalAuthBridge([string]$HelperPath, [int]$GamePid, [string]$GamePath) {
    $logPath = Join-Path $RuntimeRoot 'auth-bridge.log'
    # Local gate-2 test only: a fresh synthetic code reaches FIFA through stdin and stays on loopback.
    # A hosted launcher must replace this issuer with its authenticated OAuth control-plane exchange.
    $futCode = if ($localSessionTest) { $script:localTestCode } else { New-LocalTestCode }
    if ($futCode -notmatch '^[0-9a-f]{40}$') { throw 'Local session test code is missing or invalid.' }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $HelperPath
    $startInfo.Arguments = '--pid {0} --game "{1}" --fut-code-stdin --log "{2}"' -f $GamePid, $GamePath, $logPath
    $startInfo.WorkingDirectory = $ProjectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    # The helper owns its log handles so a launcher timeout/exit cannot break
    # output pipes while the helper is completing or rolling back a transaction.
    $helper = [Diagnostics.Process]::new()
    $helper.StartInfo = $startInfo
    try {
        if (-not $helper.Start()) { throw 'The local auth bridge process could not be started.' }
        try { $helper.StandardInput.WriteLine($futCode) } finally {
            $helper.StandardInput.Close()
            $futCode = $null
        }
        if (-not $helper.WaitForExit(60000)) {
            $status.authBridge = "UNKNOWN (helper PID $($helper.Id) still running after 60 seconds; not terminated)"
            throw "Local auth bridge readiness is unknown after 60 seconds. Helper PID $($helper.Id) was left running to complete or roll back safely. Do not start another bridge; see $logPath"
        }
        $output = Get-Content -LiteralPath $logPath -Raw -ErrorAction Stop
        if ($helper.ExitCode -ne 0 -or $output -notmatch '(?m)^\[(READY|ALREADY)\]') {
            throw "Local auth bridge did not confirm readiness (exit $($helper.ExitCode)). See $logPath"
        }
    } finally {
        $helper.Dispose()
    }
}

function New-LocalTestCode {
    $codeBytes = [byte[]]::new(20)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($codeBytes) } finally { $rng.Dispose() }
    $code = [BitConverter]::ToString($codeBytes).Replace('-', '').ToLowerInvariant()
    [Array]::Clear($codeBytes, 0, $codeBytes.Length)
    return $code
}

function Invoke-TrustPatch([string]$Server, [int]$GamePid, [bool]$PatchOriginBootstrap = $true) {
    # Passing the PID removes the process-name race while EA's launcher hands FIFA's child process off.
    $arguments = @('trust-patch', '--pid', $GamePid, '--data-root', $DataRoot)
    if (-not $PatchOriginBootstrap) { $arguments += '--skip-origin' }
    if ($trustAnchorsPath) { $arguments += @('--trust-anchors', $trustAnchorsPath) }
    $output = & $Server @arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; GamePid = $GamePid; Output = @($output) }
}

function Invoke-TrustPatchUntilReady([string]$Server, [int]$GamePid, [bool]$RequireOriginBootstrap = $true, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $latest = $null
    do {
        # EA can expose a short-lived FIFA process before handing the window to the
        # real game process. Follow the newest candidate on every pass.
        $candidate = Get-Process -Name @('FIFA21', 'FIFA21_Trial') -ErrorAction SilentlyContinue |
            Where-Object { -not $_.HasExited } |
            Sort-Object StartTime -Descending |
            Select-Object -First 1
        if ($candidate) { $GamePid = $candidate.Id }
        else {
            Start-Sleep -Seconds 1
            continue
        }
        $latest = Invoke-TrustPatch $Server $GamePid $RequireOriginBootstrap
        $text = $latest.Output -join "`n"
        $gos2015Ready = $text -match '\[(PATCHED|ALREADY)\].*GOS 2015 Certificate Authority'
        $originBootstrapReady = $text -match '\[(PATCHED|ALREADY)\].*Origin client bootstrap bypass'
        if ($text -match '\[SUCCESS\] Patched' -and $gos2015Ready -and (-not $RequireOriginBootstrap -or $originBootstrapReady)) {
            return $latest
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $latest
}

function Resolve-GamePath([string]$ExplicitPath, [object]$Settings) {
    if ($ExplicitPath) { return $ExplicitPath }
    if ($Settings.gamePath) { return [string]$Settings.gamePath }
    $candidates = @(
        (Join-Path $env:ProgramFiles 'EA Games\FIFA 21\FIFA21.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Origin Games\FIFA 21\FIFA21.exe'),
        (Join-Path $env:ProgramFiles 'Origin Games\FIFA 21\FIFA21.exe')
    )
    return @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)[0]
}

function Test-GameBinaryPreflight([string]$Path) {
    $knownMalformedHash = 'E1022A950C08724E126493D60222CE763D510D1C6EBD308506860F019BC02763'
    $knownBuildLength = 571958064
    $knownPatchOffset = 0xDBFD82
    $knownMalformedBytes = '66 E9 18 01'

    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($hash -eq $knownMalformedHash) {
        throw 'This FIFA21.exe matches the known malformed four-byte patch. Restore/verify the game through its storefront before tracing or launching it.'
    }

    if ($file.Length -eq $knownBuildLength) {
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            [void]$stream.Seek($knownPatchOffset, [IO.SeekOrigin]::Begin)
            $bytes = [byte[]]::new(4)
            $read = $stream.Read($bytes, 0, $bytes.Length)
        } finally {
            $stream.Dispose()
        }
        $actualBytes = if ($read -eq 4) { [BitConverter]::ToString($bytes).Replace('-', ' ') } else { '<unreadable>' }
        if ($actualBytes -eq $knownMalformedBytes) {
            throw "This FIFA21.exe contains the known malformed bytes at file offset 0xDBFD82. Restore/verify the game through its storefront; the launcher will not repair or run it."
        }
    }

    return [pscustomobject]@{ Hash = $hash; Length = $file.Length }
}

function Resolve-EALauncherPath([object]$Settings) {
    $configured = if ($Settings.eaAppLauncherPath) { [string]$Settings.eaAppLauncherPath } else {
        (Join-Path $env:ProgramFiles 'Electronic Arts\EA Desktop\EA Desktop\EALauncher.exe')
    }
    if (-not (Test-Path -LiteralPath $configured -PathType Leaf)) {
        throw "EA App launcher was not found at '$configured'. Choose it in the launcher's Settings, or edit '$SettingsPath'."
    }
    return $configured
}

function Get-EAAppLaunchUri([object]$Settings, [string]$GameArguments = '') {
    $offerId = if ($Settings.eaAppOfferId) { [string]$Settings.eaAppOfferId } else { '195853' }
    if ($offerId -notmatch '^\d+$') {
        throw "Invalid eaAppOfferId '$offerId'. EA App requires the numeric FIFA 21 offer ID."
    }
    $encodedArguments = [Uri]::EscapeDataString($GameArguments)
    return "origin2://game/launch/?offerIds=$offerId&authCode=&cmdParams=$encodedArguments"
}

function Enter-UltimateTeamLaunchGuard([string]$Root) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $identity = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/').ToLowerInvariant()
        $digest = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity))).Replace('-', '')
    } finally { $hasher.Dispose() }
    $guard = [Threading.Mutex]::new($false, "Global\Aurora21-Launch-$digest")
    try {
        $acquired = $false
        try { $acquired = $guard.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'A launch is already in progress for this checkout. Wait for it to finish.' }
        return $guard
    } catch { $guard.Dispose(); throw }
}

function Assert-UltimateTeamGameLaunchAvailable([object[]]$ExistingGame, [bool]$ServicesOnly, [bool]$ExternalGame) {
    if (-not $ServicesOnly -and -not $ExternalGame -and $ExistingGame.Count) {
        throw "FIFA21.exe is already running (PID $($ExistingGame[0].Id)). Close it before starting a new session."
    }
}

$status = @{ server = 'NOT STARTED'; game = 'NOT STARTED'; connection = 'NOT STARTED'; trustBridge = 'NOT APPLIED'; authBridge = 'NOT APPLIED'; lsx = 'NOT CHECKED'; detail = @() }
$server = $null
$redirector = $null
$suspendedGame = $null
if (-not $SafeGameLaunch -and -not (Test-Administrator)) {
    Write-Host 'Administrator permission is required to update the hosts file and patch the running FIFA21.exe process.' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($GamePath) { $argList += @('-GamePath', ('"{0}"' -f $GamePath)) }
    if ($ClientModDataPath) { $argList += @('-ClientModDataPath', ('"{0}"' -f $ClientModDataPath)) }
    if ($UpdateLeaderboardClientMods) { $argList += '-UpdateLeaderboardClientMods' }
    if ($NoGameLaunch) { $argList += '-NoGameLaunch' }
    if ($SafeGameLaunch) { $argList += '-SafeGameLaunch' }
    if ($WaitForExternalGame) { $argList += '-WaitForExternalGame' }
    if ($LSXMode) { $argList += @('-LSXMode', $LSXMode) }
    $argList += @('-UserConfigRoot', ('"{0}"' -f $UserConfigRoot))
    if ($LauncherLogPath) { $argList += @('-LauncherLogPath', ('"{0}"' -f $LauncherLogPath)) }
    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -WindowStyle Hidden -Wait -PassThru
    exit $elevated.ExitCode
}

$transcribing = $false
$launchGuard = $null
# Reject duplicate starts before changing status, services or another launch's routing.
try {
    $launchGuard = Enter-UltimateTeamLaunchGuard $ProjectRoot
    Assert-UltimateTeamGameLaunchAvailable @(Get-Process -Name @('FIFA21', 'FIFA21_Trial') -ErrorAction SilentlyContinue) $NoGameLaunch.IsPresent $WaitForExternalGame.IsPresent
} catch {
    if ($launchGuard) { $launchGuard.ReleaseMutex(); $launchGuard.Dispose() }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
$localRoutingAttempted = $false
try {
    if ($LauncherLogPath) { Start-Transcript -LiteralPath $LauncherLogPath -Append -Force | Out-Null; $transcribing = $true }
    New-Item -ItemType Directory -Force -Path $RuntimeRoot | Out-Null
    Save-Status $status
    Write-StatusLine 'Settings' $SettingsPath
    if ($NoGameLaunch -and $WaitForExternalGame) {
        throw '-NoGameLaunch and -WaitForExternalGame cannot be used together.'
    }
    Ensure-WorkingCopy ([string]$settings.sourcePath)
    $serverExe = Resolve-ServerExecutable

    $requestedLSXMode = if ($LSXMode) { $LSXMode } elseif ($settings.lsxMode) { [string]$settings.lsxMode } else { 'auto' }
    if ($requestedLSXMode -notin @('auto', 'ea-app', 'replacement')) {
        throw "Invalid lsxMode '$requestedLSXMode'. Use auto, ea-app, or replacement."
    }

    $lsxListener = Get-Listener 3216
    $lsxOwner = $null
    if ($lsxListener) {
        $lsxOwner = Get-CimInstance Win32_Process -Filter "ProcessId=$($lsxListener.OwningProcess)" -ErrorAction SilentlyContinue
    }
    $eaProcessNames = @('EADesktop.exe', 'EALauncher.exe', 'EABackgroundService.exe')
    $eaOwnsLSX = $lsxOwner -and ($eaProcessNames -contains [string]$lsxOwner.Name)
    $localServerOwnsLSX = Test-ProcessOwnsServerCommand $lsxOwner $serverExe 'serve'

    if ($requestedLSXMode -eq 'auto') {
        $selectedLSXMode = if ($eaOwnsLSX) {
            'ea-app'
        } elseif ($localServerOwnsLSX) {
            'replacement'
        } elseif (-not $lsxListener -and $settings.launchViaEAApp -ne $false) {
            'ea-app'
        } elseif (-not $lsxListener) {
            'replacement'
        } else {
            throw "Port 3216 is occupied by $($lsxOwner.Name) (PID $($lsxListener.OwningProcess)); use EA App mode only for an EA-owned listener."
        }
    } elseif ($requestedLSXMode -eq 'ea-app') {
        if ($lsxListener -and -not $eaOwnsLSX) {
            throw 'EA App LSX mode requires EADesktop.exe, EALauncher.exe, or EABackgroundService.exe to own 127.0.0.1:3216.'
        }
        if (-not $eaOwnsLSX) {
            $status.detail += 'EA App LSX listener will be created by the origin2 launch.'
        }
        $selectedLSXMode = 'ea-app'
    } else {
        if ($lsxListener -and -not $localServerOwnsLSX) {
            throw "Replacement LSX mode requires 127.0.0.1:3216 to be free; currently owned by $($lsxOwner.Name) (PID $($lsxListener.OwningProcess))."
        }
        $selectedLSXMode = 'replacement'
    }

    $serverLSXMode = if ($selectedLSXMode -eq 'ea-app') { 'off' } else { 'required' }
    $status.lsx = if ($selectedLSXMode -eq 'ea-app') {
        if ($eaOwnsLSX) {
            "EA APP owns 127.0.0.1:3216 (PID $($lsxListener.OwningProcess))"
        } else {
            'EA APP will own 127.0.0.1:3216 after origin2 launch'
        }
    } else {
        'LOCAL REPLACEMENT will own 127.0.0.1:3216'
    }
    $status.detail += "LSX mode selected: $selectedLSXMode."
    Write-StatusLine 'LSX' $status.lsx Green

    $reuseHealthyServer = $false
    $serverBuildHash = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
    $serverListeners = @(44321, 8090 | ForEach-Object { Get-Listener $_ } | Where-Object { $_ })
    if ($serverListeners) {
        $serverOwnerIds = @($serverListeners | Select-Object -ExpandProperty OwningProcess -Unique)
        if ($serverListeners.Count -ne 2 -or $serverOwnerIds.Count -ne 1) {
            throw 'FUT/Blaze ports 8090 and 44321 must both belong to the same Aurora 21 server before it can be reused or restarted.'
        }
        $serverOwner = Get-CimInstance Win32_Process -Filter "ProcessId=$($serverOwnerIds[0])" -ErrorAction SilentlyContinue
        if (-not (Test-ProcessOwnsServerCommand $serverOwner $serverExe 'serve')) {
            throw "FUT/Blaze ports belong to PID $($serverOwnerIds[0]), which is not '$serverExe serve'. No foreign server will be stopped."
        }
        if (Test-HealthyLocalMockServer $serverBuildHash) {
            if ($localSessionTest -or $tlsProbeMode -ne 'off' -or $blazeTlsProbe -or $advertiseHostname) {
                throw 'A local session or TLS probe needs a fresh server. Stop UltimateTeam normally, then retry.'
            }
            if ($selectedLSXMode -eq 'replacement' -and -not $localServerOwnsLSX) {
                throw 'A healthy local FUT/Blaze server is already running without the required replacement LSX listener. Run Stop-UltimateTeam.ps1, then retry.'
            }
            $reuseHealthyServer = $true
            Write-StatusLine 'Server' 'Reusing local FUT/Blaze services with the current auth bootstrap profile' Green
        } else {
            Write-StatusLine 'Server' "Restarting this Aurora 21 server (PID $($serverOwnerIds[0])) for the current auth bootstrap profile" Yellow
            Stop-Process -Id $serverOwnerIds[0] -Force -ErrorAction Stop
            $releaseDeadline = (Get-Date).AddSeconds(10)
            do {
                $remainingListeners = @(Get-NetTCPConnection -State Listen -OwningProcess $serverOwnerIds[0] -ErrorAction SilentlyContinue)
                if (-not $remainingListeners) { break }
                Start-Sleep -Milliseconds 200
            } while ((Get-Date) -lt $releaseDeadline)
            if ($remainingListeners -or (Get-Listener 44321) -or (Get-Listener 8090)) {
                throw 'The stale Aurora 21 server did not release its ports within 10 seconds, or another process claimed them.'
            }
        }
    }
    if (-not $SafeGameLaunch) {
        $existingRedirector = Get-Listener 42230
        if ($existingRedirector) {
            $redirectorOwner = Get-CimInstance Win32_Process -Filter "ProcessId=$($existingRedirector.OwningProcess)" -ErrorAction SilentlyContinue
            if (-not (Test-ProcessOwnsServerCommand $redirectorOwner $serverExe 'redirector')) {
                $ownerName = if ($redirectorOwner) { [string]$redirectorOwner.Name } else { 'an unknown process' }
                throw "TLS redirector port 42230 is owned by $ownerName (PID $($existingRedirector.OwningProcess)), not this Aurora 21 runtime."
            }

            Write-StatusLine 'Redirector' "Restarting existing local TLS redirector (PID $($existingRedirector.OwningProcess))" Yellow
            Stop-Process -Id $existingRedirector.OwningProcess -Force -ErrorAction Stop
            Wait-Process -Id $existingRedirector.OwningProcess -Timeout 5 -ErrorAction SilentlyContinue
            $releaseDeadline = (Get-Date).AddSeconds(5)
            while ((Get-Listener 42230) -and (Get-Date) -lt $releaseDeadline) {
                Start-Sleep -Milliseconds 100
            }
            if (Get-Listener 42230) {
                throw 'The existing Aurora 21 TLS redirector did not release port 42230 after it was stopped.'
            }
        }
    }

    $serverLog = Join-Path $RuntimeRoot 'server.log'
    $serverError = Join-Path $RuntimeRoot 'server.error.log'
    $redirectorLog = Join-Path $RuntimeRoot 'redirector.log'
    $redirectorError = Join-Path $RuntimeRoot 'redirector.error.log'
    if (-not $reuseHealthyServer) {
        # A club database left running by a crashed server still holds that server's inherited
        # log handles; stop it so the new server starts (and owns) a clean cluster.
        $databaseState = & $serverExe postgres stop --data-root $DataRoot 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not stop a leftover local club database: $databaseState" }
        $serverArgs = @('serve', '--lsx-mode', $serverLSXMode, '--data-root', ('"{0}"' -f $DataRoot))
        $serverArgs += @('--panel-api-port', $panelPort)
        if ($tlsProbeMode -ne 'off') {
            $serverArgs += @('--tls-probe-mode', $tlsProbeMode, '--tls-probe-port', $tlsProbePort)
        }
        if ($blazeTlsProbe) {
            $serverArgs += @('--blaze-tls-probe-port', $blazeTlsProbePort)
        }
        if ($advertiseHostname) {
            $serverArgs += '--advertise-hostname'
        }
        if ($localSessionTest) {
            $script:localTestCode = New-LocalTestCode
            $serverArgs += @('--player-identity-mode', 'session', '--local-session-test')
            # Start-Process passes this environment only to the new server. It is never an HTTP issuer,
            # command-line argument, log entry, or file; the server clears its copy after registration.
            $env:ULTIMATETEAM21_LOCAL_TEST_CODE = $script:localTestCode
        }
        try {
            $server = Start-Process -FilePath $serverExe -ArgumentList $serverArgs -WorkingDirectory $VendorRoot -RedirectStandardOutput $serverLog -RedirectStandardError $serverError -WindowStyle Hidden -PassThru
        } finally {
            Remove-Item Env:ULTIMATETEAM21_LOCAL_TEST_CODE -ErrorAction SilentlyContinue
        }
        # The very first start also creates the club database cluster.
        if (-not (Wait-ForPort 44321 90) -or -not (Wait-ForPort 8090 30) -or -not (Wait-ForPort 10853 30) -or
            ($tlsProbeMode -ne 'off' -and -not (Wait-ForPort $tlsProbePort 30)) -or
            @($tlsProbeExtraPorts | Where-Object { -not (Wait-ForPort $_ 30) }).Count -gt 0 -or
            ($blazeTlsProbe -and -not (Wait-ForPort $blazeTlsProbePort 30))) {
            if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
            throw "FUT server did not become ready on required ports, including TLS probes $tlsProbePort/$blazeTlsProbePort when selected. See $serverError"
        }
        $startedListener = Get-Listener 8090
        $startedOwner = if ($startedListener) {
            Get-CimInstance Win32_Process -Filter "ProcessId=$($startedListener.OwningProcess)" -ErrorAction SilentlyContinue
        }
        if (-not (Test-ProcessOwnsServerCommand $startedOwner $serverExe 'serve') -or $startedOwner.ProcessId -ne $server.Id) {
            throw 'The FUT listener was not created by the exact Aurora 21 server just launched.'
        }
        $ownedPorts = @(44321, 8090, 10853)
        if ($tlsProbeMode -ne 'off') { $ownedPorts += $tlsProbePort }
        $ownedPorts += $tlsProbeExtraPorts
        if ($blazeTlsProbe) { $ownedPorts += $blazeTlsProbePort }
        foreach ($port in $ownedPorts) {
            $listener = Get-Listener $port
            if (-not $listener -or $listener.OwningProcess -ne $server.Id) {
                throw "Required port $port was not owned by the Aurora 21 server just started (PID $($server.Id))."
            }
        }
        if (-not (Test-HealthyLocalMockServer $serverBuildHash)) {
            throw "The new Aurora 21 server did not expose the current auth bootstrap profile. See $serverError"
        }
    }
    if ($selectedLSXMode -eq 'replacement' -and -not (Wait-ForPort 3216)) {
        Stop-StartedRuntime $server $redirector
        throw "Selected LSX mode '$selectedLSXMode' did not produce a listener on 127.0.0.1:3216. See $serverError"
    }
    if ($selectedLSXMode -eq 'replacement') {
        $expectedLSXOwnerPid = if ($server) { $server.Id } elseif ($lsxListener) { $lsxListener.OwningProcess } else { $null }
        $lsxAfterStart = Get-Listener 3216
        if (-not $lsxAfterStart -or $lsxAfterStart.OwningProcess -ne $expectedLSXOwnerPid) {
            Stop-StartedRuntime $server $redirector
            throw 'Replacement LSX listener was not owned by the local FUT server process.'
        }
        $status.lsx = "LOCAL REPLACEMENT READY (PID $expectedLSXOwnerPid, 127.0.0.1:3216)"
    }
    Write-StatusLine 'LSX' $status.lsx Green
    if (-not $SafeGameLaunch) {
        $redirectorArgs = @('redirector', '--data-root', ('"{0}"' -f $DataRoot))
        if ($blazeTlsProbe) {
            $redirectorArgs += @('--blaze-tls-port', $blazeTlsProbePort)
        }
        $redirector = Start-Process -FilePath $serverExe -ArgumentList $redirectorArgs -WorkingDirectory $VendorRoot -RedirectStandardOutput $redirectorLog -RedirectStandardError $redirectorError -WindowStyle Hidden -PassThru
        if (-not (Wait-ForPort 42230 45)) {
            if (-not $redirector.HasExited) { Stop-Process -Id $redirector.Id -Force }
            if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
            throw "TLS redirector did not become ready on required port 42230. See $redirectorError"
        }
    }

    $status.server = if ($SafeGameLaunch) { "READY (reused/started Blaze 44321 and FUT 8090; TLS redirector intentionally skipped; LSX $selectedLSXMode)" } else { "READY (Blaze 44321, FUT 8090, TLS redirector 42230; LSX $selectedLSXMode)" }
    if ($tlsProbeMode -ne 'off') {
        $status.detail += "Loopback HTTPS probe: $tlsProbeMode on spring18.gosredirector.ea.com:$tlsProbePort$(if ($tlsProbeExtraPorts) { " (EASW/SSF on $($tlsProbeExtraPorts -join '/'))" })."
    }
    if ($blazeTlsProbe) {
        $status.detail += "Loopback Blaze TLS probe: redirector advertises secure Blaze on spring18.gosredirector.ea.com:$blazeTlsProbePort."
    }
    Write-StatusLine 'Server' $status.server Green

    if ($NoGameLaunch) {
        $status.game = 'SKIPPED by -NoGameLaunch'
        $status.authBridge = 'NOT VERIFIED (services-only -NoGameLaunch)'
        $status.connection = 'LOCAL SERVICES ONLY; game authentication not verified; hosts redirect not installed'
        Write-StatusLine 'Game' $status.game Yellow
        Write-StatusLine 'Auth bridge' $status.authBridge Yellow
        Write-StatusLine 'Connection' $status.connection Yellow
    } else {
        $resolvedGame = Resolve-GamePath $GamePath $settings
        if (-not $resolvedGame -or -not (Test-Path -LiteralPath $resolvedGame -PathType Leaf)) {
            $status.game = 'NOT FOUND'
            $status.connection = 'WAITING — hosts redirect was not installed'
            $status.detail += "Choose FIFA21.exe in the launcher's Settings, or set gamePath in '$SettingsPath'."
            Write-StatusLine 'Game' $status.game Red
            Write-StatusLine 'Connection' $status.connection Yellow
            Write-Host "Missing prerequisite: FIFA21.exe path. Open the launcher's Settings or edit '$SettingsPath'." -ForegroundColor Red
        } else {
            $gamePreflight = Test-GameBinaryPreflight $resolvedGame
            $status.detail += "Game SHA-256: $($gamePreflight.Hash)."
            $existingGame = @(Get-Process -Name @('FIFA21', 'FIFA21_Trial') -ErrorAction SilentlyContinue)
            $existingFifa = $existingGame | Sort-Object StartTime -Descending | Select-Object -First 1
            if ($existingFifa -and -not $WaitForExternalGame) {
                throw "FIFA21.exe is already running (PID $($existingGame[0].Id)). Close it before starting a new EA App session."
            }
            if ($UpdateLeaderboardClientMods -and -not $ClientModDataPath) {
                if ($WaitForExternalGame) { throw 'Automatic leaderboard mods require this worker to launch FIFA.' }
                . (Join-Path $ProjectRoot 'Client-LeaderboardMods.ps1')
                $clientManifest = Get-UltimateTeamLeaderboardModManifest
                $ClientModDataPath = Get-UltimateTeamLeaderboardModPath -GamePath $resolvedGame -UserConfigRoot $UserConfigRoot -Manifest $clientManifest -ToolRoot (Join-Path $ProjectRoot 'client-mod-tools')
                $clientModGameArguments = '-dataPath "{0}"' -f $ClientModDataPath
            }
            $launchContext = 'direct executable'
            if ($WaitForExternalGame) {
                if ($existingFifa) {
                    $launchContext = "already-running EA App game (PID $($existingFifa.Id))"
                    Write-StatusLine 'Game' "ATTACHING to running FIFA 21 PID $($existingFifa.Id)" Cyan
                } else {
                    $launchContext = 'external FIFA 21 configuration launcher'
                    Write-StatusLine 'Game' 'WAITING - press Play in the FIFA 21 configuration launcher' Yellow
                }
            } elseif ($selectedLSXMode -eq 'ea-app' -and $settings.launchViaEAApp -ne $false) {
                $eaLauncherPath = Resolve-EALauncherPath $settings
                $launchUri = Get-EAAppLaunchUri $settings $clientModGameArguments
                $launchLog = Join-Path $RuntimeRoot 'ea-app-launch.log'
                "$(Get-Date -Format o) launcher=$eaLauncherPath uri=$launchUri" | Add-Content -LiteralPath $launchLog -Encoding utf8
                $offerId = if ($settings.eaAppOfferId) { [string]$settings.eaAppOfferId } else { '195853' }
                Write-StatusLine 'EA App' "Launching offer $offerId through origin2" Cyan
                $game = Start-Process -FilePath $eaLauncherPath -ArgumentList @($launchUri) -PassThru
                $launchContext = "EA App origin2 offer $offerId"
                if (-not (Wait-ForPort 3216 30)) {
                    throw 'EA App did not create its Origin-LSX listener on 127.0.0.1:3216 after the origin2 launch.'
                }
                $lsxAfterLaunch = Get-Listener 3216
                $lsxAfterOwner = if ($lsxAfterLaunch) {
                    Get-CimInstance Win32_Process -Filter "ProcessId=$($lsxAfterLaunch.OwningProcess)" -ErrorAction SilentlyContinue
                }
                if (-not $lsxAfterOwner -or $eaProcessNames -notcontains [string]$lsxAfterOwner.Name) {
                    throw 'Port 3216 was opened by a non-EA process during EA App mode.'
                }
                $status.lsx = "EA APP owns 127.0.0.1:3216 (PID $($lsxAfterLaunch.OwningProcess))"
            } else {
                if ($SafeGameLaunch) {
                    $gameStartOptions = @{ FilePath = $resolvedGame; WorkingDirectory = (Split-Path -Path $resolvedGame -Parent); PassThru = $true }
                    if ($clientModGameArguments) { $gameStartOptions.ArgumentList = $clientModGameArguments }
                    $game = Start-Process @gameStartOptions
                } else {
                    $suspendedGame = Start-SuspendedGame $resolvedGame $clientModGameArguments
                    $game = Get-Process -Id $suspendedGame.ProcessId -ErrorAction Stop
                    $launchContext = 'direct executable (suspended until runtime patches are ready)'
                }
            }
            $status.detail += "Game launch context: $launchContext."
            if ($ClientModDataPath) { $status.detail += "Client mod data: $ClientModDataPath (in-game verification required)." }
            $fifa = Wait-ForGameProcess
            $status.game = "RUNNING (PID $($fifa.Id); window state deferred until trust patch)"
            Write-StatusLine 'Game' $status.game Green

            if ($SafeGameLaunch) {
                $status.trustBridge = 'SKIPPED by -SafeGameLaunch'
                $status.authBridge = 'SKIPPED by -SafeGameLaunch'
                $status.connection = 'LOCAL SERVICES ONLY; TLS redirector, trust patch, auth bridge, and hosts routing skipped'
                Write-StatusLine 'Trust bridge' $status.trustBridge Yellow
                Write-StatusLine 'Auth bridge' $status.authBridge Yellow
                Write-StatusLine 'Connection' $status.connection Yellow
            } else {
            $requireOriginBootstrap = $selectedLSXMode -eq 'replacement'
            $patch = Invoke-TrustPatchUntilReady $serverExe $fifa.Id $requireOriginBootstrap
            $patch.Output | Set-Content -LiteralPath (Join-Path $RuntimeRoot 'trust-patch.log') -Encoding utf8
            $patchText = $patch.Output -join "`n"
            $gos2015Ready = $patchText -match '\[(PATCHED|ALREADY)\].*GOS 2015 Certificate Authority'
            $originBootstrapReady = -not $requireOriginBootstrap -or $patchText -match '\[(PATCHED|ALREADY)\].*Origin client bootstrap bypass'
            if ($patchText -match '\[SUCCESS\] Patched' -and $gos2015Ready -and $originBootstrapReady) {
                $status.game = "RUNNING (PID $($patch.GamePid); trust tables ready)"
                $status.trustBridge = 'PATCHED (running process only)'
                $status.authBridge = 'PREPARING native local auth helper'
                "[INFO] Preparing the local auth bridge for FIFA PID $($patch.GamePid)." |
                    Set-Content -LiteralPath (Join-Path $RuntimeRoot 'auth-bridge.log') -Encoding utf8
                Save-Status $status
                Write-StatusLine 'Auth bridge' $status.authBridge Cyan
                $authHelper = Ensure-LocalAuthBridge
                # Install routing before a suspended direct launch can resolve
                # the redirector. Flushing Windows DNS afterwards cannot clear
                # an address already cached inside FIFA.
                $localRoutingAttempted = $true
                Add-RedirectHostsEntry
                if ($suspendedGame) {
                    # The native bridge waits for unpacked code; let a direct
                    # launch unpack with trust and local routing already ready.
                    Resume-SuspendedGame $suspendedGame
                    $suspendedGame = $null
                }
                $status.authBridge = 'WAITING for native local auth readiness'
                Save-Status $status
                Invoke-LocalAuthBridge $authHelper $patch.GamePid $resolvedGame
                $script:localTestCode = $null
                $status.authBridge = 'READY (synthetic FUT code for local test; game login not yet verified)'
                $status.connection = 'LOCAL ROUTING ARMED (trust and auth bridges ready; awaiting FIFA TLS and game login)'
                Write-StatusLine 'Trust bridge' $status.trustBridge Green
                Write-StatusLine 'Auth bridge' $status.authBridge Green
                Write-StatusLine 'Connection' $status.connection Green
            } else {
                if ($suspendedGame) {
                    Stop-SuspendedGame $suspendedGame
                    $suspendedGame = $null
                }
                Stop-StartedRuntime $server $redirector
                if ($localRoutingAttempted) { Remove-RedirectHostsEntry }
                $status.server = 'STOPPED after trust bridge failure'
                $status.trustBridge = 'FAILED — see runtime\trust-patch.log'
                $status.connection = 'NOT REDIRECTED — hosts file was left unchanged'
                $status.detail += 'The exact trust-patch output is saved in runtime\trust-patch.log.'
                Write-StatusLine 'Trust bridge' $status.trustBridge Red
                Write-StatusLine 'Connection' $status.connection Red
            }
            }
        }
    }

    @{ serverPid = if ($server) { $server.Id } else { $null }; redirectorPid = if ($redirector) { $redirector.Id } else { $null }; projectRoot = $ProjectRoot } |
        ConvertTo-Json | Set-Content -LiteralPath $PidPath -Encoding utf8
    Save-Status $status
    Write-Host "Status file: $StatusPath"
} catch {
    if ($status.authBridge -match '^(PREPARING|WAITING)') {
        $status.authBridge = 'FAILED - see runtime\auth-bridge.log and launcher error'
    }
    if ($suspendedGame) {
        try { Stop-SuspendedGame $suspendedGame } catch { $status.detail += "Suspended game cleanup: $($_.Exception.Message)" }
        $suspendedGame = $null
    }
    try { Stop-StartedRuntime $server $redirector } catch { $status.detail += "Runtime cleanup: $($_.Exception.Message)" }
    if ($localRoutingAttempted) {
        try { Remove-RedirectHostsEntry } catch { $status.detail += "Hosts cleanup: $($_.Exception.Message)" }
    }
    $status.server = if ($status.server -eq 'NOT STARTED') { 'FAILED' } else { $status.server }
    $status.connection = 'NOT READY'
    $status.detail += $_.Exception.Message
    Save-Status $status
    Write-StatusLine 'ERROR' $_.Exception.Message Red
    Write-Host "Status file: $StatusPath"
    exit 1
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($launchGuard) { $launchGuard.ReleaseMutex(); $launchGuard.Dispose() }
}
