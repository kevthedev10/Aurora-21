[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
$Project = Join-Path $Root 'LocalFUTLauncher.csproj'
$AuthSource = Join-Path $Root 'FIFA21-LocalAuthBridge.cs'
$OutputRoot = Join-Path $Root 'public-release'
$PackageRoot = Join-Path $OutputRoot 'Aurora21-Alpha-v1'
$ArchivePath = Join-Path $OutputRoot 'Aurora21-Alpha-v1-Public-Release-win-x64.zip'
$WorkRoot = Join-Path $env:TEMP ('Aurora21-PublicRelease-' + [Guid]::NewGuid().ToString('N'))
$PublishRoot = Join-Path $WorkRoot 'publish'
$AuthOutput = Join-Path $WorkRoot 'Aurora21-AuthBridge.exe'

function Require-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required $Label was not found: $Path"
    }
}

function Get-Sha256Hex([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Copy-RequiredFile([string]$RelativePath) {
    $source = Join-Path $Root $RelativePath
    Require-File $source $RelativePath
    $destination = Join-Path $PackageRoot $RelativePath
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

function Compile-AuthBridge {
    Require-File $AuthSource 'local auth bridge source'
    $compilerCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    $compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) {
        throw 'The Windows .NET Framework C# compiler was not found. Windows 10/11 normally includes it.'
    }

    & $compiler /nologo /target:exe /platform:x64 /optimize+ `
        /reference:System.Web.Extensions.dll /reference:System.dll /reference:System.Core.dll `
        ("/out:{0}" -f $AuthOutput) $AuthSource
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $AuthOutput -PathType Leaf)) {
        throw "Local auth bridge compilation failed with exit code $LASTEXITCODE."
    }
}

