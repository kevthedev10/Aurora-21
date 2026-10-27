<#
Read-only, debugger-assisted auth investigation. Run while FIFA is paused at a
documented breakpoint. Prints only length and SHA-256; never prints the bytes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$Address,
    [int]$ByteCount = 0,
    [switch]$NullTerminated
)

$ErrorActionPreference = 'Stop'
$expectedGameSha256 = '44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494'
$maxBytes = 256

if ($ByteCount -lt 0 -or $ByteCount -gt $maxBytes) {
    throw "ByteCount must be between 0 and $maxBytes."
}
if ($NullTerminated -eq ($ByteCount -gt 0)) {
    throw 'Specify exactly one of -ByteCount or -NullTerminated.'
}
if ($Address -match '^(?:0[xX])?([0-9a-fA-F]{1,16})$') {
    $addressValue = [Convert]::ToUInt64($Matches[1], 16)
}
else {
    throw 'Address must be a hexadecimal pointer, such as 0x1234ABCD.'
}
if ($addressValue -eq 0 -or $addressValue -gt [long]::MaxValue) {
    throw 'Address is outside the supported user-mode pointer range.'
}

$expectedPath = [IO.Path]::GetFullPath($GamePath)
$game = Get-Process -Id $ProcessId -ErrorAction Stop
if ($game.ProcessName -ne 'FIFA21') {
    throw 'The selected process is not FIFA21.exe.'
}
$actualPath = [IO.Path]::GetFullPath($game.Path)
if (-not [string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected FIFA process does not match -GamePath.'
}
$actualSha256 = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedGameSha256) {
    throw 'The selected FIFA executable does not match the investigated build.'
}

if (-not ('Aurora21ReadOnlyMemory' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Aurora21ReadOnlyMemory
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer,
        UIntPtr size, out UIntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@
}

$handle = [Aurora21ReadOnlyMemory]::OpenProcess(0x0410, $false, $ProcessId)
if ($handle -eq [IntPtr]::Zero) {
    throw "Cannot open FIFA for read-only inspection (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
}
try {
    $bytes = New-Object 'System.Collections.Generic.List[byte]'
    $limit = if ($NullTerminated) { $maxBytes } else { $ByteCount }
    for ($offset = 0; $offset -lt $limit; $offset++) {
        $one = New-Object byte[] 1
        $read = [UIntPtr]::Zero
        $pointer = [IntPtr]::new([long]($addressValue + [uint64]$offset))
        $ok = [Aurora21ReadOnlyMemory]::ReadProcessMemory($handle, $pointer, $one, [UIntPtr]::new([uint64]1), [ref]$read)
        if (-not $ok -or $read.ToUInt64() -ne 1) {
            throw "Cannot read the selected memory at byte offset $offset."
        }
        if ($NullTerminated -and $one[0] -eq 0) { break }
        $bytes.Add($one[0])
    }
    if ($NullTerminated -and $bytes.Count -eq $maxBytes) {
        throw "No NUL terminator was found within $maxBytes bytes."
    }
    if ($bytes.Count -eq 0) {
        throw 'The selected value is empty.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString($sha.ComputeHash($bytes.ToArray())).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    Write-Output "len=$($bytes.Count) sha256=$hash prefix=$($hash.Substring(0, 12))"
}
finally {
    [void][Aurora21ReadOnlyMemory]::CloseHandle($handle)
}
