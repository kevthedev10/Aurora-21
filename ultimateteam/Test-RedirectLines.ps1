# Fixture-only checks for the hosted hosts-file edit. The real hosts file, DNS and processes are never touched.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'client-pack\Start-Aurora21.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'The hosted start script does not parse.' }
$hostsList = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$RedirectHosts'
}, $false)
if (-not $hostsList) { throw 'Missing $RedirectHosts.' }
. ([scriptblock]::Create($hostsList.Extent.Text))
foreach ($name in @('Remove-RedirectNames', 'Write-HostsLines')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing hosts function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Assert-Equal($Actual, $Expected, [string]$Reason) {
    $a = @($Actual) -join "`n"
    $e = @($Expected) -join "`n"
    if ($a -cne $e) { throw "$Reason`nexpected:`n$e`nactual:`n$a" }
}

# Untouched: comments, blank lines, unrelated entries, look-alike names.
$plain = @('# Copyright (c) Microsoft', '', '127.0.0.1 localhost', '0.0.0.0 gosredirector.ea.com', '1.2.3.4 xspring18.gosredirector.ea.com')
Assert-Equal (Remove-RedirectNames $plain) $plain 'Lines without a redirect name must stay exactly as they were.'

# Every earlier redirect form goes: plain, tabbed, trailing comment, other case, trailing dot, test environments.
$old = @(
    '127.0.0.1 spring18.gosredirector.ea.com',
    "185.1.1.1`tspring18.gosredirector.stest.ea.com",
    '10.0.0.1 spring18.gosredirector.scert.ea.com # Aurora',
    '10.0.0.2 SPRING18.GOSREDIRECTOR.EA.COM',
    '10.0.0.3 spring18.gosredirector.sdev.ea.com.'
)
Assert-Equal (Remove-RedirectNames (@('127.0.0.1 localhost') + $old)) @('127.0.0.1 localhost') 'Every redirect line must be removed, whatever its form.'

# A block-list line keeps its other names (and comment); only ours leave.
Assert-Equal (Remove-RedirectNames @('0.0.0.0 spring18.gosredirector.ea.com gosredirector.ea.com # block EA')) `
    @('0.0.0.0 gosredirector.ea.com # block EA') 'Other names on a shared line must be kept.'

# A commented-out redirect is not an entry and stays.
Assert-Equal (Remove-RedirectNames @('# 127.0.0.1 spring18.gosredirector.ea.com')) @('# 127.0.0.1 spring18.gosredirector.ea.com') 'Comments must stay.'

# Write-HostsLines keeps non-ASCII comments, writes no byte order mark, and restores read-only.
$root = Join-Path ([IO.Path]::GetTempPath()) ('a21-hosts-lines-test-' + [Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($root)
    $HostsPath = Join-Path $root 'hosts'
    [IO.File]::WriteAllText($HostsPath, "old`r`n")
    (Get-Item -LiteralPath $HostsPath).Attributes = 'ReadOnly'
    $comment = '# caf' + [char]0xE9
    Write-HostsLines @($comment, '1.2.3.4 spring18.gosredirector.ea.com')
    $bytes = [IO.File]::ReadAllBytes($HostsPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB) { throw 'The hosts file must be written without a byte order mark.' }
    Assert-Equal ([IO.File]::ReadAllLines($HostsPath, [Text.Encoding]::UTF8)) @($comment, '1.2.3.4 spring18.gosredirector.ea.com') 'The written lines must round-trip.'
    if (-not ((Get-Item -LiteralPath $HostsPath).Attributes -band [IO.FileAttributes]::ReadOnly)) { throw 'Read-only must be restored after the write.' }
} finally {
    if (Test-Path -LiteralPath $HostsPath) { (Get-Item -LiteralPath $HostsPath -Force).Attributes = 'Normal' }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'Hosts redirect line tests passed.'
