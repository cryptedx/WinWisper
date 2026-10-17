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
readme="$repo_root/README.md"
security="$repo_root/SECURITY.md"
enterprise="$repo_root/docs/ENTERPRISE-SECURITY.md"
changelog="$repo_root/CHANGELOG.md"
acceptance="$repo_root/docs/testing/windows-11-acceptance.md"
notices="$repo_root/THIRD-PARTY-NOTICES.md"

test -f "$readme"
test -f "$security"
test -f "$enterprise"
test -f "$changelog"
test -f "$acceptance"
test -f "$notices"

for doc in "$security" "$enterprise" "$changelog"; do
    rg -q --fixed-strings './eng/install-git-hooks.sh' "$doc"
    rg -q --fixed-strings './eng/release-local.sh --dry-run' "$doc"
    rg -q --fixed-strings './eng/release-local.sh --self-signed' "$doc"
    rg -q --fixed-strings 'eng/Release.ps1 is not a supported release entry point' "$doc"
    rg -q --fixed-strings 'hooks validate' "$doc"
    rg -q --fixed-strings 'remind' "$doc"
    rg -q --fixed-strings 'they never build, tag, push, publish' "$doc"
    rg -q --fixed-strings 'automatically' "$doc"
    rg -q --fixed-strings 'developer consciously starts' "$doc"
    rg -q --fixed-strings 'complete commit range' "$doc"
    rg -q --fixed-strings 'revalidated fail-closed' "$doc"
    rg -q --fixed-strings 'VM acceptance passes' "$doc"
    rg -q --fixed-strings 'local annotated tag is created last' "$doc"
    rg -q --fixed-strings 'SSH host-key trust' "$doc"
    rg -q --fixed-strings 'known_hosts' "$doc"
    rg -q --fixed-strings 'windows-vm' "$doc"
    rg -q --fixed-strings 'SOURCE_ARCHIVE_SHA256' "$doc"
    rg -q --fixed-strings 'RELEASE_REQUEST_SHA256' "$doc"
done

rg -q --fixed-strings '## Reporting a vulnerability' "$security"
rg -q --fixed-strings 'does not contain a security-reporting email address' "$security"
rg -q --fixed-strings 'enterpriseReady=false' "$security"
rg -q --fixed-strings 'RFC 3161 timestamp' "$security"
rg -q --fixed-strings 'current dependency-vulnerability assessment' "$security"
rg -q --fixed-strings 'High- or' "$security"
rg -q --fixed-strings 'Critical-severity finding' "$security"
rg -q --fixed-strings 'product-license decision' "$security"
rg -q --fixed-strings 'protected Authenticode' "$security"
rg -q --fixed-strings 'private key' "$security"
rg -q --fixed-strings 'independent vulnerability and malware scan' "$security"
rg -q --fixed-strings 'SBOM and license review including NOASSERTION' "$security"
rg -q --fixed-strings 'native runtime' "$security"
rg -q --fixed-strings 'endpoint EDR, WDAC, SmartScreen' "$security"
rg -q --fixed-strings 'IT pilot approval' "$security"
rg -q --fixed-strings -- '--self-signed is not a distribution approval' "$security"
rg -q --fixed-strings 'Code-verified' "$security"
rg -q --fixed-strings 'VM-verified' "$security"
rg -q --fixed-strings 'Signature-verified' "$security"
rg -q --fixed-strings 'Not run' "$security"

# The security policy states the network boundary as it is: installation, speech recognition and translation need
# no network access, because both models are bundled.
rg -q --fixed-strings 'installation, speech recognition and translation require no application network access' "$security"
rg -q --fixed-strings 'cloud transcription or translation service' "$security"
rg -q --fixed-strings 'compatibility path for non-bundled development builds' "$security"
# An explicit check: a negated command does not stop a script that runs under set -e.
if rg -q --fixed-strings 'installation and normal runtime' "$security"; then
    echo 'SECURITY.md still says that installation and normal runtime need no network access.' >&2
    exit 1
fi

