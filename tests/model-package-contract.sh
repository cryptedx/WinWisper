#!/usr/bin/env bash
set -euo pipefail

# The WinWispr Models installer carries the speech model, the translation model and the offline voices that
# Setup and Portable leave out. eng/model-package.json versions it and pins its content; WinWispr's own
# self-signed certificate (eng/signing) signs it like every other release binary.

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
package_json="$repo_root/eng/model-package.json"
package_helper="$repo_root/eng/ModelPackage.ps1"
builder="$repo_root/eng/Build-ModelPackage.ps1"
script="$repo_root/installer/WinWisprModels.iss"
app_script="$repo_root/installer/WinWispr.iss"
manifest="$repo_root/src/WinWispr/Speech/ModelManifest.cs"
signing="$repo_root/eng/CodeSigning.ps1"
pin="$repo_root/eng/signing/code-signing.json"
certificate="$repo_root/eng/signing/WinWispr-CodeSigning.cer"
publisher="$repo_root/eng/publish-models.sh"

fail() {
    printf 'model-package contract: %s\n' "$1" >&2
    exit 1
}

for file in "$package_json" "$package_helper" "$builder" "$script" "$signing" "$pin" "$certificate" "$publisher"; do
    test -f "$file" || fail "missing $file"
done

# The digest names exactly the pinned content; a pin change without a new version fails here.
json() { plutil -extract "$1" raw -o - "$package_json"; }
version="$(json version)"
[[ "$version" =~ ^[1-9][0-9]*$ ]] || fail "invalid package version: $version"
speech_sha256="$(json speechArchive.sha256)"
speech_bytes="$(json speechArchive.bytes)"
digest="$(
    {
        printf 'speech %s %s\n' "$speech_sha256" "$speech_bytes"
        printf 'translation-pins %s\n' "$(shasum -a 256 "$repo_root/eng/translation-model-pins.json" | awk '{print $1}')"
        for voice in $(cd "$repo_root/eng/voices" && ls vits-piper-*.json | LC_ALL=C sort); do
            printf 'voice %s %s\n' "${voice%.json}" "$(shasum -a 256 "$repo_root/eng/voices/$voice" | awk '{print $1}')"
        done
    } | shasum -a 256 | awk '{print $1}'
)"
[[ "$digest" == "$(json contentSha256)" ]] ||
    fail "the model pins changed (content $digest); raise version in eng/model-package.json and record the new contentSha256"

# The app downloads the same speech model archive when none is installed.
grep -Fq "ArchiveBytes: ${speech_bytes:0:3}_${speech_bytes:3:3}_${speech_bytes:6:3}," "$manifest" ||
    fail 'eng/model-package.json and ModelManifest.Parakeet disagree on the archive size'
grep -Fq "ArchiveSha256: \"$speech_sha256\"" "$manifest" ||
    fail 'eng/model-package.json and ModelManifest.Parakeet disagree on the archive SHA-256'

# The installer adds the models to an existing WinWispr folder, never to a folder without WinWispr.exe.
app_id="$(sed -n 's/^AppId={{\([0-9A-F-]*\)}$/\1/p' "$app_script")"
[[ -n "$app_id" ]] || fail 'WinWispr.iss has no AppId'
grep -Fq "Uninstall\\{$app_id}_is1" "$script" || fail 'the models installer must find WinWispr through its uninstall entry'
grep -Fq "'InstallLocation'" "$script" || fail 'the models installer must read InstallLocation'
grep -Fq 'DefaultDirName={code:WinWisprDirectory}' "$script" || fail 'the models installer must default to the WinWispr folder'
grep -Fq "FileExists(AddBackslash(Directory) + 'WinWispr.exe')" "$script" || fail 'the models installer must require WinWispr.exe'
grep -Fq 'function PrepareToInstall' "$script" || fail 'a silent install must also refuse a folder without WinWispr.exe'
grep -Fq 'AppName=WinWispr Models' "$script" || fail 'AppName must be WinWispr Models'
grep -Fq 'OutputBaseFilename=WinWispr-Models-v{#PackageVersion}' "$script" || fail 'unexpected output name'
grep -Fq 'UninstallFilesDir={app}\ModelsUninstall' "$script" || fail 'the models uninstaller lives in ModelsUninstall'
grep -Fq 'AppMutex=Local\WinWispr' "$script" || fail 'the models installer must close a running WinWispr'
grep -Fq 'Source: "{#SourceDir}\ModelData\model.tar.bz2"; DestDir: "{app}\ModelData"; Flags: ignoreversion nocompression' "$script" ||
    fail 'the speech model archive is already compressed'
