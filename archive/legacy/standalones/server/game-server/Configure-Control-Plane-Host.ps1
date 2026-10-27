[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$settingsPath = Join-Path $Root 'control-plane-host.json'
$secretPath = Join-Path $Root 'config\control-plane-host-secret.json'

$baseUrl = (Read-Host 'Deployed Project 21 control-panel URL (https://...)').Trim().TrimEnd('/')
if ($baseUrl -notmatch '^https://[a-z0-9.-]+(?::\d+)?$') { throw 'A valid HTTPS control-panel URL is required.' }
$secret = Read-Host 'HOST_AGENT_SECRET configured for the control panel' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try { $plainSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
if ($plainSecret.Length -lt 32) { throw 'HOST_AGENT_SECRET must contain at least 32 characters.' }

Add-Type -AssemblyName System.Security
$plainBytes = [Text.Encoding]::UTF8.GetBytes($plainSecret)
try {
    $protected = [Security.Cryptography.ProtectedData]::Protect(
        $plainBytes,
        [Text.Encoding]::UTF8.GetBytes('Project21.HostAgent.v1'),
        [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
}
finally {
    [Array]::Clear($plainBytes, 0, $plainBytes.Length)
    $plainSecret = $null
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $secretPath) | Out-Null
@{ version = 1; protectedSecret = [Convert]::ToBase64String($protected) } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath $secretPath -Encoding UTF8
@{ enabled = $true; baseUrl = $baseUrl; pollSeconds = 5 } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath $settingsPath -Encoding UTF8
$launcherRoot = Join-Path (Split-Path -Parent $Root) 'standalone-launcher'
if (Test-Path -LiteralPath $launcherRoot -PathType Container) {
    @{ enabled = $true; baseUrl = $baseUrl } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $launcherRoot 'control-plane.json') -Encoding UTF8
    Write-Host 'Sibling standalone-launcher configured for Discord authentication.'
}
Write-Host 'Host control-plane connection configured.' -ForegroundColor Green