rg -q --fixed-strings '## Data inventory and retention' "$enterprise"
rg -q --fixed-strings '%LOCALAPPDATA%\WinWispr\settings.json' "$enterprise"
rg -q --fixed-strings 'sample array for recognition' "$enterprise"
rg -q --fixed-strings 'No WinWispr transcript/history store' "$enterprise"
rg -q --fixed-strings '1 MiB' "$enterprise"
rg -q --fixed-strings '7 days' "$enterprise"
rg -q --fixed-strings 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run' "$enterprise"
rg -q --fixed-strings '## Network boundary' "$enterprise"
rg -q --fixed-strings 'compatibility path' "$enterprise"
rg -q --fixed-strings 'non-bundled development builds' "$enterprise"
rg -q --fixed-strings 'application HTTP dependency' "$enterprise"
rg -q --fixed-strings 'HTTPS only' "$enterprise"
rg -q --fixed-strings '## Supply chain and artifact verification' "$enterprise"

# The update check is documented as the one application network access: hosts, consent, verification, opt-out,
# machine policy and the self-signed limit. Hosts are named without URLs.
for doc in "$security" "$enterprise" "$readme" "$changelog"; do
    rg -q --fixed-strings 'DisableUpdateCheck' "$doc"
done
rg -q --fixed-strings 'one network feature on by default: the update check' "$security"
rg -q --fixed-strings 'It sends no telemetry' "$security"
rg -q --fixed-strings 'docs/adr/0003-in-app-update-check.md' "$security"
rg -q --fixed-strings '### Update check' "$enterprise"
rg -q --fixed-strings 'api.github.com' "$enterprise"
rg -q --fixed-strings 'release-assets.githubusercontent.com' "$enterprise"
rg -q --fixed-strings 'HKLM\Software\Policies\WinWispr' "$enterprise"
rg -q --fixed-strings '(DWORD)' "$enterprise"
rg -q --fixed-strings 'AutomaticUpdateChecks' "$enterprise"
rg -q --fixed-strings 'nothing is' "$enterprise"
rg -q --fixed-strings 'F8D419ED06D2038754C6BB2E5042BBF6DBE5903C' "$enterprise"
rg -q --fixed-strings 'trusts the holder of WinWispr' "$enterprise"
rg -q --fixed-strings '## Updating' "$readme"
rg -q --fixed-strings 'Nothing is downloaded before you click it' "$readme"
rg -q --fixed-strings '### Added' "$changelog"
# Nothing may claim that the application has no updater any more. Explicit check: a negated command does not stop a
# script that runs under set -e.
if rg -n -i 'no (application )?updater|install an updater|no remote release service, automatic update channel' "$security" "$enterprise" "$readme"; then
    echo 'Documentation still says that WinWispr has no update check.' >&2
    exit 1
fi

# The README states the network boundary as it is: both models are bundled and nothing is downloaded.
rg -q --fixed-strings 'WinWispr downloads nothing' "$readme"
rg -q --fixed-strings 'it needs no network access' "$readme"
rg -q --fixed-strings 'TranslationData' "$readme"

# The bundled translation model: where it lives, its provenance and its controls.
rg -q --fixed-strings '### Bundled translation model' "$enterprise"
rg -q --fixed-strings '`TranslationData\` in the installation folder' "$enterprise"
rg -q --fixed-strings '%LOCALAPPDATA%\WinWispr\TranslationModels\' "$enterprise"
rg -q --fixed-strings 'Xenova/opus-mt-de-en' "$enterprise"
rg -q --fixed-strings 'Xenova/opus-mt-en-de' "$enterprise"
rg -q --fixed-strings 'pinned size and SHA-256' "$enterprise"
rg -q --fixed-strings 'no network destination' "$enterprise"
rg -q --fixed-strings 'src/WinWispr/Speech/ModelManifest.cs' "$enterprise"
rg -q --fixed-strings 'eng/translation-model-pins.json' "$enterprise"
rg -q --fixed-strings 'Opus-MT' "$enterprise"
rg -q --fixed-strings 'CC-BY-4.0' "$enterprise"
# Nothing may describe a translation download any more. Explicit checks: a negated command does not stop a script
# that runs under set -e.
for document in "$security" "$enterprise" "$readme"; do
    if rg -q -i 'optional translation model|translation model download|Download translation|hf\.co' "$document"; then
        echo "$document still describes a translation model download; the model is bundled." >&2
        exit 1
    fi
done
# Nothing of the abandoned mirrored archive design may come back.
for document in "$security" "$enterprise" "$notices"; do
    if rg -q -i 'models-translation-v1|build-translation-archive|translation-model-archive|opus-mt-int8-v1\.tar' "$document"; then
        echo "$document still describes a mirrored translation archive." >&2
        exit 1
    fi
