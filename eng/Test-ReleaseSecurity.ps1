[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PortableZip,
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$Installer,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedSourceCommit
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())

function Assert-Sha256Sidecar {
    param([Parameter(Mandatory)] [string]$Path)

    $sidecar = "$Path.sha256"
    if (-not (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
        throw "SHA-256 sidecar is missing: $sidecar"
    }
    $line = (Get-Content -Raw -LiteralPath $sidecar).Trim()
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
        throw "Invalid SHA-256 sidecar: $sidecar"
    }
    if ($Matches[2].Trim() -cne [IO.Path]::GetFileName($Path)) {
        throw "SHA-256 sidecar names the wrong file: $sidecar"
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if ($actual -ine $Matches[1]) {
        throw "SHA-256 mismatch: $Path"
    }
}

function Assert-ValidSignature {
    param([Parameter(Mandatory)] [string]$Path)

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signature is not valid for ${Path}: $($signature.Status)"
    }
}

function Assert-SafeArchive {
    param([Parameter(Mandatory)] [string]$Path)

    $rootPrefix = [IO.Path]::GetFullPath($extractRoot).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            $entryName = $entry.FullName.Replace('\', '/')
            $destination = [IO.Path]::GetFullPath((Join-Path $extractRoot $entryName))
            if (-not $destination.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "ZIP entry escapes the extraction root: $($entry.FullName)"
            }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "ZIP entry is a symbolic link: $($entry.FullName)"
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory)] [string]$Path)

    $pending = [Collections.Generic.List[string]]::new()
    $pending.Add([IO.Path]::GetFullPath($Path))
    for ($index = 0; $index -lt $pending.Count; $index++) {
        foreach ($child in @(Get-ChildItem -LiteralPath $pending[$index] -Force)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Extracted payload contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Add($child.FullName)
            }
        }
    }
}

try {
    Assert-Sha256Sidecar -Path $PortableZip
    Assert-Sha256Sidecar -Path $Installer
    Assert-SafeArchive -Path $PortableZip
    New-Item -ItemType Directory -Path $extractRoot | Out-Null
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $extractRoot
    Assert-NoReparseTree -Path $extractRoot

    $buildInfoPath = Join-Path $extractRoot 'BUILD-INFO.json'
    $buildInfo = Get-Content -Raw -LiteralPath $buildInfoPath | ConvertFrom-Json
    if ($buildInfo.sourceCommit -cne $ExpectedSourceCommit.ToLowerInvariant()) {
        throw "BUILD-INFO source commit mismatch: $($buildInfo.sourceCommit)"
    }
    if ($buildInfo.sourceArchiveSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'BUILD-INFO source archive SHA-256 is invalid.'
    }
    [DateTimeOffset]::Parse(
        $buildInfo.buildUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind) | Out-Null
    if ($buildInfo.runtimeBuild -cne 'rebuilt') {
        throw "Release artifact used a cached runtime: $($buildInfo.runtimeBuild)"
    }

    $dependencies = $buildInfo.runtimeDependencies
    if ($dependencies.directMlAdapterSelectionVersion -ne 1 -or
        $dependencies.sherpaCommit -cne '3dc7c569f31ca2cd4a20ed6f7db780327e6714c5' -or
        $dependencies.onnxRuntimeDirectMlVersion -cne '1.24.4' -or
        $dependencies.onnxRuntimeDirectMlPackageSha256 -cne '57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681' -or
        $dependencies.directMlVersion -cne '1.15.4' -or
        $dependencies.directMlPackageSha256 -cne '4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9') {
        throw 'BUILD-INFO runtime dependencies are not the approved versions.'
    }

    $runtimeRoot = Join-Path $extractRoot 'Runtime\win-x64'
    $runtimeManifest = Get-Content -Raw -LiteralPath `
        (Join-Path $runtimeRoot 'runtime.dependencies.json') | ConvertFrom-Json
    foreach ($property in @(
        'directMlAdapterSelectionVersion',
        'sherpaCommit',
        'onnxRuntimeDirectMlVersion',
        'onnxRuntimeDirectMlPackageSha256',
        'directMlVersion',
        'directMlPackageSha256')) {
        if ($runtimeManifest.$property -cne $dependencies.$property) {
            throw "Runtime manifest disagrees with BUILD-INFO: $property"
        }
    }
    foreach ($nativeName in @('DirectML.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'sherpa-onnx-cxx-api.dll')) {
        $matches = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter $nativeName)
        $expected = Join-Path $runtimeRoot $nativeName
        if ($matches.Count -ne 1 -or $matches[0].FullName -ine $expected) {
            throw "Expected one native runtime file at $expected."
        }
    }

    $hashEntries = @{}
    foreach ($line in @(Get-Content -LiteralPath (Join-Path $runtimeRoot 'runtime.sha256'))) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -ne 2 -or $parts[0] -ne [IO.Path]::GetFileName($parts[0]) -or
            $parts[1] -cnotmatch '^[0-9a-f]{64}$' -or $hashEntries.ContainsKey($parts[0])) {
            throw "Invalid native runtime hash line: $line"
        }
        $hashEntries[$parts[0]] = $parts[1]
    }
    $runtimeDlls = @(Get-ChildItem -LiteralPath $runtimeRoot -Filter '*.dll' -File)
    if ($hashEntries.Count -ne $runtimeDlls.Count) {
        throw 'Native runtime hash list does not cover every DLL exactly once.'
    }
    foreach ($dll in $runtimeDlls) {
        if (-not $hashEntries.ContainsKey($dll.Name) -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $dll.FullName).Hash -ine $hashEntries[$dll.Name]) {
            throw "Native runtime hash mismatch: $($dll.Name)"
        }
    }

    Assert-ValidSignature -Path $Installer
    Assert-ValidSignature -Path (Join-Path $extractRoot 'WinWispr.exe')
    Assert-ValidSignature -Path (Join-Path $extractRoot 'WinWispr.dll')
    Write-Output 'RELEASE_SECURITY=PASS'
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}
