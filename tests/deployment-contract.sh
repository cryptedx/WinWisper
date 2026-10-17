#!/usr/bin/env bash
set -euo pipefail

# `set -e` ignores pipelines negated with `!`, so negative guards go through here.
refute() {
    if "$@"; then
        printf 'unexpected match: %s\n' "$*" >&2
        exit 1
    fi
}

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
runner="$repo_root/eng/deploy-windows-vm.sh"
worker="$repo_root/eng/Deploy-WindowsVm.ps1"
development_version="$repo_root/eng/DevelopmentVersion.ps1"
portable_builder="$repo_root/eng/Build-Portable.ps1"
cleanup="$repo_root/eng/Cleanup-WindowsVm.ps1"
prune="$repo_root/eng/Prune-VmArtifacts.ps1"

test -x "$runner"
test -f "$worker"
test -f "$development_version"
test -f "$portable_builder"
test -f "$cleanup"
test -f "$prune"
bash -n "$runner"

grep -Fq -- "remote_host='windows-vm'" "$runner"
refute grep -Fq -- 'WINWISPR_VM_HOST' "$runner"
grep -Fq -- "remote_mirror='C:/windev/WinWispr/src'" "$runner"
grep -Fq -- 'COPYFILE_DISABLE=1 git archive --format=zip HEAD' "$runner"
grep -Fq -- 'Register-ScheduledTask' "$runner"
grep -Fq -- 'New-ScheduledTaskPrincipal' "$runner"
grep -Fq -- '-LogonType Interactive' "$runner"
grep -Fq -- '-EncodedCommand' "$runner"
grep -Fq -- '--delete-excluded' "$runner"
grep -Fq -- "'P /artifacts/***' 'H /artifacts/'" "$runner"
grep -Fq -- 'artifacts/.vm-lock' "$repo_root/eng/vm-lock.sh"
grep -Fq -- 'eng/vm-lock.sh' "$runner"
grep -Fq -- 'artifacts/.vm-control/release/' "$runner"
refute grep -Fq -- 'Z:' "$runner"
refute grep -Fq -- 'net.exe use' "$runner"
refute grep -Fq -- 'HKCU:\\Network' "$runner"
grep -Fq -- '--cleanup-legacy' "$runner"
# The model-free Update is retired; --model-package builds the WinWispr Models installer and copies it back.
refute grep -Fq -- '--model-free-update' "$runner"
grep -Fq -- '--model-package' "$runner"
grep -Fq -- "\${rsync_mirror}artifacts/models/\$package_name" "$runner"
grep -Fq -- 'model_package_json="$repo_root/eng/model-package.json"' "$runner"
grep -Fq -- 'artifacts/.vm-control' "$runner"
grep -Fq -- 'local_model=' "$runner"
grep -Fq -- 'Bundled model SHA-256 mismatch' "$runner"
grep -Fq -- 'RELEASE_REQUEST_SHA256=' "$runner"
grep -Fq -- 'SourceArchiveSha256' "$runner"
refute grep -Fq -- 'REMOTE_REPOSITORY_HEAD=' "$runner"
refute grep -Fq -- 'PREFLIGHT=PASS' "$runner"
refute grep -Fq -- 'scp' "$runner"
refute grep -Fq -- 'Compress-Archive' "$runner"
refute grep -Fq -- 'WinWispr-artifacts.zip' "$runner"
refute grep -Fq -- 'cp "$archive"' "$runner"
refute grep -Fq -- '-SourceArchivePath' "$runner"
refute grep -Fq -- 'rm -rf "$repo_root/artifacts"' "$runner"
refute grep -Fq -- 'mv "$run_dir/artifacts" "$repo_root/artifacts"' "$runner"
grep -Fq -- 'unzip -Z1' "$runner"
grep -Fq -- 'WINWISPR_MIN_MAC_FREE_GIB:-15' "$runner"
grep -Fq -- 'remote_prune="$remote_mirror/eng/Prune-VmArtifacts.ps1"' "$runner"
grep -Fq -- '"$repo_root/eng/prune-artifacts.sh"' "$repo_root/eng/release-local.sh"

