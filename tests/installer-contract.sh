#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo_root/installer/WinWispr.iss"
builder="$repo_root/eng/Build-Installer.ps1"
portable_builder="$repo_root/eng/Build-Portable.ps1"
reuse_smoke="$repo_root/tests/InstallerReuse.Smoke.ps1"

test -f "$script"
test -f "$builder"
test -f "$portable_builder"
test -f "$reuse_smoke"
git -C "$repo_root" ls-files --error-unmatch tests/InstallerReuse.Smoke.ps1 >/dev/null

rg -q --fixed-strings 'PrivilegesRequired=admin' "$script"
rg -q --fixed-strings 'PrivilegesRequiredOverridesAllowed=dialog' "$script"
rg -q --fixed-strings 'UsePreviousPrivileges=no' "$script"
rg -q --fixed-strings 'UsePreviousAppDir=no' "$script"
rg -q --fixed-strings 'not WizardSilent' "$script"
rg -q --fixed-strings 'DefaultDirName={autopf}\WinWispr' "$script"
rg -q --fixed-strings '{localappdata}\Programs\WinWispr' "$script"
rg -q --fixed-strings "ExpandConstant('{src}')" "$script"
rg -q --fixed-strings 'AppMutex=Local\WinWispr' "$script"
rg -q --fixed-strings 'CreateUninstallRegKey=yes' "$script"
rg -q --fixed-strings 'UninstallDisplayName=WinWispr' "$script"
rg -q --fixed-strings '[Icons]' "$script"
rg -q --fixed-strings 'Name: "{autoprograms}\WinWispr"' "$script"
rg -q --fixed-strings 'Filename: "{app}\WinWispr.exe"' "$script"
rg -q --fixed-strings 'IconFilename: "{app}\Assets\WinWisprIcon.ico"' "$script"
rg -q --fixed-strings 'UninstallDisplayIcon={app}\Assets\WinWisprIcon.ico' "$script"
rg -q --fixed-strings '[UninstallDelete]' "$script"
rg -q --fixed-strings 'Type: filesandordirs; Name: "{localappdata}\WinWispr"; Check: ShouldDeleteAppData' "$script"
rg -q --fixed-strings 'function InitializeUninstall: Boolean' "$script"
rg -q --fixed-strings 'DeleteAppData' "$script"
rg -q --fixed-strings 'DeleteAppData := False' "$script"
rg -q --fixed-strings 'MB_YESNO or MB_DEFBUTTON2' "$script"
rg -q --fixed-strings 'UninstallSilent' "$script"
rg -q --fixed-strings 'Type: dirifempty; Name: "{app}"' "$script"
rg -q --fixed-strings 'procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);' "$script"
rg -q --fixed-strings 'CurUninstallStep = usPostUninstall' "$script"
rg -q --fixed-strings "DelTree(ExpandConstant('{app}'), True, False, True)" "$script"
rg -q --fixed-strings 'Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs' "$script"
# Setup and Portable carry no models: the WinWispr Models installer adds them (tests/model-package-contract.sh).
# The model-free Update is retired, because Setup itself is now model-free.
for retired in 'WithoutModel' 'WinWispr-Update-v' 'ModelData\model.tar.bz2'; do
    if rg -n --fixed-strings "$retired" "$script" "$builder"; then
        echo "Setup must not reference the retired model payload or Update: $retired" >&2
        exit 1
    fi
done
for retired in 'WithoutModel' "'WinWispr-update-v'" 'Copy-TranslationModel' 'Copy-TtsVoices' 'model.tar.bz2'; do
    if rg -n --fixed-strings "$retired" "$portable_builder"; then
        echo "Portable must not bundle models or build the retired Update: $retired" >&2
        exit 1
    fi
