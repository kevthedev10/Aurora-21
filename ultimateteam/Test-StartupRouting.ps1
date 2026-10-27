# Offline fixtures only: no real hosts, DNS, UAC, game or services.
$ErrorActionPreference = 'Stop'
$worker = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Launch-UltimateTeam.ps1'))
$routing = $worker.LastIndexOf('Add-RedirectHostsEntry')
$resume = $worker.LastIndexOf('Resume-SuspendedGame $suspendedGame')
if ($routing -lt 0 -or $resume -lt $routing) { throw 'Local routing must be installed before direct FIFA resumes' }
foreach ($name in @('Launch-UltimateTeam.ps1', 'Stop-UltimateTeam.ps1', 'Local-Routing.ps1')) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
    if ($name -ne 'Local-Routing.ps1') {
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name))
        if (-not $source.Contains(". (Join-Path `$ProjectRoot 'Local-Routing.ps1')") -or -not $source.Contains('Remove-RedirectHostsEntry')) {
            throw "$name must use shared routing and restore it during cleanup"
        }
    }
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Aurora21-Routing-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$HostsPath = Join-Path $fixture 'hosts'
$UserConfigRoot = Join-Path $fixture 'config'
$ProjectRoot = $PSScriptRoot
. (Join-Path $PSScriptRoot 'Local-Routing.ps1')
function ipconfig { param($unused) }
function Resolve-LocalRedirectHost { param($Hostname) return '127.0.0.1' }
function Assert-Equal($Actual, $Expected, $Message) {
    if (($Actual -join "`n") -cne ($Expected -join "`n")) { throw $Message }
}
try {
    $original = @(('# unrelated caf' + [char]0xE9), '127.0.0.1 localhost', '192.0.2.1 example.test') +
        @($RedirectHosts | ForEach-Object { "185.172.175.227 $_" })
    [IO.File]::WriteAllLines($HostsPath, [string[]]$original, [Text.UTF8Encoding]::new($false))
    Add-RedirectHostsEntry
    $installed = @($RedirectHosts | ForEach-Object { "127.0.0.1 $_" })
    Assert-Equal (Split-RedirectHostsLines ([IO.File]::ReadAllLines($HostsPath))).Redirect $installed 'Hosted entries must be replaced exclusively by loopback'
    Add-RedirectHostsEntry
    $lines = @([IO.File]::ReadAllLines($HostsPath)) + '192.0.2.2 added-during-test.example'
    [IO.File]::WriteAllLines($HostsPath, [string[]]$lines)
    Remove-RedirectHostsEntry
    Assert-Equal (Split-RedirectHostsLines ([IO.File]::ReadAllLines($HostsPath))).Redirect $original[3..6] 'Repeated starts must restore original hosted routing'
    if (-not ([IO.File]::ReadAllLines($HostsPath) -contains '192.0.2.2 added-during-test.example')) { throw 'Cleanup lost unrelated edits' }
    if (-not ([IO.File]::ReadAllLines($HostsPath) -contains $original[0])) { throw 'Non-ASCII comment lost' }
    if (Test-Path $RoutingStatePath) { throw 'Successful cleanup retained backup' }

    # Multi-host lines, aliases, mixed case and comments must remain usable.
    $multi = '185.172.175.227 SPRING18.GOSREDIRECTOR.EA.COM other.example # keep comment'
    [IO.File]::WriteAllLines($HostsPath, [string[]]@($multi))
    Add-RedirectHostsEntry
    Remove-RedirectHostsEntry
    $lines = [IO.File]::ReadAllLines($HostsPath)
    if ($lines -notcontains '185.172.175.227 other.example # keep comment' -or $lines -notcontains '185.172.175.227 SPRING18.GOSREDIRECTOR.EA.COM') { throw 'Multi-host restoration failed' }

    # An empty original mapping is recoverable after a failed launch.
    [IO.File]::WriteAllLines($HostsPath, [string[]]@('127.0.0.1 localhost'))
    Add-RedirectHostsEntry
    Remove-RedirectHostsEntry
    Assert-Equal ([IO.File]::ReadAllLines($HostsPath)) @('127.0.0.1 localhost') 'Empty original routing must restore'

    # DNS must reject mixed/local+remote results, leaving recovery available.
    function Resolve-LocalRedirectHost { param($Hostname) return @('127.0.0.1', '192.0.2.20') }
    $rejected = $false
    try { Add-RedirectHostsEntry } catch { $rejected = $_.Exception.Message -like 'Local routing failed*' }
    if (-not $rejected -or -not (Test-Path $RoutingStatePath)) { throw 'Conflicting resolution must fail and keep backup' }
    Remove-RedirectHostsEntry
    function Resolve-LocalRedirectHost { param($Hostname) return '127.0.0.1' }

    # Another launcher changing routing must never be overwritten on Stop.
    Add-RedirectHostsEntry
    $external = @($RedirectHosts | ForEach-Object { "192.0.2.50 $_" })
    [IO.File]::WriteAllLines($HostsPath, [string[]]$external)
    $rejected = $false
    try { Remove-RedirectHostsEntry } catch { $rejected = $_.Exception.Message -like 'Another launcher changed*' }
    if (-not $rejected -or -not (Test-Path $RoutingStatePath)) { throw 'External launcher conflict must preserve backup' }
    Assert-Equal ([IO.File]::ReadAllLines($HostsPath)) $external 'External routing overwritten'
    Remove-Item $RoutingStatePath

    # Older launches without a backup: remove only exact local FIFA mappings.
    [IO.File]::WriteAllLines($HostsPath, [string[]](@('192.0.2.1 example.test', '185.172.175.227 spring18.gosredirector.ea.com') + $installed))
    Remove-RedirectHostsEntry
    Assert-Equal ([IO.File]::ReadAllLines($HostsPath)) @('192.0.2.1 example.test', '185.172.175.227 spring18.gosredirector.ea.com') 'Legacy cleanup altered hosted/unrelated entries'
    Write-Host 'PASS: routing order, local override, restart recovery, hosted restoration, aliases, DNS conflict and external-edit protection.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