grep -Fq -- '[string]$RepositoryPath' "$worker"
grep -Fq -- '[string]$SourceArchiveSha256' "$worker"
refute grep -Fq -- 'ModelFreeUpdateVersion' "$worker"
grep -Fq -- '[switch]$ModelPackage' "$worker"
# Every build prunes old output and checks the free space before it starts.
grep -Fq -- "& (Join-Path \$PSScriptRoot 'Prune-VmArtifacts.ps1') -ArtifactsPath \$artifacts" "$worker"
grep -Fq -- 'function Assert-FreeSpace' "$worker"
[[ "$(grep -n -F -- 'Assert-FreeSpace' "$worker" | sed -n '2p' | cut -d: -f1)" -lt "$(grep -n -F -- '    Assert-BuildPreconditions' "$worker" | tail -n 1 | cut -d: -f1)" ]]
# The prune never touches the build caches, the model, or the control files.
# `! grep` never fails under set -e, so the guard exits explicitly.
if grep -Eq -- "Join-Path \\\$artifacts '(runtime-cache|model|voices|\.vm-control)'" "$prune"; then
    echo 'Prune-VmArtifacts.ps1 must not address the build caches, the model, or the control files.' >&2
    exit 1
fi
grep -Fq -- "@('bin', 'obj', 'logs')" "$prune"
grep -Fq -- "DevelopmentVersion.ps1" "$worker"
grep -Fq -- 'function Get-DevelopmentVersion' "$development_version"
grep -Fq -- 'Get-NextVersion -CurrentVersion' "$development_version"
grep -Fq -- 'Get-HighestReachableReleaseTag' "$development_version"
grep -Fq -- 'for-each-ref' "$development_version"
grep -Fq -- 'merge-base' "$development_version"
grep -Fq -- "Write-Output \"DEPLOYMENT_VERSION=\$releaseVersion\"" "$worker"
refute grep -Fq -- "else { '1.0.0' }" "$worker"
refute grep -Fq -- 'UPDATE_INSTALLER=' "$worker"
refute grep -Fq -- "'-WithoutModel'" "$worker"
# Deployments and releases sign Portable's binaries and Setup, and verify every signature after installing.
grep -Fq -- "'-Sign'," "$worker"
grep -Fq -- "'-NoBuild',
            '-Sign'," "$worker"
grep -Fq -- 'Get-CodeSigningCertificate -Pin $signingPin | Out-Null' "$worker"
grep -Fq -- 'Assert-CodeSignature -Path $signed -Pin $signingPin' "$worker"
grep -Fq -- "(Join-Path \$install 'ModelsUninstall\\unins000.exe')" "$worker"
refute grep -Fq -- '[string]$SourceArchivePath' "$worker"
refute grep -Fq -- 'Assert-NoMacMetadata -Names (Get-ZipNames -Path $sourceArchivePathFull)' "$worker"
grep -Fq -- "'safe.directory=*'" "$worker"
grep -Fq -- 'Get-RepositoryHead' "$worker"
grep -Fq -- 'Assert-ControlFile' "$worker"
grep -Fq -- '.vm-control' "$worker"
grep -Fq -- "'-PortableArchivePath'" "$worker"
grep -Fq -- 'LogonType Interactive' "$worker"
grep -Fq -- '[Diagnostics.Process]::GetCurrentProcess().SessionId' "$worker"
grep -Fq -- '$processes[0].SessionId -ne $interactiveSessionId' "$worker"
grep -Fq -- '$processes | ForEach-Object { $_.Id }' "$worker"
refute grep -Fq -- '$processes[0].SessionId -ne 1' "$worker"
grep -Fq -- "Write-Output 'APP_RUNNING=1'" "$worker"
grep -Fq -- 'ResolveAssemblyReferencesFindRelatedSatellites=false' "$worker"
grep -Fq -- '$evidenceExitCode = $LASTEXITCODE' "$worker"
grep -Fq -- 'ResolveAssemblyReferencesFindRelatedSatellites=false' "$portable_builder"
grep -Fq -- "'--no-build'" "$portable_builder"
grep -Fq -- "'-NoBuild'" "$worker"
grep -Fq -- '"-p:Version=$releaseVersion"' "$worker"
grep -Fq -- "'--artifacts-path', \$checkArtifacts" "$worker"
grep -Fq -- '[IO.FileShare]::None' "$worker"
grep -Fq -- "Write-Output 'CHECK=PASS'" "$worker"
# Incremental builds only reuse output built from the same synced tree.
grep -Fq -- ". (Join-Path \$PSScriptRoot 'SourceTreeBuild.ps1')" "$worker"
grep -Fq -- "Start-SourceTreeBuild -MarkerPath \$sourceTreeMarker -SourceTreeSha256 \$SourceTreeSha256" "$worker"
grep -Fq -- "if (\$buildMode -eq 'rebuild') { \$buildArguments += '--no-incremental' }" "$worker"
grep -Fq -- "Complete-SourceTreeBuild -MarkerPath \$sourceTreeMarker -SourceTreeSha256 \$SourceTreeSha256" "$worker"
grep -Fq -- 'if (-not $Check) { Stop-RunningWinWispr }' "$worker"
refute grep -Fq -- 'function Restore-ModelFreeApp' "$worker"
# Runs that can include desktop E2E tests refuse a disturbed desktop right before the tests.
grep -Fq -- 'function Assert-UndisturbedDesktop' "$worker"
grep -Fq -- "if (\$title.Length -gt 60)" "$worker"
grep -Fq -- '$_.WaitReason -ne '"'"'Suspended'"'"'' "$worker"
grep -Fq -- 'Assert-UndisturbedDesktop -Strict ($releaseMode -or [bool]$TestFilter)' "$worker"
[[ "$(grep -n -F -- 'Assert-UndisturbedDesktop -Strict' "$worker" | cut -d: -f1)" -lt "$(grep -n -F -- 'Invoke-RequiredCommand -FilePath $runnerPath' "$worker" | cut -d: -f1)" ]]
grep -Fq -- "'^WinWispr-(publish|installer)-'" "$worker"
refute grep -Fq -- "-Filter 'WinWispr-*'" "$worker"
grep -Fq -- "tests\\WinWispr.Tests\\bin\\x64\\Release\\net10.0-windows10.0.26100.0\\win-x64\\WinWispr.Tests.exe" "$worker"
refute grep -Fq -- "Get-ChildItem -LiteralPath \$runnerRoot -Recurse -File -Filter 'WinWispr.Tests.exe'" "$worker"
refute grep -Fq -- 'Expand-Archive' "$worker"
refute grep -Fq -- '$incoming' "$worker"
refute grep -Eq -- "C:\\\\Users\\\\[^\\\\]+\\\\WinWispr'" "$worker"
refute grep -Fq -- 'Remove-SafeTree -Path $sourceArtifacts' "$worker"
# Settings written after the dictionary split have no PersonalDictionary field; strict mode
# throws when the worker reads a missing property, so it must look the field up first.
grep -Fq -- ".PSObject.Properties['PersonalDictionary']" "$worker"
refute grep -Fq -- ').PersonalDictionary)' "$worker"