function Copy-SourcesFolder {
    $sourceFolder = Join-Path $Root 'sources'
    $destinationFolder = Join-Path $PackageRoot 'sources'
    New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null
    if (-not (Test-Path -LiteralPath $sourceFolder -PathType Container)) { return }

    foreach ($file in Get-ChildItem -LiteralPath $sourceFolder -Recurse -File) {
        # Developer/help files do not belong in the public release. Actual media
        # is kept external so it can be changed without rebuilding the launcher.
        if ($file.Name -match '(?i)^readme(?:\.|$)' -or $file.Extension -match '(?i)^\.(md|bak|tmp)$') {
            continue
        }
        $relative = $file.FullName.Substring($sourceFolder.Length).TrimStart('\','/')
        $destination = Join-Path $destinationFolder $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}

function Assert-CleanPublicPackage {
    $allFiles = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File)

    # Launcher/project/debug/developer source must never ship. Runtime PS1/PY
    # scripts are intentionally allowed because Aurora 21 (Alpha v1) executes them.
    $forbidden = @($allFiles | Where-Object {
        $_.Extension -match '(?i)^\.(cs|xaml|csproj|sln|pdb|md|bak|tmp|log|db|sqlite|sqlite3|pem|key|pfx|p12)$' -or
        $_.Name -match '(?i)(^|[._-])(notes?|backup|private|secrets?)([._-]|$)'
    })
    if ($forbidden.Count -gt 0) {
        throw "Forbidden development/sensitive files reached the public package:`n$($forbidden.FullName -join [Environment]::NewLine)"
    }

    $allowedPowerShell = @(
        'Ensure-Player-Access.ps1',
        'Ensure-Python.ps1',
        'Ensure-WireGuard-Connection.ps1',
        'First-Run-WireGuard.ps1',
        'Launch-LocalFUT.ps1',
        'Manage-Aurora21Club.ps1',
        'Player-Access-Common.ps1',
        'Stop-LocalFUT.ps1',
        'User-Settings.ps1',
        'Verify-WireGuardDownload.ps1',
        'Watch-FIFA21-Exit.ps1',
        'WireGuard-Common.ps1'
    )
    $unexpectedPs1 = @($allFiles | Where-Object {
        $_.Extension -ieq '.ps1' -and $allowedPowerShell -notcontains $_.Name
    })
    if ($unexpectedPs1.Count -gt 0) {
        throw "Unexpected PowerShell source reached the public package:`n$($unexpectedPs1.FullName -join [Environment]::NewLine)"
    }

    $allowedPython = @(
        'runtime-source\generate_local_cert.py',
        'runtime-source\PATCH_RUNNING_FIFA21_TRUST.py',
        'runtime-source\redirector_tls.py',
        'runtime-source\RESTORE_RUNNING_FIFA21_TRUST.py',
        'runtime-source\localfut21\server.py'
    )
    $unexpectedPy = @($allFiles | Where-Object {
        if ($_.Extension -ine '.py') { return $false }
        $relative = $_.FullName.Substring($PackageRoot.Length).TrimStart('\','/')
        return $allowedPython -notcontains $relative
    })
    if ($unexpectedPy.Count -gt 0) {
        throw "Unexpected Python source reached the public package:`n$($unexpectedPy.FullName -join [Environment]::NewLine)"
    }

    # Public package must not accidentally include credential/private-key data.
    $textFiles = @($allFiles | Where-Object { $_.Extension -match '(?i)^\.(cmd|json|ps1|py|txt)$' })
    foreach ($file in $textFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        if ($text -match '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
            $text -match '(?i)CF-Access-Client-Secret\s*:' -or
            $text -match '(?i)DISCORD_CLIENT_SECRET\s*[=:]' -or
            $text -match '(?i)HOST_AGENT_SECRET\s*[=:]' -or
            $text -match '(?im)^\s*PrivateKey\s*=\s*[A-Za-z0-9+/]{42}[A-Za-z0-9+/=]{2}') {
            throw "Credential/private-key material was detected in the public package: $($file.FullName)"
        }
    }
}

try {
    Write-Host ''
    Write-Host 'Aurora 21 (Alpha v1) - Public Release Builder' -ForegroundColor Cyan
    Write-Host '-----------------------------------' -ForegroundColor DarkGray

    Require-File $Project 'launcher project'
    Require-File $AuthSource 'local auth bridge source'

    if (Get-Process -Name 'Aurora21-Launcher' -ErrorAction SilentlyContinue) {
        throw 'Aurora21-Launcher is running. Close it before making a public release.'
    }
    if (Get-Process -Name 'Aurora21-AuthBridge' -ErrorAction SilentlyContinue) {
        throw 'Aurora21-AuthBridge is running. Stop LocalFUT before making a public release.'
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET 8 SDK was not found. Install the .NET 8 SDK on this development PC, then run this again.'
    }

    New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

    Write-Host '[1/5] Publishing self-contained win-x64 launcher...'
    & dotnet publish $Project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --nologo `
        -o $PublishRoot `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) {
        throw "Self-contained launcher publish failed with exit code $LASTEXITCODE."
    }

    $publishedExe = Join-Path $PublishRoot 'Aurora21-Launcher.exe'
    Require-File $publishedExe 'published launcher executable'

    Write-Host '[2/5] Compiling the release auth helper...'
    Compile-AuthBridge

    Write-Host '[3/5] Creating minimal runtime package...'
    if (Test-Path -LiteralPath $OutputRoot -PathType Container) {
        Remove-Item -LiteralPath $OutputRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PackageRoot -Force | Out-Null

    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $PackageRoot 'Aurora21-Launcher.exe') -Force
    Copy-Item -LiteralPath $AuthOutput -Destination (Join-Path $PackageRoot 'Aurora21-AuthBridge.exe') -Force

$requiredRootFiles = @(
        'control-plane.json',
        'Ensure-Player-Access.ps1',
        'Ensure-Python.ps1',
        'Ensure-WireGuard-Connection.ps1',
        'First-Run-WireGuard.ps1',
        'Launch-LocalFUT.ps1',
        'Manage-Aurora21Club.ps1',
        'Player-Access-Common.ps1',
    'remote-server.json',
        'Stop-LocalFUT.ps1',
        'THIRD-PARTY-NOTICES.txt',
        'User-Settings.ps1',
        'Verify-WireGuardDownload.ps1',
        'Watch-FIFA21-Exit.ps1',
        'wireguard-client.json',
        'WireGuard-Common.ps1'
    )
    foreach ($file in $requiredRootFiles) { Copy-RequiredFile $file }

    Copy-SourcesFolder

    $runtimeFiles = @(
        'runtime-source\generate_local_cert.py',
        'runtime-source\gos2015-original-modulus.bin',
        'runtime-source\gs2019-original-modulus.bin',
        'runtime-source\PATCH_RUNNING_FIFA21_TRUST.py',
        'runtime-source\redirector_tls.py',
        'runtime-source\RESTORE_RUNNING_FIFA21_TRUST.py',
        'runtime-source\localfut21\server.py'
    )
    foreach ($file in $runtimeFiles) { Copy-RequiredFile $file }

    Write-Host '[4/5] Verifying no development/debug/private files are included...'
    Assert-CleanPublicPackage

    if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }
    Write-Host '[5/5] Compressing public release...'
    Compress-Archive -LiteralPath $PackageRoot -DestinationPath $ArchivePath -CompressionLevel Optimal

    $hash = [pscustomobject]@{ Hash = Get-Sha256Hex $ArchivePath }
    $fileCount = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File).Count
    $packageSize = (Get-Item -LiteralPath $ArchivePath).Length

    Write-Host ''
    Write-Host 'PUBLIC RELEASE READY' -ForegroundColor Green
    Write-Host "ZIP:    $ArchivePath"
    Write-Host "Files:  $fileCount"
    Write-Host ('Size:   {0:N1} MB' -f ($packageSize / 1MB))
    Write-Host "SHA256: $($hash.Hash)"
    Write-Host ''
    Write-Host 'Public users do NOT need the .NET 8 SDK/runtime; the launcher is self-contained.' -ForegroundColor Green
    Write-Host 'The PS1/PY files left in the ZIP are runtime components the launcher actually executes.' -ForegroundColor DarkGray
}
finally {
    if (Test-Path -LiteralPath $WorkRoot -PathType Container) {
        Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
