# Authenticode signing with WinWispr's own self-signed certificate (subject CN=WinWispr). Its private key is
# non-exportable and lives only in Cert:\CurrentUser\My of the build VM; eng/signing/code-signing.json pins its
# thumbprint and eng/signing/WinWispr-CodeSigning.cer is the public certificate that
# eng/Trust-CodeSigningCertificate.ps1 trusts on a machine. A self-signed signature names the publisher on
# machines that trust it; it earns no SmartScreen reputation and is no enterprise approval.

function Read-CodeSigningPin {
    $pin = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'signing\code-signing.json') | ConvertFrom-Json
    if ([string]$pin.subject -cne 'CN=WinWispr' -or [string]$pin.thumbprint -cnotmatch '^[0-9A-F]{40}$' -or
        [string]$pin.timestampUrl -notmatch '^https?://') {
        throw 'eng/signing/code-signing.json has no valid pin; run eng/New-CodeSigningCertificate.ps1 on the build VM first.'
    }
    $pin
}

function Resolve-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $candidates = @(Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "$architecture\signtool.exe" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($candidates.Count -eq 0) {
        throw "signtool.exe was not found below $kits; install the Windows SDK signing tools."
    }
    $candidates[0]
}

# The pinned certificate with its private key from the current user's store.
function Get-CodeSigningCertificate {
    param([Parameter(Mandatory)]$Pin)
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$($Pin.thumbprint)" -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey -or $certificate.Subject -cne $Pin.subject) {
        throw "The code-signing certificate $($Pin.thumbprint) ($($Pin.subject)) with its private key is not in Cert:\CurrentUser\My."
    }
    if ($certificate.NotAfter -le (Get-Date).AddDays(30)) {
        throw "The code-signing certificate expires on $($certificate.NotAfter.ToString('yyyy-MM-dd')); create a new one."
    }
    $certificate
}

# Fails unless the file carries a valid signature by the pinned certificate. Valid needs the certificate to be
# trusted on this machine (eng/Trust-CodeSigningCertificate.ps1).
function Assert-CodeSignature {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Pin)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $Pin.thumbprint) {
        throw "$Path is not signed with the WinWispr certificate $($Pin.thumbprint)."
    }
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "The signature of $Path is $($signature.Status): $($signature.StatusMessage) Trust the certificate with eng/Trust-CodeSigningCertificate.ps1."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "The signature of $Path has no timestamp."
    }
}

function Invoke-CodeSign {
    param([Parameter(Mandatory)][string[]]$Path, [Parameter(Mandatory)]$Pin)
    Get-CodeSigningCertificate -Pin $Pin | Out-Null
    $signTool = Resolve-SignTool
    $output = & $signTool sign /q /fd SHA256 /s My /sha1 $Pin.thumbprint /tr $Pin.timestampUrl /td SHA256 @Path 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code ${LASTEXITCODE}: $($output -join ' ')"
    }
    foreach ($file in $Path) { Assert-CodeSignature -Path $file -Pin $Pin }
}

# The ISCC /S argument that defines the sign tool "winwispr" for SignTool=winwispr in the .iss scripts.
function Get-InnoSignToolArgument {
    param([Parameter(Mandatory)]$Pin)
    Get-CodeSigningCertificate -Pin $Pin | Out-Null
    $signTool = Resolve-SignTool
    "/Swinwispr=`$q$signTool`$q sign /q /fd SHA256 /s My /sha1 $($Pin.thumbprint) /tr $($Pin.timestampUrl) /td SHA256 `$f"
}
