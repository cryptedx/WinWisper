[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$PreviousTag,
    [Parameter(Mandatory)]
    [ValidatePattern('^(?:[0-9a-f]{40}|[0-9a-f]{64})$')]
    [string]$SourceCommit,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$SourceArchiveSha256,
    [Parameter(Mandatory)]
    [string]$CommitMessagesPath,
    [Parameter(Mandatory)]
    [string]$PortableArchivePath,
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    # The WinWispr Models installer the acceptance run installed; recorded, not part of the release assets.
    [Parameter(Mandatory)]
    [string]$ModelPackagePath,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-F]{40}$')]
    [string]$SigningThumbprint,
    [Parameter(Mandatory)]
    [string]$SbomPath,
    [Parameter(Mandatory)]
    [string]$AcceptanceLogPath,
    [Parameter(Mandatory)]
    [string]$LogDirectory,
    [Parameter(Mandatory)]
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'ArtifactHash.ps1')

$blockedOutputSegments = '^(secrets?|models?|audio|transcripts?)$'
$fullOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
foreach ($segment in ($fullOutputRoot -split '[\\/]')) {
    if ($segment -match $blockedOutputSegments) {
        throw "Release evidence output path must not be below secrets, models, audio, or transcripts: $OutputRoot"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    Get-ArtifactSha256 -LiteralPath $Path
}

function Test-ReparseItem {
    param([Parameter(Mandatory)]$Item)
    (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [switch]$RequireExists
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($RequireExists -and -not (Test-Path -LiteralPath $fullPath)) {
        throw "${Name} is missing: $fullPath"
    }

    $root = [IO.Path]::GetPathRoot($fullPath)
    $current = $root
    foreach ($segment in @($fullPath.Substring($root.Length) -split '[\\/]' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-ReparseItem -Item $item) {
                throw "${Name} contains a reparse point: $current"
            }
        }
        else {
            break
        }
    }
    $fullPath
}

function Assert-NoReparseTree {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $root = Assert-NoReparsePath -Path $Path -Name $Name -RequireExists
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "${Name} is not a directory: $root"
    }

    $pending = New-Object 'System.Collections.Generic.List[string]'
    $pending.Add($root)
    for ($index = 0; $index -lt $pending.Count; $index++) {
        foreach ($child in @(Get-ChildItem -LiteralPath $pending[$index] -Force)) {
            if (Test-ReparseItem -Item $child) {
                throw "${Name} contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Add($child.FullName)
            }
        }
    }
    $root
}

function Get-TreeFiles {
    param(
        [Parameter(Mandatory)][string]$Path
    )

    $root = Assert-NoReparsePath -Path $Path -Name 'File tree' -RequireExists
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "File tree is not a directory: $root"
    }

    $pending = New-Object 'System.Collections.Generic.List[string]'
    $files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    $pending.Add($root)
    for ($index = 0; $index -lt $pending.Count; $index++) {
        foreach ($child in @(Get-ChildItem -LiteralPath $pending[$index] -Force)) {
            if (Test-ReparseItem -Item $child) {
                throw "File tree contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Add($child.FullName)
            }
            else {
                [void]$files.Add($child)
            }
        }
    }
    @($files)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BasePath
    )

    $baseUri = [Uri]::new(([IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar))
    $pathUri = [Uri]::new([IO.Path]::GetFullPath($Path))
    [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('\', '/')
}

function Assert-LeafFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Name = 'Required evidence input'
    )
    $fullPath = Assert-NoReparsePath -Path $Path -Name $Name -RequireExists
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "${Name} is missing: $Path"
    }
    $fullPath
}

