[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '1.0.0',
    [string]$PortableArchivePath,
    [string]$InnoSetupPath,
    [switch]$KeepStaging,
    [string]$SourceCommit,
    [string]$SourceArchiveSha256,
    [ValidateSet('rebuilt', 'cache')]
    [string]$RuntimeBuild,
    # Signs Setup and its uninstaller (and, when this script builds Portable, WinWispr.exe and WinWispr.dll).
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArtifactHash.ps1')
. (Join-Path $PSScriptRoot 'CodeSigning.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactDirectory = Join-Path $repositoryRoot 'artifacts\installer'
$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('WinWispr-installer-' + [Guid]::NewGuid().ToString('N'))
$outputDirectory = $artifactDirectory
$portableScript = Join-Path $repositoryRoot 'eng\Build-Portable.ps1'
$installerScript = Join-Path $repositoryRoot 'installer\WinWispr.iss'
$installerPrefix = 'WinWispr-Setup-v'
$portablePrefix = 'WinWispr-portable-v'
$setupPath = Join-Path $outputDirectory ($installerPrefix + $Version + '.exe')
$hashPath = "$setupPath.sha256"

function Resolve-InnoCompiler {
    param([string]$ExplicitPath)

    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add($ExplicitPath)
    }

    $pathCommand = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        $candidates.Add($pathCommand.Source)
    }

    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
        foreach ($version in @('7', '6')) {
            $candidates.Add((Join-Path $localAppData "Programs\Inno Setup $version\ISCC.exe"))
        }
    }

    foreach ($programFiles in @(
        [Environment]::GetEnvironmentVariable('ProgramFiles'),
        [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
            foreach ($version in @('7', '6')) {
                $candidates.Add((Join-Path $programFiles "Inno Setup $version\ISCC.exe"))
            }
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    throw 'ISCC.exe was not found. Install Inno Setup 6 or 7 in the build environment or pass -InnoSetupPath; this script does not download or install it.'
}

function Invoke-PortableBuild {
    param(
        [string]$ScriptPath,
        [string]$Version,
        [string]$Commit,
        [string]$ArchiveSha256,
        [string]$RuntimeSource
    )

    $powershell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell -PathType Leaf)) {
        $powershell = 'powershell.exe'
    }

    $portableArguments = @('-Configuration', $Configuration, '-Version', $Version)
    if ($Sign) { $portableArguments += '-Sign' }
    if (-not [string]::IsNullOrWhiteSpace($Commit)) {
        $portableArguments += @('-SourceCommit', $Commit, '-SourceArchiveSha256', $ArchiveSha256, '-RuntimeBuild', $RuntimeSource)
    }
    $output = @(& $powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @portableArguments 2>&1 |
        ForEach-Object { $_.ToString() })
    $output | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "Portable build failed with exit code $LASTEXITCODE."
    }

    $portableLine = $output |
        Where-Object { $_ -like 'Portable ZIP: *' } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($portableLine)) {
        throw 'Portable build did not report its ZIP path.'
    }

    $portableArchive = $portableLine.Substring('Portable ZIP: '.Length).Trim()
    if (-not (Test-Path -LiteralPath $portableArchive -PathType Leaf)) {
        throw "Portable ZIP does not exist: $portableArchive"
    }
    return $portableArchive
}

function Get-RecordedSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$HashSidecar,
        [Parameter(Mandatory)]
        [string]$ExpectedFileName
    )

    if (-not (Test-Path -LiteralPath $HashSidecar -PathType Leaf)) {
        throw "Portable archive SHA-256 sidecar does not exist: $HashSidecar"
    }

    $record = (Get-Content -LiteralPath $hashSidecar -Raw).Trim()
    $match = [regex]::Match($record, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
    if (-not $match.Success) {
        throw "Portable archive SHA-256 sidecar is invalid: $HashSidecar"
    }

    if ($match.Groups[2].Value.Trim() -cne $ExpectedFileName) {
        throw "Portable archive SHA-256 sidecar must reference ${ExpectedFileName}: $HashSidecar"
    }

    return $match.Groups[1].Value.ToLowerInvariant()
}

