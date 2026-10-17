# Builds the WinWispr Models installer (installer\WinWisprModels.iss) from the local, pinned model inputs:
# the speech model archive (artifacts\model), the translation model (artifacts\translation-model) and the
# offline voices (artifacts\voices). Every input is checked against its pin before it is staged; nothing is
# downloaded. Writes artifacts\models\WinWispr-Models-vN.exe and its .sha256 sidecar, N from
# eng\model-package.json.
[CmdletBinding()]
param(
    [string]$InnoSetupPath,
    [string]$OutputDirectory,
    # Signs the installer and its uninstaller with the pinned certificate (eng\CodeSigning.ps1).
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArtifactHash.ps1')
. (Join-Path $PSScriptRoot 'ModelPackage.ps1')
. (Join-Path $PSScriptRoot 'CodeSigning.ps1')
. (Join-Path $PSScriptRoot 'Copy-TtsVoices.ps1')
. (Join-Path $PSScriptRoot 'Copy-TranslationModel.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repositoryRoot 'artifacts\models'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$package = Read-ModelPackage -EngRoot $PSScriptRoot
$packageName = Get-ModelPackageName -Package $package
$packagePath = Join-Path $outputDirectory $packageName
$hashPath = "$packagePath.sha256"
$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('WinWispr-models-' + [Guid]::NewGuid().ToString('N'))
$signingPin = if ($Sign) { Read-CodeSigningPin } else { $null }

function Resolve-InnoCompiler {
    param([string]$ExplicitPath)
    $candidates = @($ExplicitPath, (Get-Command 'iscc.exe' -ErrorAction SilentlyContinue | ForEach-Object Source))
    foreach ($root in @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        foreach ($version in @('7', '6')) { $candidates += (Join-Path $root "Inno Setup $version\ISCC.exe") }
    }
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }
    throw 'ISCC.exe was not found. Install Inno Setup 6 or 7 in the build environment or pass -InnoSetupPath.'
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null
try {
    $speechArchive = Assert-SpeechModelArchive -RepositoryRoot $repositoryRoot -Package $package
    New-Item -ItemType Directory -Force -Path (Join-Path $stagingDirectory 'ModelData') | Out-Null
    $stagedArchive = Join-Path $stagingDirectory 'ModelData\model.tar.bz2'
    Copy-Item -LiteralPath $speechArchive -Destination $stagedArchive
    if ((Get-ArtifactSha256 -LiteralPath $stagedArchive) -cne [string]$package.speechArchive.sha256) {
        throw 'Copied speech model archive integrity failed.'
    }
    Copy-TranslationModel -PinsPath (Join-Path $PSScriptRoot 'translation-model-pins.json') `
        -InputRoot (Join-Path $repositoryRoot 'artifacts\translation-model') `
        -Destination (Join-Path $stagingDirectory 'TranslationData')
    Copy-TtsVoices -ManifestRoot (Join-Path $PSScriptRoot 'voices') `
        -InputRoot (Join-Path $repositoryRoot 'artifacts\voices') `
        -Destination (Join-Path $stagingDirectory 'VoiceData')

    $compiler = Resolve-InnoCompiler -ExplicitPath $InnoSetupPath
    Remove-Item -LiteralPath $packagePath, $hashPath -Force -ErrorAction SilentlyContinue
    $compilerArguments = @(
        '/Qp',
        "/DSourceDir=$stagingDirectory",
        "/DOutputDir=$outputDirectory",
        "/DPackageVersion=$([int]$package.version)",
        (Join-Path $repositoryRoot 'installer\WinWisprModels.iss'))
    if ($Sign) {
        $compilerArguments = @('/DSign', (Get-InnoSignToolArgument -Pin $signingPin)) + $compilerArguments
    }
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation of the model package failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Model package was not produced: $packagePath"
    }
    if ($Sign) {
        Assert-CodeSignature -Path $packagePath -Pin $signingPin
    }
    $hash = Get-ArtifactSha256 -LiteralPath $packagePath
    [IO.File]::WriteAllText($hashPath, "$hash *$packageName`r`n", [Text.Encoding]::ASCII)
    Write-Output "Model package: $packagePath"
    Write-Output "SHA256: $hash"
}
finally {
    for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $stagingDirectory); $attempt++) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $stagingDirectory) { Start-Sleep -Milliseconds 500 }
    }
}
