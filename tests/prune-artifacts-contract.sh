#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo_root/eng/prune-artifacts.sh"
test -x "$script"
bash -n "$script"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-prune-contract.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT
repo="$tmp_dir/repo"
a="$repo/artifacts"
mkdir -p "$repo/eng"
cp "$script" "$repo/eng/prune-artifacts.sh"

file() {
    mkdir -p "$(dirname -- "$1")"
    printf 'x\n' >"$1"
}
age() {
    # Old enough for every age limit, whatever today is.
    find "$1" -exec touch -t 202001010000 {} +
}

for version in 1.2.0 1.9.0 1.10.0 1.5.3; do
    file "$a/releases/v$version/SHA256SUMS.txt"
    file "$a/installer/WinWispr-Setup-v$version.exe"
    file "$a/installer/WinWispr-Setup-v$version.exe.sha256"
    file "$a/portable/WinWispr-portable-v$version.zip"
    file "$a/portable/WinWispr-portable-v$version.zip.sha256"
done
mkdir -p "$a/releases/.staging-v1.0.0.abc"
file "$a/installer/WinWispr-Update-v1.0.0.exe"
# Only the newest WinWispr Models installer stays; package versions sort numerically.
for version in 2 10; do
    file "$a/models/WinWispr-Models-v$version.exe"
    file "$a/models/WinWispr-Models-v$version.exe.sha256"
done
file "$a/models/WinWispr-Models-v3.exe.sha256"
# An alias with a build of its own stays; one that duplicates a versioned
# package goes, with its sidecar.
printf 'own build\n' >"$a/installer/WinWispr-Setup.exe"
file "$a/installer/WinWispr-Setup-v1.1.0.exe.sha256"
file "$a/portable/WinWispr-portable-current.zip"
file "$a/portable/WinWispr-portable-current.zip.sha256"
file "$a/portable/WinWispr-portable-tmp-old.zip"
age "$a/portable/WinWispr-portable-tmp-old.zip"
file "$a/portable/WinWispr-portable-tmp-new.zip"
file "$a/dev-check/validation/log.txt"
age "$a/dev-check/validation"
file "$a/dev-check/logs/old.txt"
age "$a/dev-check/logs"
file "$a/dev-check/logs/new.txt"
file "$a/dev-check/tts-diagnostics/new.txt"
file "$a/model/model.tar.bz2"
file "$a/voices/voice/model.onnx"
file "$a/translation-model/opus-mt-de-en/source.spm"
file "$a/runtime-cache/win-x64/sherpa.dll"
file "$a/runtime-cache/build-script.sha256"
file "$a/runtime-cache/merge-tests/old.bin"
file "$a/runtime-cache/new-entry/new.bin"
file "$a/teams-build/bin/old.dll"
file "$a/dev-check/bin/WinWispr.dll"
file "$a/dev-check/obj/project.assets.json"
file "$a/.vm-control/release/evidence.zip"
age "$a/model"
age "$a/voices"
age "$a/translation-model"
age "$a/runtime-cache"
touch "$a/runtime-cache/new-entry/new.bin"
age "$a/teams-build"
age "$a/.vm-control"

"$repo/eng/prune-artifacts.sh" --dry-run >"$tmp_dir/dry.out"
grep -Fxq 'PRUNE=PASS' "$tmp_dir/dry.out"
grep -Fxq 'PRUNE_MODE=dry-run' "$tmp_dir/dry.out"
test -d "$a/releases/v1.2.0"
test -d "$a/dev-check/validation"
real_a="$(cd "$a" && pwd)"
grep -Fq "PRUNE_WOULD_REMOVE=$real_a/releases/v1.2.0 " "$tmp_dir/dry.out"
refute_line() { if grep -Fq -- "$1" "$2"; then echo "unexpected line: $1" >&2; exit 1; fi; }
refute_line "releases/v1.5.3" "$tmp_dir/dry.out"
refute_line 'PRUNE_ASSUMED_RELEASE' "$tmp_dir/dry.out"