function Read-SidecarHash {
    param(
        [Parameter(Mandatory)][string]$SidecarPath,
        [Parameter(Mandatory)][string]$ExpectedFileName
    )

    $safeSidecarPath = Assert-LeafFile -Path $SidecarPath -Name 'SHA-256 sidecar'
    $record = (Get-Content -LiteralPath $SidecarPath -Raw).Trim()
    $match = [regex]::Match($record, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
    if (-not $match.Success) {
        throw "SHA-256 sidecar is invalid: $SidecarPath"
    }
    if ($match.Groups[2].Value.Trim() -cne $ExpectedFileName) {
        throw "SHA-256 sidecar must reference ${ExpectedFileName}: $SidecarPath"
    }
    $match.Groups[1].Value.ToLowerInvariant()
}

function Assert-SidecarMatches {
    param([Parameter(Mandatory)][string]$Path)
    $expectedName = [IO.Path]::GetFileName($Path)
    $recorded = Read-SidecarHash -SidecarPath "$Path.sha256" -ExpectedFileName $expectedName
    $actual = Get-Sha256 -Path $Path
    if ($recorded -cne $actual) {
        throw "SHA-256 sidecar mismatch for ${expectedName}: expected $recorded, got $actual"
    }
}

function Assert-AcceptanceLog {
    param([Parameter(Mandatory)][string]$Path)

    $lines = [IO.File]::ReadAllLines($Path)
    $localAppData = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    } else {
        $env:LOCALAPPDATA
    }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'LOCALAPPDATA is required to validate INSTALL_PATH acceptance marker.'
    }
    $expectedInstallPath = [IO.Path]::GetFullPath((Join-Path $localAppData 'Programs\WinWispr\WinWispr.exe'))

    foreach ($entry in @(
        @{ Key = 'INTERACTIVE_SESSION'; Value = '1' },
        @{ Key = 'APP_RUNNING'; Value = '1' },
        @{ Key = 'INSTALL_PATH'; Value = $expectedInstallPath },
        @{ Key = 'VERIFY'; Value = 'PASS' }
    )) {
        $key = $entry.Key
        $marker = "$key=$($entry.Value)"
        $keyCount = @($lines | Where-Object { $_ -clike "$key=*" }).Count
        $valueCount = @($lines | Where-Object { $_ -ceq $marker }).Count
        if ($keyCount -ne 1 -or $valueCount -ne 1) {
            throw "Acceptance log must contain exactly one ${marker}; found key=$keyCount value=$valueCount."
        }
    }

    $preservedCount = @($lines | Where-Object { $_ -ceq 'APP_DATA=preserved' }).Count
    $notPresentCount = @($lines | Where-Object { $_ -ceq 'APP_DATA=not-present' }).Count
    $appDataCount = @($lines | Where-Object { $_ -clike 'APP_DATA=*' }).Count
    if (($preservedCount + $notPresentCount) -ne 1 -or $appDataCount -ne 1) {
        throw "Acceptance log must contain exactly one APP_DATA state (APP_DATA=preserved or APP_DATA=not-present); found total=$appDataCount preserved=$preservedCount not-present=$notPresentCount."
    }
}

function Copy-ExactFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [string]$Name
    )

    $leaf = if ([string]::IsNullOrWhiteSpace($Name)) { [IO.Path]::GetFileName($Path) } else { $Name }
    Copy-Item -LiteralPath $Path -Destination (Join-Path $DestinationDirectory $leaf) -Force
}

function Remove-OwnedPath {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (Test-ReparseItem -Item $item) {
        throw "Refusing to remove owned path after it became a reparse point: $Path"
    }
    if ($item.PSIsContainer) {
        Assert-NoReparseTree -Path $Path -Name 'Owned cleanup path' | Out-Null
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function ConvertTo-JsonLiteral {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        return 'null'
    }
    '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t') + '"'
}

function Split-CommitMessageRecords {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    $records = @([regex]::Split($Text, [regex]::Escape([string][char]0x1e)))
    foreach ($record in $records) {
        if ([string]::IsNullOrWhiteSpace($record)) {
            throw 'Commit message record must not be empty or whitespace.'
        }
    }
    $records
}

$fullOutputRoot = Assert-NoReparsePath -Path $fullOutputRoot -Name 'OutputRoot'
$outputRootExisted = Test-Path -LiteralPath $fullOutputRoot
if (-not $outputRootExisted) {
    New-Item -ItemType Directory -Force -Path $fullOutputRoot | Out-Null
}
$fullOutputRoot = Assert-NoReparsePath -Path $fullOutputRoot -Name 'OutputRoot' -RequireExists

$portableArchive = Assert-LeafFile -Path $PortableArchivePath -Name 'Portable archive'
$installer = Assert-LeafFile -Path $InstallerPath -Name 'Installer'
$modelPackage = Assert-LeafFile -Path $ModelPackagePath -Name 'Model package'
$sbom = Assert-LeafFile -Path $SbomPath -Name 'SBOM'
$acceptanceLog = Assert-LeafFile -Path $AcceptanceLogPath -Name 'Acceptance log'
$commitMessages = Assert-LeafFile -Path $CommitMessagesPath -Name 'Commit messages'
$fullLogDirectory = Assert-NoReparseTree -Path $LogDirectory -Name 'LogDirectory'
$canonicalNotices = Assert-LeafFile -Path (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) 'THIRD-PARTY-NOTICES.md') -Name 'THIRD-PARTY-NOTICES'

