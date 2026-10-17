# Run once on the build VM, as the user who builds: creates WinWispr's self-signed code-signing certificate
# (CN=WinWispr, non-exportable key, ten years) in Cert:\CurrentUser\My, exports the public certificate to
# eng\signing\WinWispr-CodeSigning.cer and pins its thumbprint in eng\signing\code-signing.json. Commit both
# files. Then trust it on every machine with eng\Trust-CodeSigningCertificate.ps1.
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'signing'))

$ErrorActionPreference = 'Stop'
$existing = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -ceq 'CN=WinWispr' -and $_.NotAfter -gt (Get-Date) })
if ($existing.Count -gt 0) {
    throw "A CN=WinWispr code-signing certificate already exists: $($existing.Thumbprint -join ', '). Remove it first to replace it."
}
$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WinWispr' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cerPath = Join-Path $OutputDirectory 'WinWispr-CodeSigning.cer'
[IO.File]::WriteAllBytes($cerPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
$pin = [ordered]@{
    subject = 'CN=WinWispr'
    thumbprint = $certificate.Thumbprint
    notAfter = $certificate.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd')
    timestampUrl = 'http://timestamp.digicert.com'
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'code-signing.json'), (($pin | ConvertTo-Json) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "THUMBPRINT=$($certificate.Thumbprint)"
Write-Output "CERTIFICATE=$cerPath"