done
rg -q --fixed-strings "\$_ -match '(?i)^(ModelData|TranslationData|VoiceData)/'" "$portable_builder"
# Setup's uninstaller first removes the WinWispr Models, so their uninstall entry does not outlive the folder.
rg -q --fixed-strings "ModelsUninstaller := ExpandConstant('{app}\ModelsUninstall\unins000.exe');" "$script"
rg -q --fixed-strings '(CurUninstallStep = usUninstall) and FileExists(ModelsUninstaller)' "$script"
# Setup, its uninstaller, WinWispr.exe and WinWispr.dll are signed when the build asks for it.
rg -q --fixed-strings '#ifdef Sign' "$script"
rg -q --fixed-strings 'SignTool=winwispr' "$script"
rg -q --fixed-strings 'SignedUninstaller=yes' "$script"
rg -q --fixed-strings '[switch]$Sign' "$builder"
rg -q --fixed-strings "@('/DSign', (Get-InnoSignToolArgument -Pin \$signingPin))" "$builder"
rg -q --fixed-strings 'Assert-CodeSignature -Path $setupFiles[0].FullName -Pin $signingPin' "$builder"
rg -q --fixed-strings '[switch]$Sign' "$portable_builder"
rg -q --fixed-strings "(Join-Path \$publishDirectory 'WinWispr.exe')," "$portable_builder"
rg -q --fixed-strings "(Join-Path \$publishDirectory 'WinWispr.dll'))" "$portable_builder"
rg -q --fixed-strings 'Add-Type -AssemblyName System.IO.Compression' "$portable_builder"
rg -q --fixed-strings '[IO.FileAccess]::Write, [IO.FileShare]::None, 1MB)' "$portable_builder"
rg -q --fixed-strings '[IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB)' "$builder"
rg -q --fixed-strings '[IO.Compression.ZipFileExtensions]::ExtractToDirectory($zip, $stagingDirectory)' "$builder"
rg -q --fixed-strings 'THIRD-PARTY-NOTICES.md' "$portable_builder"
rg -q --fixed-strings "[ValidatePattern('^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$')]" "$portable_builder"
rg -q --fixed-strings "[string]\$Version = '1.0.0'" "$portable_builder"
rg -q --fixed-strings '[string]$ArtifactDirectory' "$portable_builder"
if rg -n --fixed-strings "[string]\$ArtifactDirectory = (Join-Path \$PSScriptRoot" "$portable_builder"; then
    echo 'Build-Portable must resolve its default artifact path after $PSScriptRoot is initialized.' >&2
    exit 1
fi
rg -q --fixed-strings '[IO.Path]::GetFullPath($ArtifactDirectory)' "$portable_builder"
rg -q --fixed-strings '$archivePrefix + $Version + '\''.zip'\''' "$portable_builder"
rg -q --fixed-strings '"-p:Version=$Version"' "$portable_builder"
rg -q --fixed-strings '"-p:AssemblyVersion=$Version"' "$portable_builder"
rg -q --fixed-strings '"-p:FileVersion=$Version"' "$portable_builder"
rg -q --fixed-strings '"-p:InformationalVersion=$Version"' "$portable_builder"
rg -q --fixed-strings 'Get-ArtifactSha256' "$portable_builder"
rg -q --fixed-strings "'WinWispr-' + \$artifactKind + '-tmp-'" "$portable_builder"
rg -q --fixed-strings "'^WinWispr-portable-v[0-9]+\\.[0-9]+\\.[0-9]+\\.zip$'" "$portable_builder"
rg -q --fixed-strings "\$archivePattern.Replace('.zip$', '.zip(?:\\.sha256)?$')" "$portable_builder"
rg -q --fixed-strings 'Remove-Item -LiteralPath $staleArtifacts.FullName -Force' "$portable_builder"
rg -q --fixed-strings '[IO.File]::Move($temporaryArchivePath, $archivePath)' "$portable_builder"
rg -q --fixed-strings '[IO.Path]::GetTempPath()' "$portable_builder"
rg -q --fixed-strings '[IO.Path]::GetTempPath()' "$builder"
rg -q --fixed-strings '[IO.File]::Move($temporaryHashPath, $hashPath)' "$portable_builder"
rg -q --fixed-strings "'WinWispr-' + \$artifactKind + '.lock'" "$portable_builder"
rg -q --fixed-strings '[IO.File]::Open($promotionLockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)' "$portable_builder"
rg -q --fixed-strings '$promotionLock = $null' "$portable_builder"
rg -q --fixed-strings '$promotionLock.Dispose()' "$portable_builder"
rg -q --fixed-strings 'Remove-Item -LiteralPath $promotionLockPath -Force -ErrorAction SilentlyContinue' "$portable_builder"
rg -q --fixed-strings "'WinWispr-' + \$artifactKind + '-backup-'" "$portable_builder"
rg -q --fixed-strings '[IO.File]::Move($backupArchivePath, $existingArchive.FullName)' "$portable_builder"
rg -q --fixed-strings '[IO.File]::Move($backupHashPath, $existingHashPath)' "$portable_builder"
rg -q --fixed-strings 'Remove-Item -LiteralPath $archivePath, $hashPath -Force -ErrorAction SilentlyContinue' "$portable_builder"
rg -q --fixed-strings 'finally {' "$portable_builder"
rg -q --fixed-strings 'Remove-Item -LiteralPath $publishDirectory, $temporaryArchivePath, $temporaryHashPath -Recurse -Force -ErrorAction SilentlyContinue' "$portable_builder"

if rg -n '^\[(Run|UninstallRun)\]|(^|[;[:space:]])(download|external|Permissions:)' "$script"; then
    echo 'installer script contains a forbidden execution, download, external-file, or ACL entry' >&2
    exit 1
fi

