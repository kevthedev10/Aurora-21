[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'TraceReader.cs')
$root = Join-Path $env:TEMP ('Aurora21-TraceReader-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Be16([byte[]]$Bytes, [int]$At, [int]$Value) {
    $Bytes[$At] = [byte](($Value -shr 8) -band 255)
    $Bytes[$At + 1] = [byte]($Value -band 255)
}
function Be32([byte[]]$Bytes, [int]$At, [uint32]$Value) {
    for ($i = 0; $i -lt 4; $i++) { $Bytes[$At + $i] = [byte](($Value -shr ((3-$i)*8)) -band 255) }
}
function Make-Packet([bool]$Outbound, [int]$Flags, [uint32]$Sequence, [int]$Payload, [bool]$Vlan = $false) {
    $ip = if ($Vlan) { 18 } else { 14 }
    $bytes = [byte[]]::new($ip + 40)
    if ($Vlan) { Be16 $bytes 12 0x8100; Be16 $bytes 16 0x0800 }
    else { Be16 $bytes 12 0x0800 }
    $bytes[$ip] = 0x45
    Be16 $bytes ($ip + 2) (40 + $Payload)
    $bytes[$ip + 9] = 6
    $pc = [byte[]]@(192,0,2,10)
    $server = [byte[]]@(203,0,113,10)
    [Array]::Copy($(if ($Outbound) { $pc } else { $server }), 0, $bytes, $ip + 12, 4)
    [Array]::Copy($(if ($Outbound) { $server } else { $pc }), 0, $bytes, $ip + 16, 4)
    $tcp = $ip + 20
    Be16 $bytes $tcp $(if ($Outbound) { 50000 } else { 18443 })
    Be16 $bytes ($tcp + 2) $(if ($Outbound) { 18443 } else { 50000 })
    Be32 $bytes ($tcp + 4) $Sequence
    Be32 $bytes ($tcp + 8) 1234
    $bytes[$tcp + 12] = 0x50
    $bytes[$tcp + 13] = [byte]$Flags
    Be16 $bytes ($tcp + 14) 4096
    return ,$bytes
}
function Write-Block([IO.BinaryWriter]$Writer, [uint32]$Type, [byte[]]$Body) {
    $padding = (4 - ($Body.Length % 4)) % 4
    $length = [uint32](12 + $Body.Length + $padding)
    $Writer.Write($Type); $Writer.Write($length); $Writer.Write($Body)
    $Writer.Write([byte[]]::new($padding)); $Writer.Write($length)
}
function Start-Section([IO.BinaryWriter]$Writer, [int]$Resolution) {
    $stream = [IO.MemoryStream]::new()
    $b = [IO.BinaryWriter]::new($stream)
    $b.Write([uint32]0x1A2B3C4D); $b.Write([uint16]1); $b.Write([uint16]0); $b.Write([int64]-1)
    Write-Block $Writer 0x0A0D0D0A $stream.ToArray()
    $b.Dispose(); $stream.Dispose()
    $stream = [IO.MemoryStream]::new(); $b = [IO.BinaryWriter]::new($stream)
    $b.Write([uint16]1); $b.Write([uint16]0); $b.Write([uint32]96)
    $b.Write([uint16]9); $b.Write([uint16]1); $b.Write([byte]$Resolution); $b.Write([byte[]]::new(3))
    $b.Write([uint16]0); $b.Write([uint16]0)
    Write-Block $Writer 1 $stream.ToArray()
    $b.Dispose(); $stream.Dispose()
}
function Write-Packet([IO.BinaryWriter]$Writer, [uint64]$Stamp, [byte[]]$Bytes, [uint32]$OriginalLength) {
    $stream = [IO.MemoryStream]::new(); $b = [IO.BinaryWriter]::new($stream)
    $b.Write([uint32]0); $b.Write([uint32]($Stamp -shr 32)); $b.Write([uint32]($Stamp -band 0xFFFFFFFFL))
    $b.Write([uint32]$Bytes.Length); $b.Write($OriginalLength); $b.Write($Bytes)
    Write-Block $Writer 6 $stream.ToArray()
    $b.Dispose(); $stream.Dispose()
}

