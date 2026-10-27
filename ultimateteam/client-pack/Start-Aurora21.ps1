[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$ServerAddress,
    [string]$TrustAnchors,
    [string]$LogPath,
    [string]$EAAppLauncherPath,
    [switch]$NoGameLaunch,
    [ValidateSet('auto', 'ea-app', 'direct', 'steam')]
    [string]$LaunchMethod = 'auto',
    [switch]$DetectOnly,
    [switch]$Cleanup
)

# Aurora 21 hosted play on the player's PC, run elevated by the Aurora launcher from the client pack
# (runtime.json "command"). It does for a hosted server what Launch-UltimateTeam.ps1 does for the local one in EA App
# mode: start FIFA 21, patch the running process's trust tables with this server's trust anchors (in memory only), hand
# the one-time game code from POST /client/launch to the auth bridge on standard input, and only then point
# spring18.gosredirector.ea.com at the server. It prints "[READY]" when FIFA may connect, or "[FAILED] <reason>" and
# exits non-zero. The code is read from standard input and never written to a file, a log or a command line.
# Alongside, the maths bridge (Aurora21-MathBridge.exe, see "Maths bridge" below) switches FIFA's maths to the pack's
# pinned ucrtbase.dll and reports it to the server itself; it never fails or delays the launch.
#   -Cleanup removes the hosts entry again (runtime.json "cleanup", after FIFA closes).
#   -DetectOnly prints how this copy would be started ("[READY] launch method <method> (<reason>)") and changes nothing.
# Exit codes: 0 ready, 2 bad input, 3 game not started or refused, 4 trust patch failed, 5 auth bridge failed, 6 hosts.
#
# How the game starts depends on where this copy came from (-LaunchMethod auto, the default, decides):
#   ea-app  a copy the EA app installed (its __Installer data, or EA's registry entry naming this folder): started with
#           the EA app's origin2 handler; the EA app owns the Origin client, so the trust patch leaves it alone.
#           FIFA's setup window is turned off first (FIFASetup\config.ini AUTO_LAUNCH = 1), and a FIFA that hands over
#           to a new process during startup is followed: the new one is patched and bridged before [READY].
#   steam   a copy under steamapps\common: started through Steam, which hands it to the EA app; patched as ea-app.
#   direct  anything else, including an emulated copy (anadius files or a _crack folder), which is never sent to the EA
#           app even when it is installed: FIFA21.exe starts suspended, the trust patch also bypasses the Origin client
#           bootstrap check (the local launcher's replacement mode), and the game resumes before the auth bridge.

$ErrorActionPreference = 'Stop'
function Take-HostedDiscordUsername {
    $username = [Environment]::GetEnvironmentVariable('AURORA21_DISCORD_USERNAME', 'Process')
    [Environment]::SetEnvironmentVariable('AURORA21_DISCORD_USERNAME', $null, 'Process')
    return $username
}
$discordUsername = Take-HostedDiscordUsername
$PackRoot = $PSScriptRoot
$HostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$RedirectHosts = @(
    'spring18.gosredirector.ea.com',
    'spring18.gosredirector.stest.ea.com',
    'spring18.gosredirector.scert.ea.com',
    'spring18.gosredirector.sdev.ea.com'
)

function Fail([int]$Code, [string]$Message) {
    Write-Output "[FAILED] $Message"
    exit $Code
}

