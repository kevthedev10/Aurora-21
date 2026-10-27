[CmdletBinding()]
param([switch]$Once)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$settings = Get-Content -LiteralPath (Join-Path $Root 'control-plane-host.json') -Raw | ConvertFrom-Json
if ($settings.enabled -ne $true) { throw 'Host control plane is disabled. Run Configure-Control-Plane-Host.cmd.' }
$baseUrl = ([string]$settings.baseUrl).TrimEnd('/')
if ($baseUrl -notmatch '^https://') { throw 'Host control plane must use HTTPS.' }

$secretDocument = Get-Content -LiteralPath (Join-Path $Root 'config\control-plane-host-secret.json') -Raw | ConvertFrom-Json
Add-Type -AssemblyName System.Security
$protected = [Convert]::FromBase64String([string]$secretDocument.protectedSecret)
$plain = [Security.Cryptography.ProtectedData]::Unprotect(
    $protected,
    [Text.Encoding]::UTF8.GetBytes('Project21.HostAgent.v1'),
    [Security.Cryptography.DataProtectionScope]::CurrentUser
)
try { $hostSecret = [Text.Encoding]::UTF8.GetString($plain) }
finally { [Array]::Clear($plain, 0, $plain.Length) }
$headers = @{ 'X-Project21-Host-Secret' = $hostSecret }
$actionScript = Join-Path $Root 'runtime-source\localfut21\admin_actions.py'
$pollSeconds = [Math]::Max(2, [Math]::Min(30, [int]$settings.pollSeconds))
$python = Get-Command py.exe -ErrorAction SilentlyContinue
$pythonArguments = @('-3')
if (-not $python) {
    $python = Get-Command python.exe -ErrorAction SilentlyContinue
    $pythonArguments = @()
}
if (-not $python) { throw 'Python 3.10 or newer is required by the host control agent.' }

function Send-CommandResult {
    param([long]$Id, [bool]$Success, [string]$Result)
    $body = @{ success = $Success; result = $Result } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method POST -Uri ($baseUrl + '/api/host/commands/' + $Id + '/result') `
        -Headers $headers -ContentType 'application/json' -Body $body | Out-Null
}

Write-Host 'Project 21 host control agent connected. Press Ctrl+C to stop.'
do {
    try {
        $response = Invoke-RestMethod -Method GET -Uri ($baseUrl + '/api/host/commands') -Headers $headers
        foreach ($command in @($response.commands)) {
            $payloadJson = [string]$command.payload
            $payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
            $payloadBase64 = [Convert]::ToBase64String($payloadBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $playerId = [string]$command.target_discord_id
            $env:LOCALFUT_RUNTIME_ROOT = Join-Path $Root ('state\players\' + $playerId)
            $env:LOCALFUT21_USER_CONFIG_ROOT = Join-Path $Root ('config\players\' + $playerId)
            New-Item -ItemType Directory -Force -Path $env:LOCALFUT_RUNTIME_ROOT,$env:LOCALFUT21_USER_CONFIG_ROOT | Out-Null
            if ([string]$command.action -in @('add_coins','grant_pack','grant_card')) {
                $pidPath = Join-Path $env:LOCALFUT_RUNTIME_ROOT 'server.pid'
                if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
                    $playerServerPid = 0
                    if ([int]::TryParse((Get-Content -LiteralPath $pidPath -Raw).Trim(), [ref]$playerServerPid)) {
                        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$playerServerPid" -ErrorAction SilentlyContinue
                        if ($processInfo -and [string]$processInfo.CommandLine -match 'localfut21[\\/]server\.py' -and [string]$processInfo.CommandLine -match '--host\s+127\.') {
                            Stop-Process -Id $playerServerPid -Force -ErrorAction Stop
                            Start-Sleep -Milliseconds 300
                        }
                    }
                    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
                }
            }
            $output = (& $python.Source @pythonArguments $actionScript --action ([string]$command.action) `
                --target-discord-id ([string]$command.target_discord_id) --payload-base64 $payloadBase64 2>&1) -join "`n"
            $success = $LASTEXITCODE -eq 0
            Send-CommandResult -Id ([long]$command.id) -Success $success -Result $output
            Write-Host ("Command {0} {1}: {2}" -f $command.id, $(if ($success) { 'succeeded' } else { 'failed' }), $command.action)
        }
    }
    catch {
        Write-Warning ('Control-plane poll failed: ' + $_.Exception.Message)
    }
    if (-not $Once) { Start-Sleep -Seconds $pollSeconds }
} while (-not $Once)

$hostSecret = $null
