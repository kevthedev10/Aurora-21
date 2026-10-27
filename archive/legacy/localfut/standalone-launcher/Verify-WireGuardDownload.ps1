Set-StrictMode -Version Latest

function Assert-WireGuardAuthenticodeSignature {
    param([Parameter(Mandatory = $true)][string]$Path)
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        -not $signature.SignerCertificate -or
        [string]$signature.SignerCertificate.Subject -notmatch '(?i)\bWireGuard LLC\b') {
        throw 'The downloaded WireGuard installer does not have a valid WireGuard LLC signature.'
    }
}
