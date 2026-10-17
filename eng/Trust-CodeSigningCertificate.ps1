# Run elevated on each machine that should trust WinWispr's own signature, including the build VM (its builds
# verify every signature). Imports eng\signing\WinWispr-CodeSigning.cer into the machine's Trusted Root
# Certification Authorities and Trusted Publishers after checking it against the pinned thumbprint.
# -Remove takes the trust away again.
[CmdletBinding()]
param([string]$CertificatePath = (Join-Path $PSScriptRoot 'signing\WinWispr-CodeSigning.cer'), [switch]$Remove)

$ErrorActionPreference = 'Stop'
$pin = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'signing\code-signing.json') | ConvertFrom-Json
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell (Run as administrator).'
}
foreach ($store in @('Root', 'TrustedPublisher')) {
    if ($Remove) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\$store\$($pin.thumbprint)" -ErrorAction SilentlyContinue
        Write-Output "REMOVED=LocalMachine\$store"
        continue
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    if ($certificate.Thumbprint -cne $pin.thumbprint -or $certificate.Subject -cne $pin.subject -or $certificate.HasPrivateKey) {
        throw "$CertificatePath is not the pinned WinWispr certificate $($pin.thumbprint)."
    }
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    Write-Output "TRUSTED=LocalMachine\$store"
}