try {
    $fixture = Join-Path $root 'synthetic.pcapng'
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($fixture))
    try {
        Start-Section $writer 6
        # Payload is intentionally omitted, as in a header-limited capture. IP length still supplies its size.
        Write-Packet $writer 1000000 (Make-Packet $true 0x18 100 500) 554
        Write-Packet $writer 1250000 (Make-Packet $false 0x10 200 0 $true) 58
        Write-Packet $writer 1500000 (Make-Packet $true 0x18 100 500) 554
        Write-Packet $writer 2000000 (Make-Packet $true 0x02 101 0) 54
        Write-Packet $writer 2100000 (Make-Packet $false 0x11 201 0) 54
        Start-Section $writer 9
        Write-Packet $writer 2250000000 (Make-Packet $false 0x04 202 0) 54
    } finally { $writer.Dispose() }
    $packets = @([Aurora21.MenuDiagnostics.TraceReader]::Read($fixture, '203.0.113.10'))
    Assert-That ($packets.Count -eq 6) 'Packet count/section reset failed.'
    Assert-That ($packets[0].PayloadBytes -eq 500 -and $packets[0].Direction -eq 'PC->server') 'Truncated payload or direction parsing failed.'
    Assert-That ($packets[1].LocalPort -eq 50000 -and $packets[1].Acknowledgement -eq 1234 -and $packets[1].Window -eq 4096) 'Incoming VLAN/TCP field parsing failed.'
    Assert-That (-not $packets[0].RepeatedData -and $packets[2].RepeatedData) 'Repeated sequence tracking failed.'
    Assert-That ($packets[3].Flags -eq 'SYN' -and $packets[4].Flags -eq 'FIN|ACK' -and $packets[5].Flags -eq 'RST') 'TCP flag parsing failed.'
    Assert-That (($packets[5].Utc - $packets[0].Utc).TotalMilliseconds -eq 1250) 'Microsecond/nanosecond timestamp conversion failed.'
    Assert-That (@([Aurora21.MenuDiagnostics.TraceReader]::Read($fixture, '203.0.113.20')).Count -eq 0) 'Server filtering failed.'
    $broken = Join-Path $root 'truncated.pcapng'
    $bytes = [IO.File]::ReadAllBytes($fixture)
    [IO.File]::WriteAllBytes($broken, [byte[]]$bytes[0..($bytes.Length - 2)])
    $rejected = $false
    try { [Aurora21.MenuDiagnostics.TraceReader]::Read($broken, '203.0.113.10') | Out-Null }
    catch { $rejected = $true }
    Assert-That $rejected 'Truncated capture was silently accepted.'
    $tokens = $null; $errors = $null
    $entry = Join-Path $PSScriptRoot '..\..\Capture-MenuStall.ps1'
    $ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($entry), [ref]$tokens, [ref]$errors)
    Assert-That ($errors.Count -eq 0) "Capture script syntax errors: $errors"
    # Exercise the actual -File launch/default binding from a foreign working directory.
    # Invalid IPv4 input must stop before elevation checks, game access or Packet Monitor.
    $launch = [Diagnostics.ProcessStartInfo]::new()
    $launch.FileName = (Get-Command powershell.exe).Source
    $launch.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + [IO.Path]::GetFullPath($entry) + '" -ServerIp invalid-address -Minutes 15'
    $launch.WorkingDirectory = $root
    $launch.UseShellExecute = $false
    $launch.RedirectStandardOutput = $true
    $launch.RedirectStandardError = $true
    $child = [Diagnostics.Process]::Start($launch)
    try {
        $stdout = $child.StandardOutput.ReadToEnd()
        $stderr = $child.StandardError.ReadToEnd()
        $child.WaitForExit()
        Assert-That ($child.ExitCode -ne 0 -and $stderr -match 'ServerIp must be the hosted server IPv4 address' -and
            $stderr -notmatch 'Cannot bind argument') "Default-path -File launch regression: $stdout $stderr"
    } finally { $child.Dispose() }
    # Run just the native-command wrapper against cmd.exe, never the capture entrypoint.
    $wrapper = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-PacketMonitor' }, $true)
    Invoke-Expression $wrapper.Extent.Text
    $pktmon = $env:ComSpec
    $toolLog = Join-Path $root 'mock-tool.log'
    $text = Invoke-PacketMonitor -Arguments @('/d', '/c', 'echo', 'first-token', 'argument with spaces', 'last-token')
    Assert-That ($text -match 'first-token.*argument with spaces.*last-token') 'Native capture arguments were not forwarded intact.'
    $rejected = $false
    try { Invoke-PacketMonitor -Arguments @('/d', '/c', 'exit', '9') | Out-Null }
    catch { $rejected = $_.Exception.Message -match 'failed \(9\)' }
    Assert-That $rejected 'Native tool failures were not surfaced.'
    Write-Output 'PASS: synthetic header-only capture, VLAN, direction, repeated sequences, TCP flags, timestamp resolutions, sections, server filter, truncated-file rejection, script syntax, actual -File default-path launch and native argument/error handling.'
} finally {
    # Only this test's unique synthetic directory is removed.
    Remove-Item -LiteralPath $root -Recurse -Force
}
