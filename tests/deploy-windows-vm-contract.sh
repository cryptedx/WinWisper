#!/usr/bin/env bash
set -euo pipefail

# `set -e` ignores pipelines negated with `!`, so negative guards go through here.
refute() {
    if "$@"; then
        printf 'unexpected match: %s\n' "$*" >&2
        exit 1
    fi
}

# A failed run's last stderr line names the phase and the cause: FAILED: <phase>: <reason>.
assert_failed() {
    local file="$1" expected="$2" last
    last="$(tail -n 1 "$file")"
    if [[ "$last" != "FAILED: $expected"* ]]; then
        printf 'expected the last line to start with "FAILED: %s", got "%s"\n' "$expected" "$last" >&2
        exit 1
    fi
}

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
runner="$repo_root/eng/deploy-windows-vm.sh"

test -x "$runner"
bash -n "$runner"

# The fake VM runs a real rsync server, which needs rsync 3 like the runner.
fake_rsync=''
for candidate in "${WINWISPR_RSYNC:-}" /opt/homebrew/bin/rsync /usr/local/bin/rsync rsync; do
    [[ -n "$candidate" ]] && command -v "$candidate" >/dev/null 2>&1 || continue
    if [[ "$("$candidate" --version 2>/dev/null)" =~ ^rsync[[:space:]]+version[[:space:]]+3\. ]]; then
        fake_rsync="$(command -v "$candidate")"
        break
    fi
done
[[ -n "$fake_rsync" ]] || { echo 'rsync 3 not found; install it with brew install rsync' >&2; exit 1; }
export WINWISPR_RSYNC="$fake_rsync" FAKE_RSYNC="$fake_rsync"
# The Mac's real free space must not decide these scenarios.
export WINWISPR_MIN_MAC_FREE_GIB=0

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-deploy-contract.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT
# A small stand-in for the bundled model, which is not in Git and so is
# missing from every fresh worktree. Each fake checkout's eng/model-package.json
# pins its size and SHA-256 instead of the real model's.
model_name='sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2'
model_source="$tmp_dir/$model_name"
printf 'stand-in for the bundled speech model\n' >"$model_source"
model_bytes="$(stat -f '%z' "$model_source")"
model_sha256="$(shasum -a 256 "$model_source" | awk '{print $1}')"

write_repo() {
    local repo="$tmp_dir/$1"
    mkdir -p "$repo/eng" "$repo/artifacts/model"
    cp "$runner" "$repo/eng/deploy-windows-vm.sh"
    printf '{"version": 1, "speechArchive": {"path": "artifacts/model/%s", "bytes": %s, "sha256": "%s"}}\n' \
        "$model_name" "$model_bytes" "$model_sha256" >"$repo/eng/model-package.json"
    ln -s "$model_source" "$repo/artifacts/model/$model_name"
    mkdir -p "$repo/artifacts/voices/vits-piper-test"
    printf 'voice\n' >"$repo/artifacts/voices/vits-piper-test/model.onnx"
    printf 'mac metadata\n' >"$repo/artifacts/voices/.DS_Store"
    mkdir -p "$repo/artifacts/translation-model/opus-mt-de-en/onnx"
    printf 'translation\n' >"$repo/artifacts/translation-model/opus-mt-de-en/onnx/encoder_model_quantized.onnx"
    cat >"$repo/eng/check-local.sh" <<'EOF'
#!/usr/bin/env bash
echo 'LOCAL_CHECKS=STUB'
exit "${FAKE_LOCAL_CHECKS_EXIT:-0}"
EOF
    cp "$repo_root/eng/vm-lock.sh" "$repo/eng/vm-lock.sh"
    chmod +x "$repo/eng/deploy-windows-vm.sh" "$repo/eng/check-local.sh"
    (
        cd "$repo"
        git init -q
        git config user.email test@example.invalid
        git config user.name 'Deploy Contract'
        printf 'source\n' >README.md
        printf 'artifacts/model/\nartifacts/voices/\nartifacts/translation-model/\n' >.gitignore
        git add README.md .gitignore eng/deploy-windows-vm.sh eng/model-package.json eng/check-local.sh eng/vm-lock.sh
        git commit -qm 'chore: baseline'
    )
    printf '%s\n' "$repo"
}

# The fake VM's mirror for one scenario. The model is copied into it with its
# time stamp, so rsync finds it current, as it does for the real model.
fake_mirror() {
    printf '%s/c/windev/WinWispr/src\n' "$1"
}
seed_model() {
    local mirror
    mirror="$(fake_mirror "$1")"
    mkdir -p "$mirror/artifacts/model"
    cp -p "$model_source" "$mirror/artifacts/model/$model_name"
}