Assert-SidecarMatches -Path $portableArchive
Assert-SidecarMatches -Path $installer
Assert-SidecarMatches -Path $modelPackage
# The models ship only in the WinWispr Models installer.
$portableZip = [IO.Compression.ZipFile]::OpenRead($portableArchive)
try {
    if (@($portableZip.Entries | Where-Object { $_.FullName -match '(?i)^(ModelData|TranslationData|VoiceData)/' }).Count -ne 0) {
        throw 'Portable archive must not contain models.'
    }
}
finally {
    $portableZip.Dispose()
}
Assert-AcceptanceLog -Path $acceptanceLog

$tagName = "v$Version"
$finalDirectory = Join-Path $fullOutputRoot $tagName
$archivePath = Join-Path $fullOutputRoot ('WinWispr-release-v' + $Version + '.zip')
$archiveSidecar = "$archivePath.sha256"
$stagingDirectory = Join-Path $fullOutputRoot ('.staging-' + $tagName + '-' + [guid]::NewGuid().ToString('N'))
$completed = $false
$createdStaging = $false
$createdFinalDirectory = $false
$createdArchive = $false
$createdArchiveSidecar = $false

if (Test-Path -LiteralPath $finalDirectory) {
    throw "Release evidence directory already exists: $finalDirectory"
}
if (Test-Path -LiteralPath $archivePath) {
    throw "Release evidence archive already exists: $archivePath"
}
if (Test-Path -LiteralPath $archiveSidecar) {
    throw "Release evidence archive sidecar already exists: $archiveSidecar"
}
Assert-NoReparsePath -Path $finalDirectory -Name 'Release evidence directory' | Out-Null
Assert-NoReparsePath -Path $archivePath -Name 'Release evidence archive' | Out-Null
Assert-NoReparsePath -Path $archiveSidecar -Name 'Release evidence archive sidecar' | Out-Null