done
# githubusercontent.com may appear only as an update download host, never as a translation download destination.
if rg -i 'githubusercontent\.com' "$enterprise" "$security" | rg -q -i 'translation'; then
    echo 'The security documents still name githubusercontent.com as a translation download destination.' >&2
    exit 1
fi
rg -q --fixed-strings 'NOASSERTION' "$enterprise"
rg -q --fixed-strings 'no root product `LICENSE`' "$enterprise"
rg -q --fixed-strings 'clean `COPYFILE_DISABLE=1 git archive --format=zip HEAD`' "$enterprise"
rg -q --fixed-strings 'source archive' "$enterprise"
rg -q --fixed-strings '.DS_Store' "$enterprise"
rg -q --fixed-strings '._*' "$enterprise"
rg -q --fixed-strings '__MACOSX' "$enterprise"
rg -q --fixed-strings '.AppleDouble' "$enterprise"
rg -q --fixed-strings 'repository mirror `C:\windev\WinWispr\src`' "$enterprise"
rg -q --fixed-strings 'C:\windev\WinWispr\src\artifacts' "$enterprise"
refute rg -q --fixed-strings 'Z:\' "$enterprise"
rg -q --fixed-strings '%USERPROFILE%\WinWispr\test-data' "$enterprise"
rg -q --fixed-strings 'does not delete or replace' "$enterprise"
rg -q --fixed-strings '%LOCALAPPDATA%\WinWispr' "$enterprise"
rg -q --fixed-strings 'models, audio, transcripts' "$enterprise"
rg -q --fixed-strings 'private keys, passwords, and tokens' "$enterprise"
rg -q --fixed-strings 'not release-evidence' "$enterprise"
rg -q --fixed-strings 'contents' "$enterprise"
rg -q --fixed-strings '## Threat model, controls, and residual risks' "$enterprise"
rg -q --fixed-strings '## Verification status and required evidence' "$enterprise"
rg -q --fixed-strings 'matching source commit/provenance hash, portable ZIP' "$enterprise"
rg -q --fixed-strings 'installer, and sidecars' "$enterprise"
rg -q --fixed-strings 'full third-party notices' "$enterprise"
rg -q --fixed-strings 'command logs and interactive-session VM acceptance' "$enterprise"
rg -q --fixed-strings 'vmVerified=true' "$enterprise"

rg -q --fixed-strings '## Unreleased' "$changelog"
rg -q --fixed-strings '## Changelog discipline' "$changelog"
rg -q --fixed-strings 'Conventional Commits' "$changelog"
rg -q --fixed-strings 'annotated local Git tag' "$changelog"
rg -q --fixed-strings 'tag last' "$changelog"

rg -q --fixed-strings '# WinWispr Windows 11 Acceptance (Historical Run)' "$acceptance"
rg -q --fixed-strings 'unsigned portable run dated 2026-08-20' "$acceptance"
rg -q --fixed-strings 'not evidence for a current release' "$acceptance"
rg -q --fixed-strings 'neither signature verification nor enterprise approval' "$acceptance"
rg -q --fixed-strings '## Procedure for a new VM verification' "$acceptance"
rg -q --fixed-strings 'do not demonstrate a current run' "$acceptance"

if rg -n -i 'security@|mailto:|api[ _-]?key[[:space:]]*[:=]|password[[:space:]]*[:=]|access[ _-]?token[[:space:]]*[:=]|BEGIN [A-Z ]*PRIVATE KEY' "$security" "$enterprise" "$changelog"; then
    echo 'Security documentation must not contain credentials or an invented contact.' >&2
    exit 1
fi

if rg -n --fixed-strings 'powershell.exe -ExecutionPolicy Bypass -File .\eng\Release.ps1' "$readme" "$security" "$enterprise" "$changelog"; then
    echo 'Documentation must not present eng/Release.ps1 as a runnable release entry point.' >&2
    exit 1
fi

if rg -n -i 'automatically starts the release|release automatically on every commit|auto-tag' "$readme" "$security" "$enterprise" "$changelog"; then
    echo 'Documentation must not claim automatic release or tagging.' >&2
    exit 1
fi

if rg -n -i '100% secure|completely secure|guaranteed secure|no residual risk' "$security" "$enterprise" "$changelog"; then
    echo 'Security documentation must not make absolute security claims.' >&2
    exit 1
fi

if rg -n 'https?://' "$security" "$enterprise" "$changelog"; then
    echo 'Security documentation must refer to approved source files, not add network endpoints.' >&2
    exit 1
fi