write_ssh_shim() {
    local bin="$1"
    mkdir -p "$bin"
    cat >"$bin/ssh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

# rsync starts its server through SSH: serve it from the fake VM directory.
for ((i = 1; i <= $#; i++)); do
    if [[ "${!i}" == */rsync.exe ]]; then
        printf 'RSYNC %s\n' "${*:i+1}" >>"$FAKE_SSH_LOG"
        command=("$FAKE_RSYNC")
        for argument in "${@:i+1}"; do
            case "$argument" in
                /c/*) command+=("$FAKE_VM_ROOT$argument") ;;
                *) command+=("$argument") ;;
            esac
        done
        exec "${command[@]}"
    fi
done

# SSH is a PowerShell shell on the VM: only encoded payloads are safe.
[[ "$*" == *" -EncodedCommand "* ]]
cmd="$(printf '%s' "${!#}" | base64 --decode | iconv -f UTF-16LE -t UTF-8)"
if [[ "$cmd" == *Register-ScheduledTask* ]]; then
    nested="$(printf '%s' "$cmd" | grep -oE 'EncodedCommand [A-Za-z0-9+/=]+' | awk '{print $2}')"
    cmd+=$'\n'"$(printf '%s' "$nested" | base64 --decode | iconv -f UTF-16LE -t UTF-8)"
fi
printf '%s\n' "$cmd" >>"$FAKE_SSH_LOG"

control="$FAKE_VM_ROOT/c/windev/WinWispr/src/artifacts/.vm-control"
result_file() {
    local remote
    remote="$(printf '%s' "$cmd" | grep -oE 'C:/windev/WinWispr/src/artifacts/\.vm-control/worker-[0-9-]+\.out' | sed -n '1p')"
    test -n "$remote"
    printf '%s/%s\n' "$control" "$(basename "$remote")"
}

case "$cmd" in
    *WINWISPR_POLL*)
        result_local="$(result_file)"
        offset="$(printf '%s\n' "$cmd" | sed -n 's/^\$offset = \([0-9]*\)$/\1/p')"
        printf 'STATE=%s\r\n' "${FAKE_POLL_STATE:-Ready}"
        [[ "${FAKE_POLL_STATE:-Ready}" == MISSING ]] || printf 'RESULT=%s\r\n' "${FAKE_POLL_RESULT:-0}"
        if [[ -f "$result_local.progress" ]]; then
            printf 'PROGRESS_BYTES=%s\r\n' "$(tail -c "+$((offset + 1))" "$result_local.progress" | base64 | tr -d '\n')"
        fi
        # A slow worker: its result shows up only after the given number of polls.
        polls="$(($(cat "$result_local.polls" 2>/dev/null || echo 0) + 1))"
        printf '%s\n' "$polls" >"$result_local.polls"
        if [[ -f "$result_local" ]] && ((polls > ${FAKE_RESULT_AFTER_POLLS:-0})); then
            printf 'RESULT_FILE=%s\r\n' "$(base64 <"$result_local" | tr -d '\n')"
        fi
        exit 0
        ;;
    *Register-ScheduledTask*)
        result_local="$(result_file)"
        # The task itself creates the control directory in the mirror.
        [[ "$cmd" == *"New-Item -ItemType Directory -Force -Path 'C:/windev/WinWispr/src/artifacts/.vm-control'"* ]]
        mkdir -p "$control"
        printf 'worker started\n' >"$result_local.progress"
        # A worker that never finishes writes progress but no result.
        [[ "${FAKE_NO_RESULT:-0}" != 1 ]] || exit 0
        trap 'printf "WORKER_EXIT_CODE=%s\n" "${FAKE_WORKER_EXIT:-0}" >>"$result_local"' EXIT
        # A scenario's own worker output, for example a failed desktop E2E test.
        if [[ -n "${FAKE_WORKER_OUTPUT:-}" ]]; then
            cp "$FAKE_WORKER_OUTPUT" "$result_local"
            exit 0
        fi
        if [[ "$cmd" == *Prune-VmArtifacts.ps1* ]]; then
            mode=remove
            [[ "$cmd" != *" -DryRun"* ]] || mode=dry-run
            printf 'PRUNE_ITEMS=0\r\nPRUNE_MODE=%s\r\nPRUNE=PASS\r\n' "$mode" >"$result_local"
            exit 0
        fi
        if [[ "${FAKE_CLEANUP_MODE:-0}" == 1 ]]; then
            printf 'LEGACY_SOURCE=REMOVED\r\nLEGACY_INCOMING=REMOVED\r\nLEGACY_ARTIFACTS=REMOVED\r\nTEST_DATA=PRESERVED\r\nAPP_INSTALL=PRESERVED\r\nAPP_DATA=PRESERVED\r\nLEGACY_CLEANUP=PASS\r\n' >"$result_local"
            exit 0
        fi
        if [[ "${FAKE_RELEASE_MODE:-0}" == 1 && "$cmd" == *"-DryRun"* ]]; then
            printf 'RELEASE_VERSION=1.3.0\r\n' >"$result_local"
            exit 0
        fi
        if [[ "${FAKE_RELEASE_MODE:-0}" == 1 ]]; then
            test -f "$control/WinWispr-release-request.zip"
            mkdir -p "$control/release"
            printf 'release\n' >"$control/release/WinWispr-release-v1.3.0.zip"
            sha="$(shasum -a 256 "$control/release/WinWispr-release-v1.3.0.zip" | awk '{print $1}')"
            printf '%s *WinWispr-release-v1.3.0.zip\n' "$sha" >"$control/release/WinWispr-release-v1.3.0.zip.sha256"
            printf 'RELEASE_VERSION=1.3.0\r\nRELEASE_ARCHIVE=C:\\windev\\WinWispr\\src\\artifacts\\.vm-control\\release\\WinWispr-release-v1.3.0.zip\r\nRELEASE_SHA256=%s\r\nAPP_RUNNING=1\r\nVERIFY=PASS\r\n' "$sha" >"$result_local"
            exit 0
        fi
        if [[ "$cmd" == *" -ModelPackage"* ]]; then
            models="$FAKE_VM_ROOT/c/windev/WinWispr/src/artifacts/models"
            mkdir -p "$models"
            printf 'model package\n' >"$models/WinWispr-Models-v1.exe"
            sha="$(shasum -a 256 "$models/WinWispr-Models-v1.exe" | awk '{print $1}')"
            printf '%s *WinWispr-Models-v1.exe\r\n' "$sha" >"$models/WinWispr-Models-v1.exe.sha256"
            printf 'MODEL_PACKAGE_BUILD=rebuilt\r\nMODEL_PACKAGE_VERSION=1\r\nMODEL_PACKAGE=C:\\windev\\WinWispr\\src\\artifacts\\models\\WinWispr-Models-v1.exe\r\nMODEL_PACKAGE_SHA256=%s\r\nVERIFY=PASS\r\n' "$sha" >"$result_local"
            exit 0
        fi
        if [[ "$cmd" == *" -Check "* ]]; then
            if [[ "${FAKE_CHECK_FAILURE:-0}" == 1 ]]; then
                printf 'tests failed\n' >"$result_local"
            elif [[ "$cmd" == *" -NoBuild"* ]]; then
                printf 'MODE=development-check\nBUILD_MODE=reused\nCHECK=PASS\n' >"$result_local"
            else
                printf 'MODE=development-check\nCHECK=PASS\n' >"$result_local"
            fi
            exit 0
        fi
        printf 'RUNTIME=cache\r\nAPP_RUNNING=1\r\nVERIFY=PASS\r\n' >"$result_local"
        exit 0
        ;;
    *Unregister-ScheduledTask*)
        exit 0
        ;;
    *"Remove-Item -LiteralPath 'C:/windev/WinWispr/src/artifacts/.vm-control'"*)
        rm -rf "$control"
        exit 0
        ;;
esac

printf 'unhandled fake SSH command: %s\n' "$cmd" >&2
exit 99
EOF
    chmod +x "$bin/ssh"
}

write_request() {
    local repo="$1" path="$2" request_dir="$tmp_dir/request"
    rm -rf "$request_dir"
    mkdir -p "$request_dir"
    printf 'v1.2.3\n' >"$request_dir/previous-tag.txt"
    git -C "$repo" rev-parse HEAD >"$request_dir/source-commit.txt"
    printf 'feat: release\n' >"$request_dir/commits.rs"
    (cd "$request_dir" && zip -q -X "$path" previous-tag.txt source-commit.txt commits.rs)
}

repo="$(write_repo normal)"
bin="$tmp_dir/bin-normal"
write_ssh_shim "$bin"
vm="$tmp_dir/vm-normal"
mirror="$(fake_mirror "$vm")"
seed_model "$vm"
# Build output the VM keeps across runs, and a file the Mac no longer has.
mkdir -p "$mirror/artifacts/runtime-cache"
printf 'cache\n' >"$mirror/artifacts/runtime-cache/keep"
mkdir -p "$mirror/src/App/bin/Release" "$mirror/src/App/obj"
printf 'build\n' >"$mirror/src/App/bin/Release/App.dll"
printf 'build\n' >"$mirror/src/App/obj/project.assets.json"
printf 'stale\n' >"$mirror/stale.txt"
FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/normal-ssh.log" PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" >"$tmp_dir/normal.out"
grep -Fxq 'STATUS=PASS' "$tmp_dir/normal.out"
cmp "$repo/README.md" "$mirror/README.md"
test -f "$mirror/.git/HEAD"
test -f "$mirror/artifacts/runtime-cache/keep"
test -f "$mirror/src/App/bin/Release/App.dll"
test -f "$mirror/src/App/obj/project.assets.json"
test ! -e "$mirror/stale.txt"
test ! -e "$mirror/artifacts/.vm-control"
test ! -e "$repo/artifacts/.vm-control"
test ! -e "$repo/artifacts/.vm-lock"
grep -Fq "artifacts/model/$model_name" "$tmp_dir/normal-ssh.log"
cmp "$repo/artifacts/voices/vits-piper-test/model.onnx" "$mirror/artifacts/voices/vits-piper-test/model.onnx"
test ! -e "$mirror/artifacts/voices/.DS_Store"
cmp "$repo/artifacts/translation-model/opus-mt-de-en/onnx/encoder_model_quantized.onnx" \
    "$mirror/artifacts/translation-model/opus-mt-de-en/onnx/encoder_model_quantized.onnx"
grep -Fq 'WINWISPR_POLL' "$tmp_dir/normal-ssh.log"
grep -Fq 'Register-ScheduledTask' "$tmp_dir/normal-ssh.log"
grep -Fq 'EncodedCommand' "$tmp_dir/normal-ssh.log"
grep -Fq 'New-ScheduledTaskTrigger' "$tmp_dir/normal-ssh.log"
refute grep -Fq 'Start-ScheduledTask -TaskName' "$tmp_dir/normal-ssh.log"
refute grep -Fq 'Z:' "$tmp_dir/normal-ssh.log"
refute grep -Fq 'net.exe use' "$tmp_dir/normal-ssh.log"
refute grep -Fq 'scp' "$tmp_dir/normal-ssh.log"
refute grep -Fq 'Compress-Archive' "$tmp_dir/normal-ssh.log"

repo="$(write_repo release)"
bin="$tmp_dir/bin-release"
write_ssh_shim "$bin"
vm="$tmp_dir/vm-release"
seed_model "$vm"
request="$tmp_dir/release-request.zip"
write_request "$repo" "$request"
FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/release-ssh.log" FAKE_RELEASE_MODE=1 PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --release-request "$request" >"$tmp_dir/release.out"
grep -Eq '^RELEASE_ARCHIVE=/.*/release/artifacts/\.vm-control/release/WinWispr-release-v1\.3\.0\.zip$' "$tmp_dir/release.out"
grep -Fxq 'VERIFY=PASS' "$tmp_dir/release.out"
# Only the release evidence comes back from the VM.
test -f "$repo/artifacts/.vm-control/release/WinWispr-release-v1.3.0.zip"
test -f "$repo/artifacts/.vm-control/release/WinWispr-release-v1.3.0.zip.sha256"
test ! -e "$repo/artifacts/.vm-control/WinWispr-source.zip"
test ! -e "$repo/artifacts/.vm-control/WinWispr-release-request.zip"
test ! -e "$(fake_mirror "$vm")/artifacts/.vm-control"

repo="$(write_repo dry-run)"
bin="$tmp_dir/bin-dry-run"
write_ssh_shim "$bin"
vm="$tmp_dir/vm-dry-run"
seed_model "$vm"
request="$tmp_dir/dry-run-request.zip"
write_request "$repo" "$request"
FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/dry-run-ssh.log" FAKE_RELEASE_MODE=1 PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --release-request "$request" --dry-run >"$tmp_dir/dry-run.out"
grep -Fxq 'RELEASE_VERSION=1.3.0' "$tmp_dir/dry-run.out"
test ! -e "$repo/artifacts/.vm-control"

repo="$(write_repo cleanup)"
bin="$tmp_dir/bin-cleanup"
write_ssh_shim "$bin"
FAKE_VM_ROOT="$tmp_dir/vm-cleanup" FAKE_SSH_LOG="$tmp_dir/cleanup-ssh.log" PATH="$bin:$PATH" \
    FAKE_CLEANUP_MODE=1 \
    "$repo/eng/deploy-windows-vm.sh" --cleanup-legacy >"$tmp_dir/cleanup.out"
grep -Fxq 'LEGACY_CLEANUP=PASS' "$tmp_dir/cleanup.out"

repo="$(write_repo prune)"
bin="$tmp_dir/bin-prune"
write_ssh_shim "$bin"
# --model-package builds on the VM and copies the installer and its sidecar back,
# also from a dirty tree: the package depends only on its verified inputs.
package_repo="$(write_repo model-package)"
printf 'dirty\n' >>"$package_repo/README.md"
package_vm="$tmp_dir/vm-model-package"
seed_model "$package_vm"
FAKE_VM_ROOT="$package_vm" FAKE_SSH_LOG="$tmp_dir/model-package-ssh.log" PATH="$bin:$PATH" \
    "$package_repo/eng/deploy-windows-vm.sh" --model-package >"$tmp_dir/model-package.out"
grep -Fxq 'STATUS=PASS' "$tmp_dir/model-package.out"
package_local="$(sed -n 's/^MODEL_PACKAGE_LOCAL=//p' "$tmp_dir/model-package.out")"
[[ "$package_local" == */model-package/artifacts/models/WinWispr-Models-v1.exe ]]
cmp "$(fake_mirror "$package_vm")/artifacts/models/WinWispr-Models-v1.exe" "$package_local"
test -f "$package_repo/artifacts/models/WinWispr-Models-v1.exe.sha256"
grep -Fq ' -ModelPackage' "$tmp_dir/model-package-ssh.log"
test ! -e "$package_repo/artifacts/.vm-control"
for args in '--model-package --check' '--model-package --dry-run' '--model-package --rebuild-runtime'; do
    if "$package_repo/eng/deploy-windows-vm.sh" $args >"$tmp_dir/rejected.out" 2>&1; then
        echo "Expected rejection: $args" >&2
        exit 1
    fi
    assert_failed "$tmp_dir/rejected.out" 'options: '
done

# Pruning needs neither a clean tree nor the model, and ignores the Mac's space.
printf 'uncommitted change\n' >>"$repo/README.md"
rm "$repo/artifacts/model/$model_name"
FAKE_VM_ROOT="$tmp_dir/vm-prune" FAKE_SSH_LOG="$tmp_dir/prune-ssh.log" PATH="$bin:$PATH" WINWISPR_MIN_MAC_FREE_GIB=999999999 \
    "$repo/eng/deploy-windows-vm.sh" --prune --dry-run >"$tmp_dir/prune.out"
grep -Fxq 'PRUNE_MODE=dry-run' "$tmp_dir/prune.out"
grep -Fq -- "Prune-VmArtifacts.ps1' -ArtifactsPath 'C:/windev/WinWispr/src/artifacts' -DryRun" "$tmp_dir/prune-ssh.log"
if grep -Fq 'Deploy-WindowsVm.ps1' "$tmp_dir/prune-ssh.log"; then
    echo '--prune must not start the build worker.' >&2
    exit 1
fi
test ! -e "$(fake_mirror "$tmp_dir/vm-prune")/artifacts/model"
FAKE_VM_ROOT="$tmp_dir/vm-prune" FAKE_SSH_LOG="$tmp_dir/prune-ssh.log" PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --prune >"$tmp_dir/prune-remove.out"
grep -Fxq 'PRUNE_MODE=remove' "$tmp_dir/prune-remove.out"
test ! -e "$repo/artifacts/.vm-control"
for args in '--prune --check' '--prune --cleanup-legacy' '--prune --rebuild-runtime' '--prune --model-package' '--prune --release-request missing.zip'; do
    if "$repo/eng/deploy-windows-vm.sh" $args >"$tmp_dir/rejected.out" 2>&1; then
        echo "Expected rejection: $args" >&2
        exit 1
    fi
    assert_failed "$tmp_dir/rejected.out" 'options: '
done

# A short Mac disk stops a build before any SSH call.
: >"$tmp_dir/mac-full-ssh.log"
if FAKE_VM_ROOT="$tmp_dir/vm-prune" FAKE_SSH_LOG="$tmp_dir/mac-full-ssh.log" PATH="$bin:$PATH" WINWISPR_MIN_MAC_FREE_GIB=999999999 \
    "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/mac-full.out" 2>&1; then
    echo 'A short Mac disk must stop the run.' >&2
    exit 1
fi
grep -Fq 'GiB free on the Mac' "$tmp_dir/mac-full.out"
assert_failed "$tmp_dir/mac-full.out" 'preflight: only '
test ! -s "$tmp_dir/mac-full-ssh.log"
test ! -e "$repo/artifacts/.vm-lock"

repo="$(write_repo check)"
bin="$tmp_dir/bin-check"
write_ssh_shim "$bin"
printf 'uncommitted change\n' >>"$repo/README.md"
# A deterministic check does not need to package the large model.
rm "$repo/artifacts/model/$model_name"
vm="$tmp_dir/vm-check"
FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/check-ssh.log" PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check --test-filter "Name~a'b" >"$tmp_dir/check.out"
grep -Fxq 'CHECK=PASS' "$tmp_dir/check.out"
# The check builds the working tree, and it does not need the model.
cmp "$repo/README.md" "$(fake_mirror "$vm")/README.md"
test ! -e "$(fake_mirror "$vm")/artifacts/model"
test ! -e "$(fake_mirror "$vm")/artifacts/voices"
test ! -e "$(fake_mirror "$vm")/artifacts/translation-model"
refute grep -Fq "artifacts/model/$model_name" "$tmp_dir/check-ssh.log"
grep -Fxq 'LOCAL_CHECKS=STUB' "$tmp_dir/check.out"
if grep -Fxq 'LOCAL_CHECKS=STUB' "$tmp_dir/dry-run.out"; then
    echo 'Only --check runs the local contract checks.' >&2
    exit 1
fi
refute grep -Eq '^(VERIFY|APP_RUNNING|RELEASE_VERSION)=' "$tmp_dir/check.out"
grep -Fq -- "-Check -WorkingTreeState 'dirty' -TestFilter 'Name~a''b'" "$tmp_dir/check-ssh.log" || { cat "$tmp_dir/check-ssh.log"; exit 1; }
test ! -e "$repo/artifacts/.vm-control"

# The worker learns which tree it builds: the file list, sizes, and time stamps
# of what the sync sends, without .git. A file edited before another checkout's
# last build looks older than that build's output on the shared mirror, so the
# worker rebuilds whenever this name differs from the one its output came from.
tree_hash() {
    grep -oE -- "-SourceTreeSha256 '[0-9a-f]{64}'" "$1" | tail -n 1 | grep -oE '[0-9a-f]{64}'
}
first_tree="$(tree_hash "$tmp_dir/check-ssh.log")"
[[ -n "$first_tree" ]] || { echo 'The worker must receive -SourceTreeSha256.' >&2; exit 1; }
grep -Fxq "SOURCE_TREE_SHA256=$first_tree" "$tmp_dir/check.out"
tree_run() {
    : >"$tmp_dir/tree-ssh.log"
    FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/tree-ssh.log" PATH="$bin:$PATH" \
        "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/tree.out"
    grep -Fxq 'CHECK=PASS' "$tmp_dir/tree.out"
    tree_hash "$tmp_dir/tree-ssh.log"
}
# Unchanged: the same name, so the build stays incremental.
[[ "$(tree_run)" == "$first_tree" ]] || { echo 'An unchanged tree must keep its name.' >&2; exit 1; }
# Git metadata alone does not change what is built.
git -C "$repo" commit -qam 'chore: commit the change'
[[ "$(tree_run)" == "$first_tree" ]] || { echo 'A commit without content changes must keep the name.' >&2; exit 1; }
# The 2026-10-07 case: same size, edited at a time older than the last build.
sed -i '' 's/^source$/sourcE/' "$repo/README.md"
touch -t 202001010000 "$repo/README.md"
edited_tree="$(tree_run)"
[[ "$edited_tree" != "$first_tree" ]] || { echo 'An edit with an old time stamp must change the name.' >&2; exit 1; }
cmp "$repo/README.md" "$(fake_mirror "$vm")/README.md"
# A new untracked file and a deleted tracked file are part of the synced tree too.
printf 'new\n' >"$repo/added.txt"
added_tree="$(tree_run)"
[[ "$added_tree" != "$edited_tree" ]] || { echo 'An untracked file must change the name.' >&2; exit 1; }
rm "$repo/added.txt" "$repo/.gitignore"
[[ "$(tree_run)" != "$edited_tree" ]] || { echo 'A deleted file must change the name.' >&2; exit 1; }
git -C "$repo" checkout -q -- .gitignore

# --no-build reruns the tests against the last check build: no local checks, no
# sync, no model copy, and the worker gets the same tree name plus -NoBuild.
nb_repo="$(write_repo no-build)"
nb_vm="$tmp_dir/vm-no-build"
nb_mirror="$(fake_mirror "$nb_vm")"
# Like src/WinWispr/Runtime/win-x64: ignored by Git, absent on the Mac, created on the VM by the worker.
printf 'generated/\n' >>"$nb_repo/.gitignore"
git -C "$nb_repo" commit -qam 'chore: ignore generated output'
printf 'uncommitted change\n' >>"$nb_repo/README.md"
FAKE_VM_ROOT="$nb_vm" FAKE_SSH_LOG="$tmp_dir/nb-build-ssh.log" PATH="$bin:$PATH" \
    "$nb_repo/eng/deploy-windows-vm.sh" --check --test-filter Name~x >"$tmp_dir/nb-build.out" 2>&1
built_tree="$(tree_hash "$tmp_dir/nb-build-ssh.log")"
no_build_run() {
    : >"$tmp_dir/nb-ssh.log"
    FAKE_VM_ROOT="$nb_vm" FAKE_SSH_LOG="$tmp_dir/nb-ssh.log" PATH="$bin:$PATH" env "$@" \
        "$nb_repo/eng/deploy-windows-vm.sh" --check --no-build --test-filter Name~x >"$tmp_dir/nb.out" 2>"$tmp_dir/nb.err"
}
no_build_run
grep -Fxq 'CHECK=PASS' "$tmp_dir/nb.out"
grep -Fxq 'BUILD_MODE=reused' "$tmp_dir/nb.out"
refute grep -Fxq 'LOCAL_CHECKS=STUB' "$tmp_dir/nb.out"
grep -Fq 'is current; --no-build reuses the last --check build' "$tmp_dir/nb.err"
grep -Fq -- "-Check -WorkingTreeState 'dirty' -NoBuild -TestFilter 'Name~x'" "$tmp_dir/nb-ssh.log"
[[ "$(tree_hash "$tmp_dir/nb-ssh.log")" == "$built_tree" ]] || { echo '--no-build must name the same tree.' >&2; exit 1; }
refute grep -Fq "artifacts/model/$model_name" "$tmp_dir/nb-ssh.log"
test ! -e "$nb_repo/artifacts/.vm-lock"
# Neither does Git metadata, which other checkouts change all the time, nor
# ignored output that only the VM has.
git -C "$nb_repo" tag no-build-probe
mkdir -p "$nb_mirror/generated/win-x64"
printf 'runtime\n' >"$nb_mirror/generated/win-x64/runtime.dll"
no_build_run || { cat "$tmp_dir/nb.err"; exit 1; }
grep -Fxq 'CHECK=PASS' "$tmp_dir/nb.out"
test -f "$nb_mirror/generated/win-x64/runtime.dll"
# A local edit since the build: refused before the worker, and the mirror is untouched.
cp -p "$nb_repo/README.md" "$tmp_dir/nb-README.md"
printf 'edited after the build\n' >>"$nb_repo/README.md"
if no_build_run; then
    echo '--no-build must refuse a tree that changed since the build.' >&2
    exit 1
fi
assert_failed "$tmp_dir/nb.err" "sync: the VM mirror does not hold this checkout's tree"
grep -Fxq 'README.md' "$tmp_dir/nb.err"
cmp "$tmp_dir/nb-README.md" "$nb_mirror/README.md"
refute grep -Fq 'Deploy-WindowsVm.ps1' "$tmp_dir/nb-ssh.log"
cp -p "$tmp_dir/nb-README.md" "$nb_repo/README.md"
# A mirror another run changed since the build (a stray file the sync would delete).
printf 'stray\n' >"$nb_mirror/stray.txt"
if no_build_run; then
    echo '--no-build must refuse a mirror that no longer holds this tree.' >&2
    exit 1
fi
assert_failed "$tmp_dir/nb.err" "sync: the VM mirror does not hold this checkout's tree"
test -f "$nb_mirror/stray.txt"
rm "$nb_mirror/stray.txt"
# The worker refuses a missing build or one from another tree; that reason ends the output.
printf '%s\n' 'MODE=development-check' \
    "WORKER_FAILURE=reuse-build: The last --check build in the mirror was built from source tree $(printf 'b%.0s' {1..64}), not from this checkout's tree $built_tree" \
    >"$tmp_dir/nb-refused.txt"
if no_build_run FAKE_WORKER_OUTPUT="$tmp_dir/nb-refused.txt" FAKE_WORKER_EXIT=1; then
    echo 'A refused reuse must fail the run.' >&2
    exit 1
fi
assert_failed "$tmp_dir/nb.err" 'worker: reuse-build: The last --check build in the mirror was built from source tree bbbb'

# The worker side of --no-build: it reuses only a completed check build of this exact tree,
# never starts a build, and still checks the desktop before the tests.
worker="$repo_root/eng/Deploy-WindowsVm.ps1"
grep -Fq -- '[switch]$NoBuild' "$worker"
grep -Fq -- "throw '-NoBuild requires -Check.'" "$worker"
grep -Fq -- 'if ($builtTree -cne $SourceTreeSha256)' "$worker"
grep -Fq -- "Write-Output 'BUILD_MODE=reused'" "$worker"
grep -Fq -- 'Write-Output "REUSED_BUILD_TREE=$builtTree"' "$worker"
grep -Fq -- 'REUSED_BUILD_UTC=' "$worker"
grep -Fq -- 'No completed --check build to reuse in the mirror' "$worker"
grep -Fq -- 'Write-Output "WORKER_FAILURE=${currentPhase}: ' "$worker"
reuse_line="$(grep -n -F -- 'Assert-ReusableCheckBuild -MarkerPath $sourceTreeMarker' "$worker" | cut -d: -f1)"
build_line="$(grep -n -F -- 'Start-SourceTreeBuild -MarkerPath $sourceTreeMarker' "$worker" | cut -d: -f1)"
desktop_line="$(grep -n -F -- 'Assert-UndisturbedDesktop -Strict' "$worker" | cut -d: -f1)"
else_line="$(awk -v from="$reuse_line" 'NR > from && /^        } else {$/ { print NR; exit }' "$worker")"
((reuse_line < else_line && else_line < build_line && build_line < desktop_line)) ||
    { echo 'The worker must reuse the build instead of building, then check the desktop.' >&2; exit 1; }
if command -v pwsh >/dev/null 2>&1; then
    printf '%s\n' 'param([string]$Path)' '$errors = $null' \
        '[void][Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)' \
        'if ($errors) { $errors | ForEach-Object { $_.ToString() }; exit 1 }' >"$tmp_dir/parse.ps1"
    pwsh -NoLogo -NoProfile -File "$tmp_dir/parse.ps1" -Path "$worker"
fi

# With the model on the Mac, a check copies it, but not the voices, for the speech acceptance tests.
model_repo="$(write_repo check-model)"
model_vm="$tmp_dir/vm-check-model"
FAKE_VM_ROOT="$model_vm" FAKE_SSH_LOG="$tmp_dir/check-model-ssh.log" PATH="$bin:$PATH" \
    "$model_repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/check-model.out" 2>&1
grep -Fxq 'CHECK=PASS' "$tmp_dir/check-model.out"
cmp "$model_source" "$(fake_mirror "$model_vm")/artifacts/model/$model_name"
test ! -e "$(fake_mirror "$model_vm")/artifacts/voices"
test ! -e "$model_repo/artifacts/.vm-control"

# The result file, not the task state, says the worker finished: a vanished task or a stale
# LastTaskResult only fails the run once the worker has also gone quiet.
for lost in 'MISSING:0' 'Ready:267009'; do
    lost_repo="$(write_repo "lost-${lost%%:*}")"
    FAKE_VM_ROOT="$tmp_dir/vm-lost-${lost%%:*}" FAKE_SSH_LOG="$tmp_dir/lost-ssh.log" PATH="$bin:$PATH" \
        FAKE_POLL_STATE="${lost%%:*}" FAKE_POLL_RESULT="${lost##*:}" FAKE_RESULT_AFTER_POLLS=2 \
        "$lost_repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/lost.out" 2>&1 || { cat "$tmp_dir/lost.out"; exit 1; }
    grep -Fxq 'CHECK=PASS' "$tmp_dir/lost.out"
done
silent_repo="$(write_repo silent)"
if FAKE_VM_ROOT="$tmp_dir/vm-silent" FAKE_SSH_LOG="$tmp_dir/silent-ssh.log" PATH="$bin:$PATH" \
    FAKE_POLL_STATE=MISSING FAKE_NO_RESULT=1 WINWISPR_TASK_LOST_GRACE_SECONDS=1 \
    "$silent_repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/silent.out" 2>&1; then
    echo 'A lost task whose worker writes nothing must fail the run.' >&2
    exit 1
fi
grep -Fq 'and the worker wrote nothing for 1 s' "$tmp_dir/silent.out"
assert_failed "$tmp_dir/silent.out" 'worker: interactive Windows task disappeared'

for args in '' '--test-filter Name~x' '--check --release-request missing.zip' '--check --dry-run' '--check --rebuild-runtime' '--check --cleanup-legacy' '--check --test-filter' \
    '--no-build' '--no-build --test-filter Name~x' '--check --no-build --rebuild-runtime' '--check --no-build --bogus'; do
    if "$repo/eng/deploy-windows-vm.sh" $args >"$tmp_dir/rejected.out" 2>&1; then
        echo "Expected rejection: $args" >&2
        exit 1
    fi
    assert_failed "$tmp_dir/rejected.out" ''
done
"$repo/eng/deploy-windows-vm.sh" --no-build >/dev/null 2>"$tmp_dir/rejected.err" || true
assert_failed "$tmp_dir/rejected.err" 'options: --no-build requires --check'
"$repo/eng/deploy-windows-vm.sh" --check --no-build --bogus >/dev/null 2>"$tmp_dir/rejected.err" || true
assert_failed "$tmp_dir/rejected.err" 'options: unknown option: --bogus'
# The reason a deployment refuses a dirty tree is the last line, not buried mid-output.
"$repo/eng/deploy-windows-vm.sh" >/dev/null 2>"$tmp_dir/dirty.err" || true
assert_failed "$tmp_dir/dirty.err" 'preflight: working tree is dirty; commit changes before deployment'
if FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/check-ssh.log" FAKE_CHECK_FAILURE=1 PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/check-failure.out" 2>&1; then
    echo 'Failed tests must not pass the development check.' >&2
    exit 1
fi
assert_failed "$tmp_dir/check-failure.out" 'result: VM worker did not report CHECK=PASS'
: >"$tmp_dir/local-fail-ssh.log"
if FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/local-fail-ssh.log" FAKE_LOCAL_CHECKS_EXIT=1 PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/local-failure.out" 2>&1; then
    echo 'Failed local checks must not pass the development check.' >&2
    exit 1
fi
grep -Fq 'local contract checks failed' "$tmp_dir/local-failure.out"
assert_failed "$tmp_dir/local-failure.out" 'local-checks: local contract checks failed'
test ! -s "$tmp_dir/local-fail-ssh.log" || { echo 'Failed local checks must stop before SSH.' >&2; exit 1; }
test ! -e "$repo/artifacts/.vm-control"
mkdir "$repo/artifacts/.vm-control"
printf 'owned by another run\n' >"$repo/artifacts/.vm-control/sentinel"
if "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/check-busy.out" 2>&1; then
    echo 'Concurrent operations must be rejected.' >&2
    exit 1
fi
grep -Fxq 'owned by another run' "$repo/artifacts/.vm-control/sentinel"
assert_failed "$tmp_dir/check-busy.out" 'lock: control directory already exists'
rm "$repo/artifacts/.vm-control/sentinel"
rmdir "$repo/artifacts/.vm-control"
mkdir "$repo/artifacts/.vm-lock"
printf 'PID=%s\n' "$$" >"$repo/artifacts/.vm-lock/owner"
if FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/check-ssh.log" PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/check-locked.out" 2>&1; then
    echo 'A run must not start while another checkout holds the VM lock.' >&2
    exit 1
fi
grep -Fq 'another VM operation holds' "$tmp_dir/check-locked.out"
assert_failed "$tmp_dir/check-locked.out" 'lock: another VM operation holds'
# A lock whose owner has exited is taken over.
printf 'PID=999999\n' >"$repo/artifacts/.vm-lock/owner"
FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/check-ssh.log" PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/check-stale-lock.out"
grep -Fxq 'CHECK=PASS' "$tmp_dir/check-stale-lock.out"
test ! -e "$repo/artifacts/.vm-lock"
if FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/check-ssh.log" FAKE_WORKER_EXIT=1 PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check >"$tmp_dir/worker-failure.out" 2>&1; then
    echo 'A failed worker must not pass even if it printed CHECK=PASS.' >&2
    exit 1
fi
assert_failed "$tmp_dir/worker-failure.out" 'worker: interactive Windows worker failed'

# A failed desktop E2E test: the worker names the failing phase, and every
# foreground line of the test output is repeated, once, right before FAILED.
cat >"$tmp_dir/e2e-failure.txt" <<'EOF'
PHASE=tests STATE=START UTC=2026-10-09T10:00:00Z SECONDS=0.000
failed TaskBoardEndToEndTests.Dictation (31s)
Taskboard E2E failed: no text arrived
foreground window at failure: 'SpecAssist - Main' of SpecAssist (pid 4242)
Assert.Fail failed. Clipboard: ''. Foreground: 'Notepad' of notepad (pid 77). Held modifiers: none.
PHASE=tests STATE=FAIL UTC=2026-10-09T10:01:00Z SECONDS=60.000
foreground window at failure: 'SpecAssist - Main' of SpecAssist (pid 4242)
WORKER_FAILURE=tests: Command failed with exit code 2: WinWispr.Tests.exe --filter TestCategory=TaskBoardE2E
PHASE=total STATE=FAIL UTC=2026-10-09T10:01:00Z SECONDS=61.000
EOF
if FAKE_VM_ROOT="$vm" FAKE_SSH_LOG="$tmp_dir/check-ssh.log" FAKE_WORKER_OUTPUT="$tmp_dir/e2e-failure.txt" FAKE_WORKER_EXIT=1 PATH="$bin:$PATH" \
    "$repo/eng/deploy-windows-vm.sh" --check --test-filter TestCategory=TaskBoardE2E >"$tmp_dir/e2e-failure.out" 2>"$tmp_dir/e2e-failure.err"; then
    echo 'A failed desktop E2E test must fail the check.' >&2
    exit 1
fi
assert_failed "$tmp_dir/e2e-failure.err" 'worker: tests: Command failed with exit code 2: WinWispr.Tests.exe --filter TestCategory=TaskBoardE2E'
tail -n 3 "$tmp_dir/e2e-failure.err" | head -n 2 >"$tmp_dir/e2e-foreground.txt"
printf '%s\n' "foreground window at failure: 'SpecAssist - Main' of SpecAssist (pid 4242)" \
    "Assert.Fail failed. Clipboard: ''. Foreground: 'Notepad' of notepad (pid 77). Held modifiers: none." |
    cmp - "$tmp_dir/e2e-foreground.txt" || { cat "$tmp_dir/e2e-failure.err"; exit 1; }

echo 'deploy-windows-vm contract: PASS'
