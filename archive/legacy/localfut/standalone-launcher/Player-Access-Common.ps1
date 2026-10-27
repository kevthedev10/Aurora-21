Set-StrictMode -Version Latest

function Get-Project21ControlPlaneSettings {
    param([Parameter(Mandatory = $true)][string]$Root)

    $path = Join-Path $Root 'control-plane.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{ enabled = $false; baseUrl = '' }
    }

    $settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($settings.enabled -ne $true) {
        return $settings
    }

    $baseUrl = [string]$settings.baseUrl
    if ($baseUrl -notmatch '^https://[a-z0-9.-]+(?::\d+)?/?$' -or $baseUrl -match 'CHANGE_ME') {
        throw 'control-plane.json must contain the deployed HTTPS control-panel URL.'
    }
    $settings.baseUrl = $baseUrl.TrimEnd('/')
    return $settings
}

function Get-Project21AccessStorePath {
    $root = Join-Path $env:LOCALAPPDATA 'Project21'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    return (Join-Path $root 'player-access.json')
}

function Protect-Project21Secret {
    param([Parameter(Mandatory = $true)][string]$Value)
    Add-Type -AssemblyName System.Security
    $plain = [Text.Encoding]::UTF8.GetBytes($Value)
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $plain,
            [Text.Encoding]::UTF8.GetBytes('Project21.PlayerAccess.v1'),
            [Security.Cryptography.DataProtectionScope]::CurrentUser
        )
        return [Convert]::ToBase64String($protected)
    }
    finally {
        [Array]::Clear($plain, 0, $plain.Length)
    }
}

function Unprotect-Project21Secret {
    param([Parameter(Mandatory = $true)][string]$Value)
    Add-Type -AssemblyName System.Security
    $protected = [Convert]::FromBase64String($Value)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        $protected,
        [Text.Encoding]::UTF8.GetBytes('Project21.PlayerAccess.v1'),
        [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    try { return [Text.Encoding]::UTF8.GetString($plain) }
    finally { [Array]::Clear($plain, 0, $plain.Length) }
}

function Save-Project21AccessToken {
    param([Parameter(Mandatory = $true)][string]$AccessToken)
    $path = Get-Project21AccessStorePath
    $document = [ordered]@{
        version = 1
        protectedAccessToken = Protect-Project21Secret $AccessToken
        savedAt = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $json = $document | ConvertTo-Json -Compress
    $temporary = $path + '.tmp'
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

function Get-Project21AccessToken {
    $path = Get-Project21AccessStorePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ([int]$document.version -ne 1) { return $null }
        return Unprotect-Project21Secret ([string]$document.protectedAccessToken)
    }
    catch {
        return $null
    }
}

function Remove-Project21AccessToken {
    $path = Get-Project21AccessStorePath
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

function Invoke-Project21ControlPlane {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('GET','POST')][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [object]$Body,
        [string]$AccessToken
    )
    $headers = @{}
    if ($AccessToken) { $headers.Authorization = 'Bearer ' + $AccessToken }
    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $headers
        UseBasicParsing = $true
        ErrorAction = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = ($Body | ConvertTo-Json -Compress -Depth 8)
    }
    return Invoke-RestMethod @parameters
}
