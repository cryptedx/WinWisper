[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '1.0.0',
    [string]$ArtifactDirectory,
    [string]$SourceCommit,
    [string]$SourceArchiveSha256,
    [ValidateSet('rebuilt', 'cache')]
    [string]$RuntimeBuild,
    [switch]$NoBuild,
    # Signs WinWispr.exe and WinWispr.dll with the pinned certificate (eng/CodeSigning.ps1) before zipping.
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArtifactHash.ps1')
. (Join-Path $PSScriptRoot 'CodeSigning.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactDirectory = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    Join-Path $repositoryRoot 'artifacts\portable'
} else {
    [IO.Path]::GetFullPath($ArtifactDirectory)
}
$artifactKind = 'portable'
$archivePrefix = 'WinWispr-portable-v'
$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) ('WinWispr-publish-' + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $artifactDirectory ($archivePrefix + $Version + '.zip')
$hashPath = "$archivePath.sha256"
$temporaryArchivePath = Join-Path $artifactDirectory ('WinWispr-' + $artifactKind + '-tmp-' + [Guid]::NewGuid().ToString('N') + '.zip')
$temporaryHashPath = "$temporaryArchivePath.sha256"
$promotionLockPath = Join-Path $artifactDirectory ('WinWispr-' + $artifactKind + '.lock')

function Invoke-RequiredCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

function Test-PortableEntries {
    param(
        [Parameter(Mandatory)]
        [string]$DirectoryPath
    )

    $rootPath = (Get-Item -LiteralPath $DirectoryPath).FullName
    $names = @(Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File |
        ForEach-Object { $_.FullName.Substring($rootPath.Length + 1).Replace('\', '/') })
    # The models ship only in the WinWispr Models package (eng/Build-ModelPackage.ps1).
    $forbidden = @($names | Where-Object {
        $_ -match '(?i)^(ModelData|TranslationData|VoiceData)/' -or
        $_ -match '(?i)\.(onnx|wav|flac|mp3|m4a|ogg|pcm|gguf|appinstaller)$' -or
        $_ -match '(?i)(^|/)tokens\.txt$' -or
        $_ -match '(?i)(history|transcript|polish|whisper|llama)'
    })
    if ($forbidden.Count -gt 0) {
        throw "Forbidden portable payload: $($forbidden -join ', ')"
    }

    $requiredEntries = @(
        'DirectML.dll',
        'onnxruntime.dll',
        'sherpa-onnx-c-api.dll',
        'sherpa-onnx-cxx-api.dll',
        'runtime.sha256',
        'runtime.dependencies.json',
        'BUILD-INFO.json',
        'WinWispr.pri'
    )
    foreach ($required in $requiredEntries) {
        if (-not ($names | Where-Object { $_.EndsWith($required, [StringComparison]::OrdinalIgnoreCase) })) {
            throw "Missing native runtime file: $required"
        }
    }

    foreach ($nativeName in @('DirectML.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'sherpa-onnx-cxx-api.dll')) {
        $matches = @($names | Where-Object { [IO.Path]::GetFileName($_) -ieq $nativeName })
        $expected = "Runtime/win-x64/$nativeName"
        if ($matches.Count -ne 1 -or $matches[0] -ine $expected) {
            throw "Expected one native runtime file at $expected, found: $($matches -join ', ')"
        }
    }

    $executables = @($names | Where-Object { $_.EndsWith('WinWispr.exe', [StringComparison]::OrdinalIgnoreCase) })
    if ($executables.Count -ne 1) {
        throw "Expected exactly one WinWispr.exe, found $($executables.Count)."
    }
    if (-not ($names | Where-Object { $_.EndsWith('THIRD-PARTY-NOTICES.md', [StringComparison]::OrdinalIgnoreCase) })) {
        throw 'Missing THIRD-PARTY-NOTICES.md from the portable build.'
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
$signingPin = if ($Sign) { Read-CodeSigningPin } else { $null }

$provenanceValues = @($SourceCommit, $SourceArchiveSha256, $RuntimeBuild)
$provenanceProvided = @($provenanceValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
if ($provenanceProvided -ne 0 -and $provenanceProvided -ne 3) {
    throw 'SourceCommit, SourceArchiveSha256, and RuntimeBuild must be supplied together.'
}
if ($provenanceProvided -eq 0) {
    $SourceCommit = 'untracked'
    $SourceArchiveSha256 = 'unverified'
    $RuntimeBuild = 'cache'
}
elseif ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$' -or $SourceArchiveSha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'Release provenance values are invalid.'
}

try {
    $dotnetArguments = @(
        'publish',
        (Join-Path $repositoryRoot 'src\WinWispr\WinWispr.csproj'),
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:Platform=x64',
        '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:ResolveAssemblyReferencesFindRelatedSatellites=false',
        '-p:PublishSingleFile=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        "-p:InformationalVersion=$Version",
        '-o', $publishDirectory
    )
    if ($NoBuild) { $dotnetArguments += '--no-build' }
    Invoke-RequiredCommand -FilePath 'dotnet.exe' -ArgumentList $dotnetArguments
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory 'WinWispr.dll'))
    $publishedVersion = '{0}.{1}.{2}.{3}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart, $versionInfo.FilePrivatePart
    if ($publishedVersion -ne "$Version.0") {
        throw "Published assembly version mismatch: expected $Version.0, got $publishedVersion. Rebuild with the requested version."
    }
    if ($Sign) {
        Invoke-CodeSign -Pin $signingPin -Path @(
            (Join-Path $publishDirectory 'WinWispr.exe'),
            (Join-Path $publishDirectory 'WinWispr.dll'))
    }
    foreach ($duplicate in @('DirectML.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'sherpa-onnx-cxx-api.dll')) {
        $duplicatePath = Join-Path $publishDirectory $duplicate
        if (Test-Path -LiteralPath $duplicatePath -PathType Leaf) {
            Remove-Item -LiteralPath $duplicatePath -Force
        }
    }
    $runtimeDependencies = Get-Content -Raw -LiteralPath `
        (Join-Path $publishDirectory 'Runtime\win-x64\runtime.dependencies.json') | ConvertFrom-Json
    if ($runtimeDependencies.directMlAdapterSelectionVersion -ne 1 -or $runtimeDependencies.ttsEnabled -ne $true -or $runtimeDependencies.ttsBoundaryVersion -ne 2) {
        throw 'Rebuild the native runtime with eng/Build-SherpaRuntime.ps1 before packaging GPU device selection.'
    }
    [ordered]@{
        sourceCommit = $SourceCommit.ToLowerInvariant()
        sourceArchiveSha256 = $SourceArchiveSha256.ToLowerInvariant()
        buildUtc = [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
        runtimeBuild = $RuntimeBuild
        runtimeDependencies = $runtimeDependencies
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath `
        (Join-Path $publishDirectory 'BUILD-INFO.json') -Encoding utf8
    Test-PortableEntries -DirectoryPath $publishDirectory

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Deflate emits small writes; buffer them before sending ZIP data over SMB.
    $archiveStream = [IO.FileStream]::new($temporaryArchivePath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None, 1MB)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -Recurse -File) {
            $relative = $file.FullName.Substring($publishDirectory.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        try { if ($null -ne $zip) { $zip.Dispose() } }
        finally { $archiveStream.Dispose() }
    }
    $hash = (Get-ArtifactSha256 -LiteralPath $temporaryArchivePath)
    [IO.File]::WriteAllText($temporaryHashPath, "$hash *$([IO.Path]::GetFileName($archivePath))`r`n", [Text.Encoding]::ASCII)

    $promotionLock = $null
    $promotionLock = [IO.File]::Open($promotionLockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archivePattern = '^WinWispr-portable-v[0-9]+\.[0-9]+\.[0-9]+\.zip$'
        $stalePattern = $archivePattern.Replace('.zip$', '.zip(?:\.sha256)?$')
        $existingArchives = @(Get-ChildItem -LiteralPath $artifactDirectory -File |
            Where-Object {
                $_.Name -match $archivePattern -and
                (Test-Path -LiteralPath "$($_.FullName).sha256")
            } |
            Sort-Object LastWriteTime -Descending)
        $backupArchivePath = $null
        $backupHashPath = $null
        if ($existingArchives.Count -gt 0) {
            $existingArchive = $existingArchives[0]
            $existingHashPath = "$($existingArchive.FullName).sha256"
            $backupArchivePath = Join-Path $artifactDirectory ('WinWispr-' + $artifactKind + '-backup-' + [Guid]::NewGuid().ToString('N') + '.zip')
            $backupHashPath = "$backupArchivePath.sha256"
            [IO.File]::Move($existingArchive.FullName, $backupArchivePath)
            try {
                [IO.File]::Move($existingHashPath, $backupHashPath)
            }
            catch {
                [IO.File]::Move($backupArchivePath, $existingArchive.FullName)
                throw
            }
        }

        try {
            $staleArtifacts = @(Get-ChildItem -LiteralPath $artifactDirectory -File | Where-Object {
                $_.Name -match $stalePattern
            })
            if ($staleArtifacts.Count -gt 0) {
                Remove-Item -LiteralPath $staleArtifacts.FullName -Force
            }
            [IO.File]::Move($temporaryArchivePath, $archivePath)
            [IO.File]::Move($temporaryHashPath, $hashPath)
        }
        catch {
            Remove-Item -LiteralPath $archivePath, $hashPath -Force -ErrorAction SilentlyContinue
            if (($null -ne $backupArchivePath) -and
                (Test-Path -LiteralPath $backupArchivePath) -and
                (Test-Path -LiteralPath $backupHashPath)) {
                try {
                    [IO.File]::Move($backupArchivePath, $existingArchive.FullName)
                    [IO.File]::Move($backupHashPath, $existingHashPath)
                }
                catch {
                    Remove-Item -LiteralPath $existingArchive.FullName, $existingHashPath -Force -ErrorAction SilentlyContinue
                    throw
                }
            }
            throw
        }

        if ($null -ne $backupArchivePath) {
            Remove-Item -LiteralPath $backupArchivePath, $backupHashPath -Force -ErrorAction SilentlyContinue
        }
    }
    finally {
        if ($null -ne $promotionLock) {
            $promotionLock.Dispose()
        }
        Remove-Item -LiteralPath $promotionLockPath -Force -ErrorAction SilentlyContinue
    }
    Write-Output "Portable ZIP: $([IO.Path]::GetFullPath($archivePath))"
    Write-Output "SHA256: $hash"
}
finally {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $publishDirectory, $temporaryArchivePath, $temporaryHashPath -Recurse -Force -ErrorAction SilentlyContinue
}