grep -Fq 'DestDir: "{app}\TranslationData"' "$script" || fail 'the translation model goes to TranslationData'
grep -Fq 'DestDir: "{app}\VoiceData"' "$script" || fail 'the voices go to VoiceData'
grep -Fq 'SignTool=winwispr' "$script" && grep -Fq 'SignedUninstaller=yes' "$script" || fail 'the models installer must be signable'
[[ "$(sed -n 's/^AppId={{\([0-9A-F-]*\)}$/\1/p' "$script")" != "$app_id" ]] || fail 'the models installer needs its own AppId'
if rg -n '^\[(Run|UninstallRun)\]|(^|[;[:space:]])(download|external|Permissions:)' "$script"; then
    fail 'the models installer contains a forbidden execution, download, external-file, or ACL entry'
fi

# The builder verifies every input against its pin and never downloads.
grep -Fq 'Read-ModelPackage -EngRoot $PSScriptRoot' "$builder" || fail 'the builder must check the package digest'
grep -Fq 'Assert-SpeechModelArchive -RepositoryRoot $repositoryRoot -Package $package' "$builder" || fail 'the builder must verify the speech archive'
grep -Fq 'Copy-TranslationModel -PinsPath' "$builder" || fail 'the builder must verify the translation model'
grep -Fq 'Copy-TtsVoices -ManifestRoot' "$builder" || fail 'the builder must verify the voices'
grep -Fq 'Assert-CodeSignature -Path $packagePath -Pin $signingPin' "$builder" || fail 'the builder must verify its signature'
if rg -n -i 'Invoke-WebRequest|Invoke-RestMethod|WebClient|HttpClient|huggingface' "$builder" "$package_helper"; then
    fail 'the model package builder must use local inputs only'
fi

# The pinned certificate is WinWispr's own: subject, thumbprint and the committed public certificate agree.
[[ "$(plutil -extract subject raw -o - "$pin")" == 'CN=WinWispr' ]] || fail 'the signing subject must be CN=WinWispr'
thumbprint="$(plutil -extract thumbprint raw -o - "$pin")"
[[ "$thumbprint" =~ ^[0-9A-F]{40}$ ]] || fail "invalid thumbprint: $thumbprint"
[[ "$(shasum -a 1 "$certificate" | awk '{print toupper($1)}')" == "$thumbprint" ]] ||
    fail 'WinWispr-CodeSigning.cer does not match the pinned thumbprint'
if command -v openssl >/dev/null 2>&1; then
    openssl x509 -inform der -in "$certificate" -noout -subject | grep -Fq 'CN=WinWispr' ||
        fail 'WinWispr-CodeSigning.cer is not the CN=WinWispr certificate'
    if openssl pkey -inform der -in "$certificate" -noout 2>/dev/null; then
        fail 'eng/signing must hold the public certificate only'
    fi
fi
if git -C "$repo_root" ls-files -- 'eng/signing/*' | rg -n -i '\.(pfx|p12|key|pem)$'; then
    fail 'a private key must never be committed'
fi
grep -Fq "/sha1 \$(\$Pin.thumbprint) /tr \$(\$Pin.timestampUrl) /td SHA256" "$signing" ||
    fail 'Inno signing must use the pinned certificate and an RFC 3161 timestamp'
grep -Fq 'SignatureStatus]::Valid' "$signing" || fail 'every signature must verify as Valid'
grep -Fq 'KeyExportPolicy NonExportable' "$repo_root/eng/New-CodeSigningCertificate.ps1" || fail 'the signing key must not be exportable'
grep -Fq "Cert:\\LocalMachine\\\$store" "$repo_root/eng/Trust-CodeSigningCertificate.ps1" || fail 'trust goes to the machine stores'

# The model release is separate from app releases and never becomes the latest release.
grep -Fq -- '--latest=false' "$publisher" || fail 'the model release must not become the latest release'
grep -Fq 'ASSETS=2' "$publisher" || fail 'the model release has two assets'
grep -Fq 'eng/model-package.json pins model package version' "$publisher" || fail 'the publisher must check the pinned version'
# Forgejo creates the draft for a pushed models-vN tag; the publisher only uploads and publishes.
models_workflow="$repo_root/.forgejo/workflows/publish-models.yml"
if grep -Fq 'gh release create' "$publisher"; then
    fail 'the publisher must not create releases; Forgejo creates the draft'
fi
grep -Fq 'tags: ["models-v*"]' "$models_workflow" || fail 'a pushed models-vN tag must start the Forgejo workflow'
for setting in 'release-only: "true"' 'draft: "true"' 'latest: "false"'; do
    grep -Fq "$setting" "$models_workflow" || fail "the Forgejo model workflow must set $setting"
done

printf 'model-package contract: PASS\n'