rg -q --fixed-strings 'Build-Portable.ps1' "$builder"
rg -q --fixed-strings 'ISCC.exe' "$builder"
rg -q --fixed-strings 'WinWispr-Setup-v' "$builder"
rg -q --fixed-strings "'WinWispr-portable-v'" "$builder"
rg -q --fixed-strings 'SHA256' "$builder"
rg -q --fixed-strings 'does not download' "$builder"
rg -q --fixed-strings "[ValidatePattern('^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$')]" "$builder"
rg -q --fixed-strings "[string]\$Version = '1.0.0'" "$builder"
rg -q --fixed-strings '[string]$PortableArchivePath' "$builder"
rg -q --fixed-strings -- '-Version $Version' "$builder"
rg -q --fixed-strings '"/DAppVersion=$Version"' "$builder"
rg -q --fixed-strings '$installerPrefix + $Version + '\''.exe'\''' "$builder"
rg -q --fixed-strings '$portablePrefix + $Version + '\''.zip'\''' "$builder"
rg -q --fixed-strings '[IO.Path]::GetFileName($resolvedArchive)' "$builder"
rg -q --fixed-strings 'Portable archive name must be exactly' "$builder"
rg -q --fixed-strings '.sha256' "$builder"
rg -q --fixed-strings 'Get-Content -LiteralPath $hashSidecar -Raw' "$builder"
rg -q --fixed-strings 'Get-ArtifactSha256 -LiteralPath $resolvedArchive' "$builder"
rg -q --fixed-strings 'Portable archive SHA-256 mismatch' "$builder"
rg -q --fixed-strings '$portableArchive = if (' "$builder"
rg -q --fixed-strings 'Resolve-PortableArchive -ArchivePath $PortableArchivePath -Version $Version' "$builder"
rg -q --fixed-strings 'Invoke-PortableBuild -ScriptPath $portableScript -Version $Version' "$builder"
rg -q --fixed-strings 'Test-ModelFreePortable -ArchivePath $portableArchive' "$reuse_smoke"
rg -q --fixed-strings "[string]\$Version = '2.3.4'" "$reuse_smoke"
rg -q --fixed-strings 'Build-SherpaRuntime.ps1' "$reuse_smoke"
rg -q --fixed-strings 'Build-Portable.ps1' "$reuse_smoke"
rg -q --fixed-strings 'Build-Installer.ps1' "$reuse_smoke"
rg -q --fixed-strings "'-PortableArchivePath', \$portableArchive" "$reuse_smoke"
rg -q --fixed-strings '$portableBefore' "$reuse_smoke"
rg -q --fixed-strings '$portableAfter' "$reuse_smoke"
rg -q --fixed-strings 'if ($portableBefore -cne $portableAfter)' "$reuse_smoke"
rg -q --fixed-strings 'Get-ArtifactSha256 -LiteralPath $portableArchive' "$reuse_smoke"
rg -q --fixed-strings 'Get-ArtifactSha256 -LiteralPath $installerPath' "$reuse_smoke"
rg -q --fixed-strings 'finally {' "$reuse_smoke"
rg -q --fixed-strings 'Remove-Item -LiteralPath $portableArchive, $portableSidecar, $installerPath, $installerSidecar -Force -ErrorAction SilentlyContinue' "$reuse_smoke"
rg -q --fixed-strings '#define AppVersion "1.0.0"' "$script"
rg -q --fixed-strings 'AppVersion={#AppVersion}' "$script"
rg -q --fixed-strings 'VersionInfoVersion={#AppVersion}' "$script"
rg -q --fixed-strings 'OutputBaseFilename=WinWispr-Setup-v{#AppVersion}' "$script"

# The translation model ships in the WinWispr Models installer as loose files in TranslationData, copied from the
# local, verified input only after every pinned size and SHA-256 matches. Setup never contains it, so an update
# leaves an installed copy alone. App releases upload only Setup, Portable and their sidecars.
translation_copier="$repo_root/eng/Copy-TranslationModel.ps1"
test -f "$translation_copier"
rg -q --fixed-strings "(Get-ArtifactSha256 -LiteralPath \$path) -cne [string]\$file.sha256" "$translation_copier"
rg -q --fixed-strings "(Get-ArtifactSha256 -LiteralPath \$output) -cne [string]\$file.sha256" "$translation_copier"
rg -q --fixed-strings 'This file never downloads' "$translation_copier"
if rg -n -i 'Invoke-WebRequest|Invoke-RestMethod|WebClient|HttpClient|huggingface' "$translation_copier"; then
    echo 'The translation model copier must use local inputs only.' >&2
    exit 1
fi
release_builder="$repo_root/eng/release-local.sh"
publisher="$repo_root/eng/publish-release.sh"
for release_file in "$publisher" "$release_builder"; do
    if rg -n -i 'opus-mt|translation-model|TranslationModels|TranslationData' "$release_file"; then
        echo "Release scripts must not publish the translation model as a separate asset: $release_file" >&2
        exit 1
    fi
done
# Uninstall keeps app data unless the user confirms the deletion.
rg -q --fixed-strings 'Also delete settings, models, and technical logs?' "$script"