New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null
$createdStaging = $true
try {
    $logTarget = Join-Path $stagingDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logTarget | Out-Null

    Copy-ExactFile -Path $portableArchive -DestinationDirectory $stagingDirectory
    Copy-ExactFile -Path "$portableArchive.sha256" -DestinationDirectory $stagingDirectory
    Copy-ExactFile -Path $installer -DestinationDirectory $stagingDirectory
    Copy-ExactFile -Path "$installer.sha256" -DestinationDirectory $stagingDirectory
    Copy-ExactFile -Path $sbom -DestinationDirectory $stagingDirectory -Name 'SBOM.cdx.json'
    Copy-ExactFile -Path $acceptanceLog -DestinationDirectory $stagingDirectory -Name 'VM-ACCEPTANCE.txt'
    Copy-Item -Path (Join-Path $fullLogDirectory '*') -Destination $logTarget -Recurse -Force

    $messages = @(Split-CommitMessageRecords -Text ([IO.File]::ReadAllText($commitMessages, [Text.UTF8Encoding]::new($false))))
    $changelog = [Collections.Generic.List[string]]::new()
    foreach ($line in @("# WinWispr $tagName", '', "Previous tag: $PreviousTag", "Source commit: $SourceCommit", '', '## Changes')) {
        [void]$changelog.Add($line)
    }
    foreach ($message in $messages) {
        if (-not [string]::IsNullOrWhiteSpace($message)) {
            [void]$changelog.Add("- $message")
        }
    }
    $changelog | Set-Content -LiteralPath (Join-Path $stagingDirectory "CHANGELOG-$tagName.md") -Encoding utf8

    @(
        "# Security Report $tagName",
        '',
        'enterpriseReady=false',
        'vmVerified=true',
        '',
        "Signed with WinWispr's own self-signed certificate $SigningThumbprint (CN=WinWispr); it is no public code-signing certificate.",
        'Internal release evidence only. Trusted-CA signing, vulnerability assessment, and license gates are not completed here.',
        'No Git tag, Git push, signing key, model, audio, transcript, telemetry, or hosted release operation is performed by this evidence builder.'
    ) | Set-Content -LiteralPath (Join-Path $stagingDirectory 'SECURITY-REPORT.md') -Encoding utf8

    Copy-ExactFile -Path $canonicalNotices -DestinationDirectory $stagingDirectory -Name 'THIRD-PARTY-NOTICES.md'

    $messageJson = @($messages | ForEach-Object { ConvertTo-JsonLiteral -Value $_ }) -join ', '
    @(
        '{',
        ('  "version": ' + (ConvertTo-JsonLiteral -Value $Version) + ','),
        ('  "tag": ' + (ConvertTo-JsonLiteral -Value $tagName) + ','),
        ('  "previousTag": ' + (ConvertTo-JsonLiteral -Value $PreviousTag) + ','),
        ('  "sourceCommit": ' + (ConvertTo-JsonLiteral -Value $SourceCommit) + ','),
        ('  "generatedAtUtc": ' + (ConvertTo-JsonLiteral -Value ([DateTime]::UtcNow.ToString('o'))) + ','),
        '  "enterpriseReady": false,',
        '  "vmVerified": true,',
        ('  "sourceArchiveSha256": ' + (ConvertTo-JsonLiteral -Value $SourceArchiveSha256.ToLowerInvariant()) + ','),
        ('  "portableArchive": ' + (ConvertTo-JsonLiteral -Value ([IO.Path]::GetFileName($portableArchive))) + ','),
        ('  "installer": ' + (ConvertTo-JsonLiteral -Value ([IO.Path]::GetFileName($installer))) + ','),
        ('  "signingThumbprint": ' + (ConvertTo-JsonLiteral -Value $SigningThumbprint) + ','),
        ('  "modelPackage": ' + (ConvertTo-JsonLiteral -Value ([IO.Path]::GetFileName($modelPackage))) + ','),
        ('  "modelPackageSha256": ' + (ConvertTo-JsonLiteral -Value (Get-Sha256 -Path $modelPackage)) + ','),
        ('  "commitsSincePreviousTag": [' + $messageJson + ']'),
        '}'
    ) | Set-Content -LiteralPath (Join-Path $stagingDirectory 'BUILD-INFO.json') -Encoding utf8

    $shaLines = @(Get-TreeFiles -Path $stagingDirectory |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = Get-RelativePath -Path $_.FullName -BasePath $stagingDirectory
            "$(Get-Sha256 -Path $_.FullName) *$relative"
        })
    $shaLines | Set-Content -LiteralPath (Join-Path $stagingDirectory 'SHA256SUMS.txt') -Encoding ascii

    Move-Item -LiteralPath $stagingDirectory -Destination $finalDirectory
    $createdStaging = $false
    $createdFinalDirectory = $true
    $createdArchive = $true
    # Match buffered hashing: avoid tiny network writes for large ZIP payloads.
    $archiveStream = [IO.FileStream]::new($archivePath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None, 1MB)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($file in @(Get-TreeFiles -Path $finalDirectory | Sort-Object FullName)) {
            $compression = if ($file.Extension -in @('.zip', '.exe')) {
                [IO.Compression.CompressionLevel]::NoCompression
            } else { [IO.Compression.CompressionLevel]::Optimal }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                $file.FullName,
                (Get-RelativePath -Path $file.FullName -BasePath $finalDirectory),
                $compression) | Out-Null
        }
    }
    finally {
        try { if ($null -ne $zip) { $zip.Dispose() } }
        finally { $archiveStream.Dispose() }
    }
    $archiveHash = Get-Sha256 -Path $archivePath
    $createdArchiveSidecar = $true
    Set-Content -LiteralPath $archiveSidecar -Value "$archiveHash *$([IO.Path]::GetFileName($archivePath))" -Encoding ascii
    $completed = $true

    Write-Output "RELEASE_ARCHIVE=$archivePath"
    Write-Output "RELEASE_SHA256=$archiveHash"
}
finally {
    if (-not $completed) {
        if ($createdStaging) { Remove-OwnedPath -Path $stagingDirectory }
        if ($createdFinalDirectory) { Remove-OwnedPath -Path $finalDirectory }
        if ($createdArchive) { Remove-OwnedPath -Path $archivePath }
        if ($createdArchiveSidecar) { Remove-OwnedPath -Path $archiveSidecar }
    }
}