# Writes the hosts file even when it is marked read-only or hidden (ad-block and "block activation" guides tell people to
# set read-only, and Windows then answers even an administrator "Access to the path is denied"); the attributes are put
# back afterwards so the player's protection stays as they left it.
function Write-HostsLines([string[]]$Lines) {
    $item = Get-Item -LiteralPath $HostsPath -Force -ErrorAction Stop
    $original = $item.Attributes
    $blocking = [IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System
    if ($original -band $blocking) { $item.Attributes = $original -band (-bnot $blocking) }
    try {
        # UTF-8 without a byte order mark: plain ASCII stays byte-identical, and a player's non-English comments survive
        # (Set-Content -Encoding ascii turned them into '?').
        [IO.File]::WriteAllLines($HostsPath, [string[]]$Lines, (New-Object Text.UTF8Encoding($false)))
    } finally {
        if ($original -band $blocking) { try { (Get-Item -LiteralPath $HostsPath -Force).Attributes = $original } catch { } }
    }
}

# The hosts lines without any of the redirect names. Windows uses the first line naming a host, so every earlier
# redirect must go, not only "<ip> <name>" lines: a trailing comment, several names on one line (block lists and
# "block activation" guides write "0.0.0.0 spring18.gosredirector.ea.com gosredirector.ea.com") or other case kept the
# old address ahead of ours and every launch failed "resolves to 0.0.0.0". Other names on such a line are kept.
function Remove-RedirectNames([string[]]$Lines) {
    foreach ($line in $Lines) {
        $hash = $line.IndexOf('#')
        $content = if ($hash -ge 0) { $line.Substring(0, $hash) } else { $line }
        $fields = @($content -split '\s+' | Where-Object { $_ })
        $names = @($fields | Select-Object -Skip 1)
        $kept = @($names | Where-Object { $RedirectHosts -notcontains $_.TrimEnd('.') })
        if ($fields.Count -lt 2 -or $kept.Count -eq $names.Count) { $line; continue }
        if ($kept.Count -eq 0) { continue }
        $rebuilt = "$($fields[0]) $($kept -join ' ')"
        if ($hash -ge 0) { $rebuilt += ' ' + $line.Substring($hash) }
        $rebuilt
    }
}

function Remove-RedirectHostsEntry {
    $lines = @(Get-Content -LiteralPath $HostsPath -ErrorAction Stop)
    $kept = @(Remove-RedirectNames $lines)
    if (($kept -join "`n") -cne ($lines -join "`n")) {
        Write-HostsLines $kept
        ipconfig /flushdns | Out-Null
    }
}

# Earlier runs of this script still alive (a launch stuck behind antivirus, a cleanup that never finished) hold the
# hosts file open, and every later launch failed "Held open by: Windows PowerShell". Only this pack's own script is
# stopped, never another program.
function Stop-StaleAuroraStarts {
    $script = [IO.Path]::GetFileName($PSCommandPath)
    if (-not $script) { return @() }
    $stale = @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe' OR Name = 'pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.IndexOf($script, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    foreach ($process in $stale) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    return @($stale | ForEach-Object { $_.ProcessId })
}

# Replaces any earlier redirect (a local run's 127.0.0.1 included) with this server's address, then checks it resolves.
# Antivirus hosts-file protection can hold the file open for several seconds (a player's launches failed 6 times in 16
# with the old 2 s of retries), so this keeps trying for about 20 s and then names whoever holds it.
function Set-RedirectHostsEntry([string]$Address) {
    $lastError = $null
    $denied = $false
    $readDenied = $false
    $staleStopped = $false
    $attempts = 20
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $reading = $true
        try {
            $kept = @(Remove-RedirectNames @(Get-Content -LiteralPath $HostsPath -ErrorAction Stop))
            $reading = $false
            $entries = @($RedirectHosts | ForEach-Object { "$Address $_" })
            Write-HostsLines ($kept + $entries)
            ipconfig /flushdns | Out-Null
            $resolved = @([System.Net.Dns]::GetHostAddresses($RedirectHosts[0]) | ForEach-Object { $_.IPAddressToString })
            if ($resolved -notcontains $Address) { throw "$($RedirectHosts[0]) resolves to $($resolved -join ', '), not $Address" }
            return
        } catch {
            $lastError = $_.Exception.Message
            if ($_.Exception -is [UnauthorizedAccessException] -or $_.Exception.InnerException -is [UnauthorizedAccessException]) {
                $denied = $true
                if ($reading) { $readDenied = $true }
            } elseif (-not $staleStopped -and $_.Exception -is [IO.IOException]) {
                $staleStopped = $true
                if (@(Stop-StaleAuroraStarts).Count -gt 0) { Start-Sleep -Milliseconds 500; continue }
            }
            if ($attempt -lt $attempts) { Start-Sleep -Milliseconds 1000 }
        }
    }
    if ($readDenied) {
        throw "Hosts file write failed: Windows will not even let an administrator read $HostsPath, so its permissions were changed (a hosts-lock tool or guide does this). Fix it once from an administrator Command Prompt: takeown /f `"$HostsPath`" /a, then icacls `"$HostsPath`" /reset, then start Aurora again. If those commands are refused too, a security program is protecting the hosts file: allow the Aurora launcher in it."
    }
    $holders = @(Get-FileHolders $HostsPath | Where-Object { $_ -notmatch "\(PID $PID\)$" })
    $held = if ($holders) { " Held open by: $($holders -join ', ') (allow the hosts file in that program, or pause it while starting)." }
        elseif ($denied) { " Windows refused the write even as administrator: a security program is protecting the hosts file (for example Malwarebytes, Bitdefender, Kaspersky, Norton, Avast/AVG, Spybot or HostsMan). Allow the Aurora launcher in it, or turn its hosts-file protection off while starting." }
        else { '' }
    throw "Hosts file write failed after $attempts attempts: $lastError$held"
}

# The processes that hold a file open, from the Windows Restart Manager (names and PIDs only); empty when unknown.
function Get-FileHolders([string]$Path) {
    try {
        if (-not ('UltimateTeamFileHolders' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class UltimateTeamFileHolders
{
    [StructLayout(LayoutKind.Sequential)]
    struct UniqueProcess { public int ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int AppType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, string key);
    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint files, string[] names, uint processes, IntPtr ids, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] info, ref uint reasons);

    public static string[] Of(string path)
    {
        var result = new List<string>();
        uint session;
        if (RmStartSession(out session, 0, Guid.NewGuid().ToString()) != 0) return result.ToArray();
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0) return result.ToArray();
            uint needed, count = 0, reasons = 0;
            int status = RmGetList(session, out needed, ref count, null, ref reasons);
            if (status != 234 || needed == 0) return result.ToArray();
            var info = new ProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, ref reasons) != 0) return result.ToArray();
            for (int i = 0; i < count; i++) result.Add(info[i].AppName + " (PID " + info[i].Process.ProcessId + ")");
        }
        finally { RmEndSession(session); }
        return result.ToArray();
    }
}
"@
        }
        return @([UltimateTeamFileHolders]::Of($Path))
    } catch { return @() }
}