# A release that is about to be added takes one of the three slots, so the
# preview before a release also lists the release the final prune will drop.
"$repo/eng/prune-artifacts.sh" --dry-run --assume-release 1.11.0 >"$tmp_dir/assume.out"
grep -Fxq 'PRUNE_ASSUMED_RELEASE=v1.11.0' "$tmp_dir/assume.out"
grep -Fq "PRUNE_WOULD_REMOVE=$real_a/releases/v1.2.0 " "$tmp_dir/assume.out"
grep -Fq "PRUNE_WOULD_REMOVE=$real_a/releases/v1.5.3 (0 MB, older than the newest 3 (the release being added counts as one))" "$tmp_dir/assume.out"
refute_line "releases/v1.9.0" "$tmp_dir/assume.out"
refute_line "releases/v1.10.0" "$tmp_dir/assume.out"
test -d "$a/releases/v1.5.3"
# An assumed release that is already present reserves nothing.
"$repo/eng/prune-artifacts.sh" --dry-run --assume-release 1.10.0 >"$tmp_dir/assume-present.out"
refute_line "releases/v1.5.3" "$tmp_dir/assume-present.out"
for bad in 1.2 v1.2.3 01.2.3 ''; do
    if "$repo/eng/prune-artifacts.sh" --dry-run --assume-release "$bad" >/dev/null 2>&1; then
        echo "A malformed --assume-release version must be rejected: $bad" >&2
        exit 1
    fi
done

"$repo/eng/prune-artifacts.sh" >"$tmp_dir/prune.out"
grep -Fxq 'PRUNE=PASS' "$tmp_dir/prune.out"
[[ "$(grep '^PRUNE_ITEMS=' "$tmp_dir/dry.out")" == "$(grep '^PRUNE_ITEMS=' "$tmp_dir/prune.out")" ]] || {
    echo 'The dry run must list exactly what the prune removes.' >&2
    exit 1
}

# Versions sort numerically: 1.10.0 is the newest.
for kept in 1.10.0 1.9.0 1.5.3; do test -d "$a/releases/v$kept"; done
test ! -e "$a/releases/v1.2.0"
test -d "$a/releases/.staging-v1.0.0.abc"
for kept in 1.10.0 1.9.0; do
    test -f "$a/installer/WinWispr-Setup-v$kept.exe"
    test -f "$a/installer/WinWispr-Setup-v$kept.exe.sha256"
    test -f "$a/portable/WinWispr-portable-v$kept.zip"
done
for removed in 1.5.3 1.2.0; do
    test ! -e "$a/installer/WinWispr-Setup-v$removed.exe"
    test ! -e "$a/installer/WinWispr-Setup-v$removed.exe.sha256"
    test ! -e "$a/portable/WinWispr-portable-v$removed.zip.sha256"
done
# Retention counts per kind.
test -f "$a/installer/WinWispr-Update-v1.0.0.exe"
test -f "$a/installer/WinWispr-Setup.exe"
test -f "$a/models/WinWispr-Models-v10.exe"
test -f "$a/models/WinWispr-Models-v10.exe.sha256"
test ! -e "$a/models/WinWispr-Models-v2.exe"
test ! -e "$a/models/WinWispr-Models-v2.exe.sha256"
test ! -e "$a/models/WinWispr-Models-v3.exe.sha256"
test ! -e "$a/portable/WinWispr-portable-current.zip"
test ! -e "$a/portable/WinWispr-portable-current.zip.sha256"
test ! -e "$a/installer/WinWispr-Setup-v1.1.0.exe.sha256"
test ! -e "$a/portable/WinWispr-portable-tmp-old.zip"
test -f "$a/portable/WinWispr-portable-tmp-new.zip"
test ! -e "$a/dev-check/validation"
# One recent file keeps the whole entry.
test -f "$a/dev-check/logs/old.txt"
test -f "$a/dev-check/tts-diagnostics/new.txt"
test -f "$a/model/model.tar.bz2"
test -f "$a/voices/voice/model.onnx"
test -f "$a/translation-model/opus-mt-de-en/source.spm"
test -f "$a/runtime-cache/win-x64/sherpa.dll"
test -f "$a/runtime-cache/build-script.sha256"
test ! -e "$a/runtime-cache/merge-tests"
test -f "$a/runtime-cache/new-entry/new.bin"
test ! -e "$a/teams-build"
test ! -e "$a/dev-check/bin"
test ! -e "$a/dev-check/obj"
test -f "$a/.vm-control/release/evidence.zip"

"$repo/eng/prune-artifacts.sh" >"$tmp_dir/again.out"
grep -Fxq 'PRUNE_ITEMS=0' "$tmp_dir/again.out"

rm -rf "$a"
"$repo/eng/prune-artifacts.sh" | grep -Fxq 'PRUNE=PASS'
if "$repo/eng/prune-artifacts.sh" --bogus >/dev/null 2>&1; then
    echo 'Unknown options must be rejected.' >&2
    exit 1
fi

echo 'prune-artifacts contract: PASS'
