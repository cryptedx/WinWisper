param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '2.3.4'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\eng\ArtifactHash.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$portableArchive = Join-Path $repositoryRoot ('artifacts\portable\WinWispr-portable-v' + $Version + '.zip')
$portableSidecar = "$portableArchive.sha256"
$installerPath = Join-Path $repositoryRoot ('artifacts\installer\WinWispr-Setup-v' + $Version + '.exe')
$installerSidecar = "$installerPath.sha256"
$runtimeDirectory = Join-Path $repositoryRoot 'src\WinWispr\Runtime\win-x64'

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

function Test-RuntimeReady {
    param(
        [Parameter(Mandatory)]
        [string]$DirectoryPath
    )

    if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container)) {
        return $false
    }

    foreach ($required in @(
        'DirectML.dll',
        'onnxruntime.dll',
        'sherpa-onnx-c-api.dll',
        'sherpa-onnx-cxx-api.dll',
        'runtime.sha256')) {
        if (-not (Test-Path -LiteralPath (Join-Path $DirectoryPath $required) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

# The models ship only in the WinWispr Models installer (eng/Build-ModelPackage.ps1).
function Test-ModelFreePortable {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath
    )

    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $models = @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '(?i)^(ModelData|TranslationData|VoiceData)/' })
        if ($models.Count -ne 0) {
            throw "Portable ZIP contains model files: $($models.FullName -join ', ')"
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Read-SidecarHash {
    param(
        [Parameter(Mandatory)]
        [string]$SidecarPath,
        [Parameter(Mandatory)]
        [string]$ExpectedName
    )

    $content = (Get-Content -LiteralPath $SidecarPath -Raw).Trim()
    $match = [regex]::Match($content, '^([0-9a-f]{64}) \*(.+)$')
    if (-not $match.Success) {
        throw "Invalid SHA-256 sidecar: $SidecarPath"
    }
    if ($match.Groups[2].Value -cne $ExpectedName) {
        throw "SHA-256 sidecar references $($match.Groups[2].Value), expected $ExpectedName"
    }

    return $match.Groups[1].Value
}

Push-Location $repositoryRoot
try {
    if (-not (Test-RuntimeReady -DirectoryPath $runtimeDirectory)) {
        Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $repositoryRoot 'eng\Build-SherpaRuntime.ps1'))
    }

    Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $repositoryRoot 'eng\Build-Portable.ps1'),
        '-Version', $Version)

    Test-ModelFreePortable -ArchivePath $portableArchive

    $portableBefore = Get-ArtifactSha256 -LiteralPath $portableArchive
    Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $repositoryRoot 'eng\Build-Installer.ps1'),
        '-Version', $Version,
        '-PortableArchivePath', $portableArchive)
    $portableAfter = Get-ArtifactSha256 -LiteralPath $portableArchive
    if ($portableBefore -cne $portableAfter) {
        throw "Portable hash changed during installer build: before=$portableBefore after=$portableAfter"
    }

    $recordedPortableHash = Read-SidecarHash -SidecarPath $portableSidecar -ExpectedName ([IO.Path]::GetFileName($portableArchive))
    if ($recordedPortableHash -cne $portableBefore) {
        throw "Portable sidecar hash mismatch: expected $portableBefore, got $recordedPortableHash"
    }

    $installerHash = Get-ArtifactSha256 -LiteralPath $installerPath
    $recordedInstallerHash = Read-SidecarHash -SidecarPath $installerSidecar -ExpectedName ([IO.Path]::GetFileName($installerPath))
    if ($recordedInstallerHash -cne $installerHash) {
        throw "Installer sidecar hash mismatch: expected $installerHash, got $recordedInstallerHash"
    }

    Write-Output "PORTABLE_BEFORE_SHA256=$portableBefore"
    Write-Output "PORTABLE_AFTER_SHA256=$portableAfter"
    Write-Output "INSTALLER_SHA256=$installerHash"
    Write-Output "PORTABLE_PATH=$portableArchive"
    Write-Output "INSTALLER_PATH=$installerPath"
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $portableArchive, $portableSidecar, $installerPath, $installerSidecar -Force -ErrorAction SilentlyContinue
}
