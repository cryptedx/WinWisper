# The WinWispr Models package: the speech model archive, the translation model and the offline voices, which
# Setup and Portable no longer contain. eng/model-package.json names its version and pins its content with
# contentSha256, the SHA-256 of these LF-terminated lines:
#   speech <archive sha256> <archive bytes>
#   translation-pins <sha256 of eng/translation-model-pins.json>
#   voice <name> <sha256 of eng/voices/<name>.json>      (one line per voice, ordinal order)
# A change to any pin changes the digest, so it needs a new package version (tests/model-package-contract.sh).

function Get-ModelPackageDigest {
    param([Parameter(Mandatory)][string]$EngRoot, [Parameter(Mandatory)]$Package)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("speech $($Package.speechArchive.sha256) $($Package.speechArchive.bytes)")
    $lines.Add("translation-pins $(Get-ArtifactSha256 -LiteralPath (Join-Path $EngRoot 'translation-model-pins.json'))")
    [string[]]$voices = @(Get-ChildItem -LiteralPath (Join-Path $EngRoot 'voices') -Filter 'vits-piper-*.json' -File |
        ForEach-Object { $_.BaseName })
    [Array]::Sort($voices, [StringComparer]::Ordinal)
    foreach ($voice in $voices) {
        $lines.Add("voice $voice $(Get-ArtifactSha256 -LiteralPath (Join-Path (Join-Path $EngRoot 'voices') "$voice.json"))")
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes((($lines | ForEach-Object { "$_`n" }) -join ''))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# Reads eng/model-package.json and checks that its digest still matches the pins.
function Read-ModelPackage {
    param([Parameter(Mandatory)][string]$EngRoot)
    $package = Get-Content -Raw -LiteralPath (Join-Path $EngRoot 'model-package.json') | ConvertFrom-Json
    if ([int]$package.version -lt 1 -or [string]$package.speechArchive.sha256 -notmatch '^[a-f0-9]{64}$' -or
        [long]$package.speechArchive.bytes -le 0) {
        throw 'eng/model-package.json is invalid.'
    }
    $digest = Get-ModelPackageDigest -EngRoot $EngRoot -Package $package
    if ($digest -cne [string]$package.contentSha256) {
        throw "The model pins changed (content $digest); raise the version in eng/model-package.json and record the new contentSha256."
    }
    $package
}

function Get-ModelPackageName {
    param([Parameter(Mandatory)]$Package)
    "WinWispr-Models-v$([int]$Package.version).exe"
}

# Checks the speech model archive against its pin and returns its full path.
function Assert-SpeechModelArchive {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)]$Package)
    $path = Join-Path $RepositoryRoot ([string]$Package.speechArchive.path).Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Speech model archive is missing: $path"
    }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne [long]$Package.speechArchive.bytes) {
        throw "Speech model archive size mismatch: expected $($Package.speechArchive.bytes), got $($file.Length)"
    }
    $actual = Get-ArtifactSha256 -LiteralPath $path
    if ($actual -cne [string]$Package.speechArchive.sha256) {
        throw "Speech model archive SHA-256 mismatch: expected $($Package.speechArchive.sha256), got $actual"
    }
    $file.FullName
}
