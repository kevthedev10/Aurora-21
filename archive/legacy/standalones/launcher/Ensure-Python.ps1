[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PinnedVersion = '3.12.10'
$PythonDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312'
$PythonExecutable = Join-Path $PythonDirectory 'python.exe'
$InstallerUrl = "https://www.python.org/ftp/python/$PinnedVersion/python-$PinnedVersion-amd64.exe"
$DownloadDirectory = Join-Path $env:TEMP 'Project21\Python'
$InstallerPath = Join-Path $DownloadDirectory "python-$PinnedVersion-amd64.exe"

function Test-Project21Python([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        & $Path -c "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)" 2>$null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Test-Cryptography([string]$Path) {
    try {
        & $Path -c "from importlib.metadata import version; raise SystemExit(0 if int(version('cryptography').split('.')[0]) >= 42 else 1)" 2>$null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

if (-not (Test-Project21Python $PythonExecutable)) {
    Write-Output "Python $PinnedVersion is required. Downloading the official installer..."
    New-Item -ItemType Directory -Path $DownloadDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $InstallerPath -PathType Leaf) {
        Remove-Item -LiteralPath $InstallerPath -Force
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $InstallerUrl -OutFile $InstallerPath

    $signature = Get-AuthenticodeSignature -FilePath $InstallerPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        -not $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch 'Python Software Foundation') {
        Remove-Item -LiteralPath $InstallerPath -Force -ErrorAction SilentlyContinue
        throw 'The downloaded Python installer did not have a valid Python Software Foundation signature.'
    }

    Write-Output 'Installing Python for this Windows user...'
    $arguments = @(
        '/quiet',
        'InstallAllUsers=0',
        "TargetDir=$PythonDirectory",
        'PrependPath=0',
        'Include_launcher=1',
        'InstallLauncherAllUsers=0',
        'Include_pip=1',
        'Include_test=0',
        'Include_doc=0',
        'Shortcuts=0'
    )
    $installation = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -Wait -PassThru
    Remove-Item -LiteralPath $InstallerPath -Force -ErrorAction SilentlyContinue
    if ($installation.ExitCode -ne 0 -or -not (Test-Project21Python $PythonExecutable)) {
        throw "Python installation failed with exit code $($installation.ExitCode)."
    }
}

if (-not (Test-Cryptography $PythonExecutable)) {
    Write-Output 'Installing the required secure networking component...'
    & $PythonExecutable -m pip install --disable-pip-version-check --no-warn-script-location 'cryptography>=42'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Cryptography $PythonExecutable)) {
        throw 'The required Python cryptography package could not be installed.'
    }
}

$RuntimeSource = Join-Path $PSScriptRoot 'runtime-source'
$CertificateGenerator = Join-Path $RuntimeSource 'generate_local_cert.py'
$RequiredConnectionFiles = @(
    (Join-Path $RuntimeSource 'certs\spring18-cert.pem'),
    (Join-Path $RuntimeSource 'certs\spring18-chain.pem'),
    (Join-Path $RuntimeSource 'certs\spring18-key.pem'),
    (Join-Path $RuntimeSource 'certs\spring18-gs2019-cert.pem'),
    (Join-Path $RuntimeSource 'certs\spring18-gs2019-chain.pem'),
    (Join-Path $RuntimeSource 'certs\spring18-gs2019-key.pem'),
    (Join-Path $RuntimeSource 'local-gos2015-modulus.bin'),
    (Join-Path $RuntimeSource 'local-gs2019-modulus.bin')
)
if (@($RequiredConnectionFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
    if (-not (Test-Path -LiteralPath $CertificateGenerator -PathType Leaf)) {
        throw 'The per-device connection certificate generator is missing.'
    }
    Write-Output "Generating this device's private connection certificates..."
    & $PythonExecutable $CertificateGenerator
    if ($LASTEXITCODE -ne 0 -or
        @($RequiredConnectionFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
        throw 'The per-device connection certificates could not be generated.'
    }
}

$CertificateDirectory = Join-Path $RuntimeSource 'certs'
$CurrentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $CertificateDirectory /inheritance:r /grant:r `
    "*$($CurrentUserSid):(OI)(CI)F" `
    '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'The generated private connection certificates could not be protected for this Windows user.'
}

Write-Output "Python $PinnedVersion and its required component are ready."
