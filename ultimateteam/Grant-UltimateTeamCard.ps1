[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [Int64]$ResourceId,
    [ValidateSet('club', 'unassigned')]
    [string]$Pile = 'club',
    [ValidateRange(1, 2147483647)]
    [Int64]$ReplaceResourceId,
    [switch]$AllowDuplicate
)

$ErrorActionPreference = 'Stop'

if ($env:ULTIMATETEAM_RUNTIME_ROOT) {
    throw 'Unset ULTIMATETEAM_RUNTIME_ROOT before granting a card to the normal local club.'
}

$server = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Aurora21-UltimateTeam-Server.exe')).Path
$dataRoot = Join-Path $PSScriptRoot 'runtime-source\ultimateteam21'
# The club lives in the managed PostgreSQL cluster.
$database = Join-Path $env:LOCALAPPDATA 'FIFA21UltimateTeam\postgres\data-18\PG_VERSION'
if (-not (Test-Path -LiteralPath $database -PathType Leaf)) {
    throw "No local club database exists in $(Split-Path -Parent $database). Create your UltimateTeam club once before granting cards."
}

if (Get-Process -Name FIFA21 -ErrorAction SilentlyContinue) {
    throw 'FIFA 21 is running. Close it before an offline card grant so it cannot retain stale club state.'
}

foreach ($port in 8090, 44321) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "A UltimateTeam service is listening on port $port. Stop UltimateTeam before an offline card grant."
    }
}

$arguments = @('grant-card', $ResourceId, '--pile', $Pile, '--data-root', $dataRoot)
if ($PSBoundParameters.ContainsKey('ReplaceResourceId')) { $arguments += @('--replace-resource-id', $ReplaceResourceId) }
if ($AllowDuplicate) { $arguments += '--allow-duplicate' }
& $server @arguments
if ($LASTEXITCODE -ne 0) { throw "Offline grant failed with exit code $LASTEXITCODE." }