grep -Fq -- "@('source', 'incoming', 'artifacts')" "$cleanup"
grep -Fq -- 'LEGACY_{0}={1}' "$cleanup"
grep -Fq -- "Join-Path \$root 'test-data'" "$cleanup"
grep -Fq -- 'APP_DATA=PRESERVED' "$cleanup"
grep -Fq -- 'Remove-SafeTree' "$cleanup"
refute grep -Eq -- "Remove-SafeTree .*(test-data|\\\$testDataPath)" "$cleanup"
refute grep -Fq -- 'Remove-Item -LiteralPath $testDataPath' "$cleanup"
refute grep -Fq -- 'Remove-Item -LiteralPath $appDataPath' "$cleanup"
refute grep -Fq -- 'Remove-Item -LiteralPath $installPath' "$cleanup"

# Neither the deployment worker nor the legacy cleanup may delete or replace app data. An explicit check: a
# negated command does not stop a script that runs under set -e.
if grep -Eq -- 'Remove-(Item|SafeTree)[^#]*\$appData' "$worker" "$cleanup"; then
    echo 'The deployment worker or the legacy cleanup removes app data.' >&2
    exit 1
fi

# The models ship in the WinWispr Models installer: the deployment copies their inputs to the VM, the worker
# builds the installer once per package version, installs it after Setup and checks every pinned file.
grep -Fq -- "send_directory_to_mirror \"\$local_translation_model\" 'artifacts/translation-model'" "$runner"
grep -Fq -- 'function Get-ModelPackageInstaller' "$worker"
grep -Fq -- "Write-Output 'MODEL_PACKAGE_BUILD=cache'" "$worker"
grep -Fq -- 'Assert-ModelFreeNames -Names $portableNames' "$worker"
grep -Fq -- 'Install-Installer -SetupPath $modelPackagePath' "$worker"
grep -Fq -- 'Assert-InstalledModels -InstallDirectory $install' "$worker"
grep -Fq -- 'Assert-InstalledTranslationModel -InstallDirectory $InstallDirectory' "$worker"
[[ "$(grep -n -F -- 'Install-Installer -SetupPath $currentSetup' "$worker" | cut -d: -f1)" -lt "$(grep -n -F -- 'Install-Installer -SetupPath $modelPackagePath' "$worker" | cut -d: -f1)" ]]

echo 'deployment contract: PASS'