function Resolve-PortableArchive {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,
        [Parameter(Mandatory)]
        [string]$Version
    )

    $resolvedArchive = [IO.Path]::GetFullPath($ArchivePath)
    if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) {
        throw "Portable archive does not exist: $resolvedArchive"
    }

    $expectedName = $portablePrefix + $Version + '.zip'
    if ([IO.Path]::GetFileName($resolvedArchive) -cne $expectedName) {
        throw "Portable archive name must be exactly ${expectedName}: $resolvedArchive"
    }

    $hashSidecar = "$resolvedArchive.sha256"
    $recordedHash = Get-RecordedSha256 -HashSidecar $hashSidecar -ExpectedFileName $expectedName
    $actualHash = (Get-ArtifactSha256 -LiteralPath $resolvedArchive)
    if ($actualHash -cne $recordedHash) {
        throw "Portable archive SHA-256 mismatch: expected $recordedHash, got $actualHash"
    }

    return $resolvedArchive
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null

try {
    $portableArchive = if ([string]::IsNullOrWhiteSpace($PortableArchivePath)) {
        if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
            Invoke-PortableBuild -ScriptPath $portableScript -Version $Version
        } else {
            Invoke-PortableBuild -ScriptPath $portableScript -Version $Version -Commit $SourceCommit `
                -ArchiveSha256 $SourceArchiveSha256 -RuntimeSource $RuntimeBuild
        }
    } else {
        Resolve-PortableArchive -ArchivePath $PortableArchivePath -Version $Version
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Deflate reads small blocks; buffer the archive before extracting over SMB.
    $archiveStream = [IO.FileStream]::new($portableArchive, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB)
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try { [IO.Compression.ZipFileExtensions]::ExtractToDirectory($zip, $stagingDirectory) }
        finally { $zip.Dispose() }
    }
    finally { $archiveStream.Dispose() }

    $compiler = Resolve-InnoCompiler -ExplicitPath $InnoSetupPath
    Remove-Item -LiteralPath $setupPath, $hashPath -Force -ErrorAction SilentlyContinue

    $compilerArguments = @(
        '/Qp',
        "/DSourceDir=$stagingDirectory",
        "/DOutputDir=$outputDirectory",
        "/DAppVersion=$Version",
        $installerScript
    )
    $signingPin = $null
    if ($Sign) {
        $signingPin = Read-CodeSigningPin
        $compilerArguments = @('/DSign', (Get-InnoSignToolArgument -Pin $signingPin)) + $compilerArguments
    }
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }

    $setupFiles = @(Get-ChildItem -LiteralPath $outputDirectory -Filter ($installerPrefix + $Version + '.exe') -File)
    if ($setupFiles.Count -ne 1) {
        throw "Expected exactly one $installerPrefix$Version.exe, found $($setupFiles.Count)."
    }

    if ($Sign) {
        Assert-CodeSignature -Path $setupFiles[0].FullName -Pin $signingPin
    }
    $hash = (Get-ArtifactSha256 -LiteralPath $setupFiles[0].FullName)
    [IO.File]::WriteAllText($hashPath, "$hash *$($setupFiles[0].Name)`r`n", [Text.Encoding]::ASCII)
    Write-Output "Installer: $($setupFiles[0].FullName)"
    Write-Output "SHA256: $hash"
}
finally {
    if (-not $KeepStaging) {
        for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $stagingDirectory); $attempt++) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $stagingDirectory) {
                Start-Sleep -Milliseconds 500
            }
        }
        if (Test-Path -LiteralPath $stagingDirectory) {
            throw "Installer staging directory could not be removed: $stagingDirectory"
        }
    }
}
