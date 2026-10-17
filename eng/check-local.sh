#!/usr/bin/env bash
set -uo pipefail

# Runs the macOS-side contract tests. The Windows build and MSTest suite run
# through ./eng/deploy-windows-vm.sh --check, which calls this script first.

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

die() {
    printf 'check-local: %s\n' "$1" >&2
    exit 1
}

for tool in git python3 clang++ rg; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        case "$tool" in
            rg) die 'required command not found: rg (install ripgrep: brew install ripgrep)' ;;
            clang++) die 'required command not found: clang++ (install the Xcode command line tools: xcode-select --install)' ;;
            *) die "required command not found: $tool" ;;
        esac
    fi
done

# The PowerShell unit tests target Windows (git.exe, NTFS reparse points), so
# they only run when this script runs on Windows.
powershell_runner=''
if [[ "$OSTYPE" != msys* && "$OSTYPE" != cygwin* ]]; then
    :
elif command -v pwsh >/dev/null 2>&1; then
    powershell_runner="$(command -v pwsh)"
elif command -v powershell >/dev/null 2>&1; then
    powershell_runner="$(command -v powershell)"
fi

failed=()
log_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-check-local.XXXXXX")"
trap 'rm -rf "$log_dir"' EXIT

run() {
    local name="$1" log
    shift
    log="$log_dir/$(basename "$name").log"
    if "$@" >"$log" 2>&1; then
        printf 'PASS %s\n' "$name"
    else
        printf 'FAIL %s\n' "$name"
        sed 's/^/    /' "$log" | tail -n 20
        failed+=("$name")
    fi
}

for test_script in "$repo_root"/tests/*.sh; do
    run "tests/$(basename "$test_script")" bash "$test_script"
done
run tests/native-speech-boundary.py python3 "$repo_root/tests/native-speech-boundary.py"
run tests/markdown-links.py python3 "$repo_root/tests/markdown-links.py"

# ReleaseRequest and ReleaseEvidence run inside release-contract.sh; TtsPackaging
# runs from MSTest on Windows. These have no other runner.
for ps_test in ArtifactHash DevelopmentVersion ReleaseVersion; do
    if [[ -n "$powershell_runner" ]]; then
        run "tests/$ps_test.Tests.ps1" "$powershell_runner" -NoLogo -NoProfile -File "$repo_root/tests/$ps_test.Tests.ps1"
    else
        printf 'SKIP tests/%s.Tests.ps1 (Windows-only PowerShell test)\n' "$ps_test"
    fi
done

# These tests are platform-neutral, so they run wherever PowerShell exists.
prune_runner="$(command -v pwsh || command -v powershell || true)"
for ps_test in Prune-VmArtifacts SourceTreeBuild; do
    if [[ -n "$prune_runner" ]]; then
        run "tests/$ps_test.Tests.ps1" "$prune_runner" -NoLogo -NoProfile -File "$repo_root/tests/$ps_test.Tests.ps1"
    else
        printf 'SKIP tests/%s.Tests.ps1 (install PowerShell: brew install powershell)\n' "$ps_test"
    fi
done

if ((${#failed[@]} > 0)); then
    printf 'check-local: %d failed: %s\n' "${#failed[@]}" "${failed[*]}" >&2
    exit 1
fi
echo 'LOCAL_CHECKS=PASS'