if (-not ('UltimateTeamNativeProcess' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct UltimateTeamStartupInfo
{
    public int cb;
    public string lpReserved;
    public string lpDesktop;
    public string lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
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
    public static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref UltimateTeamStartupInfo startupInfo, out UltimateTeamProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
"@
}

# A direct launch starts FIFA21.exe suspended so the trust patch (with the Origin client bootstrap bypass) lands before
# the game runs (Launch-UltimateTeam.ps1's replacement mode).
function Start-SuspendedGame([string]$Path) {
    $startup = [UltimateTeamStartupInfo]::new()
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf([type][UltimateTeamStartupInfo])
    $processInfo = [UltimateTeamProcessInformation]::new()
    $commandLine = [Text.StringBuilder]::new(('"{0}"' -f $Path))
    $started = [UltimateTeamNativeProcess]::CreateProcessW($Path, $commandLine, [IntPtr]::Zero, [IntPtr]::Zero, $false,
        [UltimateTeamNativeProcess]::CreateSuspended, [IntPtr]::Zero, (Split-Path -Path $Path -Parent), [ref]$startup, [ref]$processInfo)
    if (-not $started) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    return [pscustomobject]@{ ProcessId = [int]$processInfo.dwProcessId; ProcessHandle = $processInfo.hProcess; ThreadHandle = $processInfo.hThread }
}

function Close-SuspendedGameHandles([object]$SuspendedGame) {
    if (-not $SuspendedGame) { return }
    if ($SuspendedGame.ThreadHandle -ne [IntPtr]::Zero) { [void][UltimateTeamNativeProcess]::CloseHandle($SuspendedGame.ThreadHandle); $SuspendedGame.ThreadHandle = [IntPtr]::Zero }
    if ($SuspendedGame.ProcessHandle -ne [IntPtr]::Zero) { [void][UltimateTeamNativeProcess]::CloseHandle($SuspendedGame.ProcessHandle); $SuspendedGame.ProcessHandle = [IntPtr]::Zero }
}

function Resume-SuspendedGame([object]$SuspendedGame) {
    if ([UltimateTeamNativeProcess]::ResumeThread($SuspendedGame.ThreadHandle) -eq [uint32]::MaxValue) {
        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    Close-SuspendedGameHandles $SuspendedGame
}

function Stop-SuspendedGame([object]$SuspendedGame) {
    if (-not $SuspendedGame) { return }
    Stop-Process -Id $SuspendedGame.ProcessId -Force -ErrorAction SilentlyContinue
    Close-SuspendedGameHandles $SuspendedGame
}

# How this copy has to be started, and why (see the header). Emulator markers win over everything: such a copy is never
# sent to the EA app, even when the EA app is installed on the PC.
function Resolve-LaunchMethod([string]$Path) {
    $folder = [IO.Path]::GetFullPath((Split-Path -Parent $Path)).TrimEnd('\')
    foreach ($marker in 'anadius64.dll', 'anadius32.dll', 'anadius.cfg') {
        if (Test-Path -LiteralPath (Join-Path $folder $marker) -PathType Leaf) { return @('direct', "an emulated copy: $marker") }
    }
    if (Test-Path -LiteralPath (Join-Path $folder '_crack') -PathType Container) { return @('direct', 'an emulated copy: _crack folder') }
    if ($folder -match '\\steamapps\\common\\') { return @('steam', 'a Steam copy') }
    if (Test-Path -LiteralPath (Join-Path $folder '__Installer\installerdata.xml') -PathType Leaf) { return @('ea-app', 'an EA app install: __Installer') }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\EA Games\FIFA 21', 'HKLM:\SOFTWARE\EA Games\FIFA 21') {
        $dir = [string](Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).'Install Dir'
        if ($dir -and [IO.Path]::GetFullPath($dir).TrimEnd('\') -ieq $folder) { return @('ea-app', 'an EA app install: registry') }
    }
    return @('direct', 'no EA app, Steam or emulator markers')
}

# The EA app starts this game through FIFA's own launcher (FIFASetup\fifaconfig.exe, installerdata.xml), which shows
# its settings window unless its config.ini says AUTO_LAUNCH = 1. On some PCs that window waits for its Play button,
# which starts a second FIFA21.exe after this script patched the first: the game that connects is unpatched and has no
# code ("Unable to connect to the EA servers"). With AUTO_LAUNCH = 1 fifaconfig starts FIFA21.exe at once, passing on
# the EA app's arguments. fifaconfig reads each "key = value" line into a dictionary that throws on a repeated key
# (case-sensitive), so an existing AUTO_LAUNCH line is rewritten, never another added. Only an existing config.ini is
# touched: without one fifaconfig would launch its built-in default (fifa17.exe). Bytes are kept as they are (Latin-1
# round trip). Returns $true when the file changed.
function Enable-FifaLauncherAutoLaunch([string]$Path) {
    $config = Join-Path (Split-Path -Parent $Path) 'FIFASetup\config.ini'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { return $false }
    $encoding = [Text.Encoding]::GetEncoding(28591)
    $text = $encoding.GetString([IO.File]::ReadAllBytes($config))
    $lines = [regex]::Split($text, '(?<=\n)') | Where-Object { $_.Length -gt 0 }
    $updated = [Text.StringBuilder]::new()
    $found = $false
    foreach ($line in $lines) {
        if ($line -cnotmatch '^\s*AUTO_LAUNCH\s*=') { [void]$updated.Append($line); continue }
        if ($found) { continue }
        $found = $true
        $ending = if ($line.EndsWith("`r`n")) { "`r`n" } elseif ($line.EndsWith("`n")) { "`n" } else { '' }
        [void]$updated.Append('AUTO_LAUNCH = 1' + $ending)
    }
    if (-not $found) {
        $newline = if ($text.Contains("`r`n") -or -not $text.Contains("`n")) { "`r`n" } else { "`n" }
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { [void]$updated.Append($newline) }
        [void]$updated.Append('AUTO_LAUNCH = 1' + $newline)
    }
    $result = $updated.ToString()
    if ($result -ceq $text) { return $false }
    $attributes = [IO.File]::GetAttributes($config)
    $readOnly = ($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0
    if ($readOnly) {
        $writable = $attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        if ($writable -eq 0) { $writable = [IO.FileAttributes]::Normal }
        [IO.File]::SetAttributes($config, [IO.FileAttributes]$writable)
    }
    try { [IO.File]::WriteAllBytes($config, $encoding.GetBytes($result)) }
    finally { if ($readOnly) { [IO.File]::SetAttributes($config, $attributes) } }
    return $true
}

# Only an anadius direct copy has this local VDF-style identity file. Operate on bytes so UTF-8 BOM,
# non-ASCII comments and line endings survive unchanged; the replacement username is ASCII.
function Sync-DirectGameUsername([string]$Path, [string]$Username) {
    if ([string]::IsNullOrEmpty($Username)) { return $false }
    $config = Join-Path (Split-Path -Parent $Path) 'anadius.cfg'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { return $false }
    if ($Username -cnotmatch '^[A-Za-z0-9._]{2,32}$') {
        throw [ArgumentException]::new('The verified Discord username is not supported by this local profile format.')
    }

    $encoding = [Text.Encoding]::GetEncoding(28591)
    $original = [IO.File]::ReadAllBytes($config)
    $text = $encoding.GetString($original)
    $keys = [regex]::Matches($text, '(?im)^[ \t]*"Username"(?=[ \t]|$)')
    $lines = [regex]::Matches($text, '(?im)^(?<prefix>[ \t]*"Username"[ \t]+")(?<value>[^"\r\n]*)(?<suffix>"[ \t]*(?://[^\r\n]*)?)(?=\r?$)')
    if ($keys.Count -ne 1 -or $lines.Count -ne 1) {
        throw [IO.InvalidDataException]::new('The local profile must contain exactly one plain quoted Username line.')
    }
    $value = $lines[0].Groups['value']
    if ($value.Value -ceq $Username) { return $false }
    $updated = $text.Substring(0, $value.Index) + $Username + $text.Substring($value.Index + $value.Length)
    $temporary = Join-Path (Split-Path -Parent $config) ('anadius.cfg.aurora21.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = $config + '.aurora21.bak'
    $repeatBackup = $temporary + '.bak'
    $originalAttributes = [IO.File]::GetAttributes($config)
    $restoreAttributes = $false
    try {
        [IO.File]::WriteAllBytes($temporary, $encoding.GetBytes($updated))
        # Refuse if the profile changed while preparing the replacement.
        $current = [IO.File]::ReadAllBytes($config)
        if ([Convert]::ToBase64String($current) -cne [Convert]::ToBase64String($original)) {
            throw [IO.IOException]::new('The local profile changed while preparing the update.')
        }
        if ([IO.File]::GetAttributes($config) -ne $originalAttributes) {
            throw [IO.IOException]::new('The local profile attributes changed while preparing the update.')
        }
        # ReplaceFile refuses a read-only destination. Clear only that bit, only after the
        # single field and unchanged bytes have been verified; finally restores all original flags.
        if (($originalAttributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            $writableAttributes = $originalAttributes -band (-bnot [IO.FileAttributes]::ReadOnly)
            if ($writableAttributes -eq 0) { $writableAttributes = [IO.FileAttributes]::Normal }
            [IO.File]::SetAttributes($config, [IO.FileAttributes]$writableAttributes)
            $restoreAttributes = $true
            $current = [IO.File]::ReadAllBytes($config)
            if ([Convert]::ToBase64String($current) -cne [Convert]::ToBase64String($original)) {
                throw [IO.IOException]::new('The local profile changed before the update.')
            }
        }
        $backupTarget = if (Test-Path -LiteralPath $backup) { $repeatBackup } else { $backup }
        [IO.File]::Replace($temporary, $config, $backupTarget)
        $restoreAttributes = $true
        if ($backupTarget -eq $backup) { [IO.File]::SetAttributes($backup, $originalAttributes) }
    } finally {
        try {
            if ($restoreAttributes -and (Test-Path -LiteralPath $config -PathType Leaf)) {
                [IO.File]::SetAttributes($config, $originalAttributes)
            }
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            if (Test-Path -LiteralPath $repeatBackup) { Remove-Item -LiteralPath $repeatBackup -Force }
        }
    }
    return $true
}

function Test-TrustPatched([string]$Text, [bool]$PatchOrigin) {
    ($Text -match '\[SUCCESS\] Patched') -and ($Text -match '\[(PATCHED|ALREADY)\].*GOS 2015 Certificate Authority') -and
        (-not $PatchOrigin -or $Text -match '\[(PATCHED|ALREADY)\].*Origin client bootstrap bypass')
}

function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The same refusal as the local launcher: a FIFA21.exe carrying the known malformed four-byte patch is never run.
function Test-GameBinaryPreflight([string]$Path) {
    $knownMalformedHash = 'E1022A950C08724E126493D60222CE763D510D1C6EBD308506860F019BC02763'
    $knownBuildLength = 571958064
    $knownPatchOffset = 0xDBFD82
    $knownMalformedBytes = '66 E9 18 01'
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() -eq $knownMalformedHash) {
        throw 'This FIFA21.exe matches the known malformed four-byte patch. Repair the game in the EA App first.'
    }
    if ($file.Length -eq $knownBuildLength) {
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            [void]$stream.Seek($knownPatchOffset, [IO.SeekOrigin]::Begin)
            $bytes = [byte[]]::new(4)
            $read = $stream.Read($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
        if ($read -eq 4 -and [BitConverter]::ToString($bytes).Replace('-', ' ') -eq $knownMalformedBytes) {
            throw 'This FIFA21.exe contains the known malformed bytes at 0xDBFD82. Repair the game in the EA App first.'
        }
    }
}

function Get-NewestGameProcess {
    Get-Process -Name @('FIFA21', 'FIFA21_Trial') -ErrorAction SilentlyContinue |
        Where-Object { -not $_.HasExited } | Sort-Object StartTime -Descending | Select-Object -First 1
}

# Best-effort: a copy whose setup window cannot be turned off still starts (the handover watch below covers it).
function Write-FifaLauncherAutoLaunch {
    try {
        if (Enable-FifaLauncherAutoLaunch $GamePath) { Write-Output "[INFO] FIFA's setup window turned off (FIFASetup\config.ini AUTO_LAUNCH = 1)" }
    } catch { Write-Output "[INFO] FIFA's setup window could not be turned off: $($_.Exception.Message)" }
}

function Test-GameRunning([int]$GamePid) {
    [bool](Get-Process -Id $GamePid -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
}

# Through the EA app or Steam, the FIFA21.exe this launch patched can still hand over to a new one (FIFA's setup window
# Play button, an EA relaunch), which would start unpatched and without the code. Watches the patched process for
# $SettleSeconds (it went within 15 s in the launcher logs that showed it) and returns $null while it keeps running, or
# the FIFA process that replaced it. A replacement can wait for a click in FIFA's setup window, hence the longer wait.
function Wait-GameHandover([int]$GamePid, [int]$SettleSeconds = 30, [int]$ReplacementSeconds = 120) {
    $settle = (Get-Date).AddSeconds($SettleSeconds)
    while ((Get-Date) -lt $settle -and (Test-GameRunning $GamePid)) { Start-Sleep -Milliseconds 500 }
    if (Test-GameRunning $GamePid) { return $null }
    $deadline = (Get-Date).AddSeconds($ReplacementSeconds)
    while ((Get-Date) -lt $deadline) {
        $next = Get-NewestGameProcess
        if ($next -and $next.Id -ne $GamePid) { return $next }
        Start-Sleep -Milliseconds 500
    }
    throw "FIFA 21 (PID $GamePid) closed during startup and no new FIFA 21 followed within $ReplacementSeconds seconds."
}

# --- Maths bridge ------------------------------------------------------------------------------------------------
# FIFA 21 takes its maths (acosf, atan2f, pow, ...) from Windows' ucrtbase.dll, which Windows 11 24H2 changed in the
# last bit, so two PCs either side of that line desync online and the server pairs searchers by maths group. A copy of
# ucrtbase.dll next to FIFA21.exe never helps (Windows 10/11 always load the system UCRT). Aurora21-MathBridge.exe
# instead loads the pack's pinned Microsoft ucrtbase.dll (ucrt\, Windows SDK redist 10.0.26100.1742) into the running
# FIFA by full path and points FIFA's 50 maths imports at it: memory only, this process only, every player alike.
#   - It starts as soon as FIFA exists (a direct launch is still suspended), polls until the Windows loader has mapped
#     ucrtbase, and switches the import table before FIFA's startup code copies it (or both tables, if FIFA got there
#     first). It touches FIFA's memory alongside the trust patch (which writes other tables) and the auth bridge. Its
#     NtSuspendProcess/NtResumeProcess pairs only start once FIFA runs, and suspend counts nest, so they neither resume
#     a game this script holds suspended nor cut into the auth bridge's own suspended install.
#   - After the switch has held for 20 s it reports [READY] and [MATH] v=1; ucrt=...; sha256=...; fma3=<its own
#     _get_FMA3_enable> to its log, then sends that value itself: POST /client/launch/math with the launch code. The
#     code is handed over on its standard input once this launch is ready, and only to the helper of the process the
#     trust patch and auth bridge used; the server accepts it until the code expires (it is not used up by FIFA's login).
#   - That is usually after [READY]: the helper outlives this script and never holds the launcher's output pipes.
#   - Best-effort: a missing helper, an unsupported FIFA build, a timeout or a failed report never fails or delays the
#     launch; the server then pairs the player by Windows build. Its log is {logs}\math-bridge.log.
$MathBridgeHelper = Join-Path $PackRoot 'Aurora21-MathBridge.exe'
$MathBridgeLibrary = Join-Path $PackRoot 'ucrt\ucrtbase.dll'
$MathReportHosts = @('21.aurorafut.com', '21.onlyonemzy.com', '21.aurcdn.com')   # the server's HTTPS front (Caddy)

# The helper's command line: the game, the pinned library, its log and the report URLs. Never the code (standard input).
function Get-MathBridgeArguments([int]$GamePid, [string]$GameExe, [string]$LogFile) {
    $arguments = '--pid {0} --game "{1}" --dll "{2}" --log "{3}" --code-stdin' -f $GamePid, $GameExe, $MathBridgeLibrary, $LogFile
    foreach ($reportHost in $MathReportHosts) { $arguments += ' --report "https://{0}/client/launch/math"' -f $reportHost }
    return $arguments
}

# Starts a child that inherits none of this script's own standard handles (the launcher's pipes): a helper still
# running after [READY] must not keep the launcher's view of this command open. Its own standard handles are new pipes.
function Start-DetachedHelper([Diagnostics.ProcessStartInfo]$StartInfo) {
    $saved = @()
    foreach ($standard in -10, -11, -12) {   # STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE
        $handle = [UltimateTeamNativeProcess]::GetStdHandle($standard)
        [uint32]$flags = 0
        if ($handle -ne [IntPtr]::Zero -and $handle -ne [IntPtr]::new(-1) -and
            [UltimateTeamNativeProcess]::GetHandleInformation($handle, [ref]$flags) -and ($flags -band 1) -and
            [UltimateTeamNativeProcess]::SetHandleInformation($handle, 1, 0)) { $saved += $handle }
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $StartInfo
    try { [void]$process.Start() }
    finally { foreach ($handle in $saved) { [void][UltimateTeamNativeProcess]::SetHandleInformation($handle, 1, 1) } }
    return $process
}

function Start-MathBridge([int]$GamePid, [string]$LogFile) {
    foreach ($file in $MathBridgeHelper, $MathBridgeLibrary) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "$(Split-Path -Leaf $file) is not in this client pack" }
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $MathBridgeHelper
    $startInfo.Arguments = Get-MathBridgeArguments $GamePid $GamePath $LogFile
    $startInfo.WorkingDirectory = $PackRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    # Unread: the helper writes to its log; these only take an early error (read back by Get-MathBridgeStatus).
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = Start-DetachedHelper $startInfo
    return [pscustomobject]@{ Process = $process; GamePid = $GamePid; Log = $LogFile }
}

# Hands the launch code to the helper of the FIFA process this launch patched, and closes its standard input either
# way: a helper for any other process (an EA stub that exited) gets no code and reports nothing.
function Submit-MathBridgeCode([object]$Bridge, [int]$GamePid, [string]$Code) {
    if (-not $Bridge) { return }
    try {
        if ($Bridge.GamePid -eq $GamePid -and $Code -and -not $Bridge.Process.HasExited) { $Bridge.Process.StandardInput.WriteLine($Code) }
    } catch {
    } finally {
        try { $Bridge.Process.StandardInput.Close() } catch { }
    }
}

# The helper's outcome from its log and exit code, as one [INFO] line.
function Format-MathBridgeResult([string]$Text, [int]$ExitCode) {
    $math = $null
    if ($Text -match '(?m)^\[MATH\] ([^\r\n]+)') { $math = $Matches[1] }
    if ($ExitCode -eq 0 -and $math -and $Text -match '(?m)^\[READY\] Maths bridge installed for PID \d+: \d+ maths imports use ucrtbase ') {
        if ($Text -match '(?m)^\[REPORTED\] ') { return "[INFO] maths bridge: FIFA uses the pinned ucrtbase ($math); reported" }
        return "[INFO] maths bridge: FIFA uses the pinned ucrtbase ($math) but it was not reported; pairing uses the Windows build"
    }
    $reason = if ($Text -match '(?m)^\[ERROR\] ([^\r\n]+)') { $Matches[1].TrimEnd('.') } else { "exit $ExitCode" }
    return "[INFO] maths bridge: $reason; pairing uses the Windows build"
}

# One [INFO] line for the launcher: the result when the helper has finished, else that it is still at work.
function Get-MathBridgeStatus([object]$Bridge) {
    if (-not $Bridge) { return $null }
    $log = Split-Path -Leaf $Bridge.Log
    $text = [string](Get-Content -LiteralPath $Bridge.Log -Raw -ErrorAction SilentlyContinue)
    if (-not $Bridge.Process.HasExited) {
        if ($text -match '(?m)^\[READY\] Maths bridge installed' -and $text -match '(?m)^\[MATH\] ([^\r\n]+)') {
            return "[INFO] maths bridge: FIFA uses the pinned ucrtbase ($($Matches[1])); the helper is reporting it (see $log)"
        }
        return "[INFO] maths bridge: switching FIFA's maths to the pinned ucrtbase; the helper reports it itself (see $log)"
    }
    if (-not $text) { try { $text = $Bridge.Process.StandardError.ReadToEnd() } catch { } }
    return Format-MathBridgeResult $text $Bridge.Process.ExitCode
}

# EA can expose a short-lived FIFA process before the real one: follow the newest on every pass (local launcher rule).
# -GamePid: the suspended game a direct launch created (patched as is); 0 follows the newest FIFA process instead.
# -PatchOrigin: also bypass the Origin client bootstrap check (a direct launch has no EA app to own the Origin client).
function Invoke-TrustPatchUntilReady([int]$TimeoutSeconds = 60, [int]$GamePid = 0, [bool]$PatchOrigin = $false) {
    $server = Join-Path $PackRoot 'trust\Aurora21-UltimateTeam-Server.exe'
    $env:ULTIMATETEAM_CERTIFICATE_ROOT = Join-Path $PackRoot 'trust'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $latest = $null
    do {
        $target = $GamePid
        if ($target -le 0) {
            $game = Get-NewestGameProcess
            if (-not $game) { Start-Sleep -Seconds 1; continue }
            $target = $game.Id
        }
        # Through the EA app or Steam, the EA app owns the Origin client, so its bootstrap is left alone (--skip-origin).
        $arguments = @('trust-patch', '--pid', $target)
        if (-not $PatchOrigin) { $arguments += '--skip-origin' }
        $arguments += @('--trust-anchors', $TrustAnchors)
        $output = @(& $server @arguments 2>&1)
        $latest = [pscustomobject]@{ GamePid = $target; Output = $output }
        if (Test-TrustPatched ($output -join "`n") $PatchOrigin) { return $latest }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $latest
}

function Invoke-AuthBridge([int]$GamePid, [string]$Code, [string]$BridgeLog) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $PackRoot 'Aurora21-LocalAuthBridge.exe'
    $startInfo.Arguments = '--pid {0} --game "{1}" --fut-code-stdin --hosted --log "{2}"' -f $GamePid, $GamePath, $BridgeLog
    $startInfo.WorkingDirectory = $PackRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $helper = [Diagnostics.Process]::new()
    $helper.StartInfo = $startInfo
    try {
        if (-not $helper.Start()) { throw 'The auth bridge could not be started.' }
        try { $helper.StandardInput.WriteLine($Code) } finally { $helper.StandardInput.Close() }
        if (-not $helper.WaitForExit(60000)) {
            # Never killed: it may be completing or rolling back its in-memory change (local launcher rule).
            throw "The auth bridge did not finish within 60 seconds (PID $($helper.Id) left running). See $BridgeLog"
        }
        $output = Get-Content -LiteralPath $BridgeLog -Raw -ErrorAction Stop
        if ($helper.ExitCode -ne 0 -or $output -notmatch '(?m)^\[(READY|ALREADY)\]') {
            throw "The auth bridge did not confirm readiness (exit $($helper.ExitCode)). See $BridgeLog"
        }
    } finally { $helper.Dispose() }
}

if ($DetectOnly) {
    if (-not $GamePath -or (Split-Path -Leaf $GamePath) -notin @('FIFA21.exe', 'FIFA21_Trial.exe')) { Fail 2 'GamePath must be the FIFA21.exe of the installed game.' }
    $method, $reason = if ($LaunchMethod -eq 'auto') { Resolve-LaunchMethod $GamePath } else { $LaunchMethod, 'chosen with -LaunchMethod' }
    Write-Output "[READY] launch method $method ($reason)"
    exit 0
}
if (-not (Test-Administrator)) { Fail 2 'Run elevated: the hosts file and the FIFA process can only be changed as administrator.' }
if ($Cleanup) {
    try { Remove-RedirectHostsEntry; Write-Output '[READY] hosts entry removed'; exit 0 } catch { Fail 6 $_.Exception.Message }
}

# The one-time code arrives on standard input only (from POST /client/launch).
$code = [Console]::In.ReadLine()
if ($code -notmatch '^[0-9a-f]{40}$') { Fail 2 'No valid game code on standard input.' }
$address = $null
if (-not [System.Net.IPAddress]::TryParse([string]$ServerAddress, [ref]$address) -or $address.AddressFamily -ne 'InterNetwork') {
    Fail 2 'ServerAddress must be the server''s IPv4 address (gameAddress from /client/health or /client/launch).'
}
if (-not $GamePath -or -not (Test-Path -LiteralPath $GamePath -PathType Leaf) -or (Split-Path -Leaf $GamePath) -notin @('FIFA21.exe', 'FIFA21_Trial.exe')) {
    Fail 2 'GamePath must be the FIFA21.exe of the installed game.'
}
foreach ($anchor in @('local-gos2015-modulus.bin', 'local-gs2019-modulus.bin')) {
    $file = Get-Item -LiteralPath (Join-Path ([string]$TrustAnchors) $anchor) -ErrorAction SilentlyContinue
    if (-not $file -or $file.Length -ne 256) { Fail 2 "TrustAnchors must hold this server's $anchor (GET /client/trust-anchors/manifest.json)." }
}
if (-not $LogPath) { $LogPath = Join-Path $env:TEMP 'aurora21-client' }
New-Item -ItemType Directory -Force -Path $LogPath | Out-Null

try { Test-GameBinaryPreflight $GamePath } catch { Fail 3 $_.Exception.Message }
$method, $reason = if ($LaunchMethod -eq 'auto') { Resolve-LaunchMethod $GamePath } else { $LaunchMethod, 'chosen with -LaunchMethod' }
$suspended = $null
# A suspended game must never be left behind: any unexpected error ends it before the script stops.
trap { if ($suspended) { Stop-SuspendedGame $suspended }; Write-Output "[FAILED] $($_.Exception.Message)"; exit 1 }
if (Get-NewestGameProcess) {
    if (-not $NoGameLaunch) { Fail 3 'FIFA 21 is already running: close it and press Play again.' }
} elseif (-not $NoGameLaunch) {
    switch ($method) {
        'ea-app' {
            $eaLauncher = if ($EAAppLauncherPath) { $EAAppLauncherPath } else { Join-Path $env:ProgramFiles 'Electronic Arts\EA Desktop\EA Desktop\EALauncher.exe' }
            if (-not (Test-Path -LiteralPath $eaLauncher -PathType Leaf)) {
                Fail 3 "This FIFA 21 is $reason, but the EA app was not found. Install or repair the EA app, or choose another game folder."
            }
            Write-FifaLauncherAutoLaunch
            Write-Output "[INFO] Starting FIFA 21 through the EA app ($reason)"
            Start-Process -FilePath $eaLauncher -ArgumentList @('origin2://game/launch/?offerIds=195853&authCode=&cmdParams=') | Out-Null
        }
        'steam' {
            Write-FifaLauncherAutoLaunch
            Write-Output "[INFO] Starting FIFA 21 through Steam ($reason)"
            Start-Process -FilePath 'steam://rungameid/1313860' | Out-Null
        }
        default {
            if ($discordUsername) {
                try { [void](Sync-DirectGameUsername $GamePath $discordUsername) }
                catch {
                    $cause = $_.Exception.GetBaseException()
                    $category = if ($cause -is [UnauthorizedAccessException]) { 'access-denied' }
                        elseif ($cause -is [IO.InvalidDataException]) { 'profile-format' }
                        elseif ($cause -is [ArgumentException]) { 'invalid-username' }
                        elseif ($cause -is [IO.IOException]) { 'profile-io-or-race' }
                        else { 'unexpected' }
                    $code = '0x' + $cause.HResult.ToString('X8')
                    Write-Output "[INFO] Direct-copy Username update failed ($category, $code)."
                    Fail 3 'The direct copy local Username could not be updated safely; FIFA 21 was not started.'
                }
            }
            Write-Output "[INFO] Starting FIFA21.exe directly ($reason)"
            try { $suspended = Start-SuspendedGame $GamePath } catch { Fail 3 "FIFA21.exe could not be started: $($_.Exception.Message)" }
        }
    }
}
$deadline = (Get-Date).AddSeconds(180)
$firstGame = $null
while (-not ($firstGame = Get-NewestGameProcess)) {
    if ((Get-Date) -gt $deadline) { Fail 3 'FIFA21.exe did not start within 180 seconds.' }
    Start-Sleep -Milliseconds 200
}
# The maths bridge starts now, before the trust patch: FIFA's startup must find its maths imports already switched.
$mathBridge = $null
try {
    $mathPid = if ($suspended) { $suspended.ProcessId } else { $firstGame.Id }
    $mathBridge = Start-MathBridge $mathPid (Join-Path $LogPath 'math-bridge.log')
} catch { Write-Output "[INFO] maths bridge: not started ($($_.Exception.Message)); pairing uses the Windows build" }
Write-Output '[INFO] FIFA 21 is running; patching its trust tables'

$patchOrigin = $method -eq 'direct'
$patchTarget = if ($suspended) { $suspended.ProcessId } else { 0 }
$routed = $false
$handovers = 0
while ($true) {
    $patch = Invoke-TrustPatchUntilReady -GamePid $patchTarget -PatchOrigin $patchOrigin
    if ($patch) { $patch.Output | Set-Content -LiteralPath (Join-Path $LogPath 'trust-patch.log') -Encoding utf8 }
    if (-not ($patch -and (Test-TrustPatched ($patch.Output -join "`n") $patchOrigin))) {
        if ($suspended) { Stop-SuspendedGame $suspended; $suspended = $null }
        $hosts = if ($routed) { 'The hosts entry is removed when Aurora cleans up.' } else { 'The hosts file was left unchanged.' }
        Fail 4 "The trust patch did not succeed; see $(Join-Path $LogPath 'trust-patch.log'). $hosts"
    }
    if ($suspended) {
        # The auth bridge waits for unpacked code, so a direct launch resumes before the bridge and the hosts redirect.
        try { Resume-SuspendedGame $suspended } catch { Stop-SuspendedGame $suspended; $suspended = $null; Fail 3 "FIFA21.exe could not be resumed: $($_.Exception.Message)" }
        $suspended = $null
    }
    # EA can expose a short-lived FIFA first (an EA app or Steam launch): the helper started for it gets no code, and the
    # process the trust patch settled on gets its own, later one (its own log, as the first may still be running).
    if ($mathBridge -and $mathBridge.GamePid -ne $patch.GamePid) {
        Submit-MathBridgeCode $mathBridge 0 $null
        $mathBridge = $null
        try { $mathBridge = Start-MathBridge $patch.GamePid (Join-Path $LogPath ('math-bridge-{0}.log' -f $patch.GamePid)) }
        catch { Write-Output "[INFO] maths bridge: not started ($($_.Exception.Message)); pairing uses the Windows build" }
    }
    Write-Output '[INFO] trust tables patched; starting the auth bridge'

    # A failure below exits with the code unsent: the maths helper's standard input closes and it reports nothing.
    $bridgeError = $null
    try { Invoke-AuthBridge $patch.GamePid $code (Join-Path $LogPath 'auth-bridge.log') } catch { $bridgeError = $_.Exception.Message }
    # A direct launch is the game itself; a bridge failure on an EA app or Steam FIFA that is still running is real too.
    # One that has gone was handing over (the bridge lost its process): the code is still unused, so follow the next.
    if ($bridgeError -and ($method -eq 'direct' -or (Test-GameRunning $patch.GamePid))) { Fail 5 $bridgeError }
    if (-not $bridgeError -and -not $routed) {
        try { Set-RedirectHostsEntry $address.ToString() } catch { Fail 6 $_.Exception.Message }
        $routed = $true
    }
    if ($method -eq 'direct') { break }
    try { $next = Wait-GameHandover $patch.GamePid } catch { Fail 3 $_.Exception.Message }
    if (-not $next) { break }
    if (++$handovers -gt 3) { Fail 3 'FIFA 21 kept handing over to new processes during startup.' }
    Write-Output "[INFO] FIFA 21 (PID $($patch.GamePid)) handed over to PID $($next.Id); patching the new process"
    $patchTarget = $next.Id
}
# Ready: the maths helper of the patched process gets the code for its report (sent once its switch has held).
Submit-MathBridgeCode $mathBridge $patch.GamePid $code
$code = $null
$mathStatus = Get-MathBridgeStatus $mathBridge
if ($mathStatus) { Write-Output $mathStatus }
Write-Output "[READY] FIFA 21 (PID $($patch.GamePid)) is patched and routed to $($address.ToString())"
exit 0
