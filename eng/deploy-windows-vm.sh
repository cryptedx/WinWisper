#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
remote_host='windows-vm'
# The VM (windows-vm, the local VMware Fusion VM on the Mac) builds
# from an rsync mirror of the calling checkout. Build output
# stays in the mirror's artifacts directory, which the sync never touches.
remote_mirror='C:/windev/WinWispr/src'
remote_control_root="$remote_mirror/artifacts/.vm-control"
local_control_root="$repo_root/artifacts/.vm-control"
remote_worker="$remote_mirror/eng/Deploy-WindowsVm.ps1"
remote_cleanup="$remote_mirror/eng/Cleanup-WindowsVm.ps1"
remote_prune="$remote_mirror/eng/Prune-VmArtifacts.ps1"
# The VM's MSYS2 rsync sees C: as /c.
remote_rsync='C:/windev/tools/msys64/usr/bin/rsync.exe'
rsync_mirror='/c/windev/WinWispr/src/'
# The model package's inputs and pins: eng/model-package.json.
model_package_json="$repo_root/eng/model-package.json"
local_model="$repo_root/$(plutil -extract speechArchive.path raw -o - "$model_package_json")"
local_voices="$repo_root/artifacts/voices"
local_translation_model="$repo_root/artifacts/translation-model"
model_bytes="$(plutil -extract speechArchive.bytes raw -o - "$model_package_json")"
model_sha256="$(plutil -extract speechArchive.sha256 raw -o - "$model_package_json")"
rebuild_runtime=0
release_request=''
dry_run=0
cleanup_legacy=0
prune=0
check=0
no_build=0
test_filter=''
model_package=0

usage() {
    cat <<'EOF'
Usage: ./eng/deploy-windows-vm.sh [--rebuild-runtime]
       ./eng/deploy-windows-vm.sh --check [--no-build] [--test-filter <expression>]
       ./eng/deploy-windows-vm.sh --cleanup-legacy
       ./eng/deploy-windows-vm.sh --prune [--dry-run]
       ./eng/deploy-windows-vm.sh --model-package
       ./eng/deploy-windows-vm.sh --release-request <zip> [--dry-run]

Builds, tests, packages, installs, and interactively smoke-tests WinWispr on
the Windows VM windows-vm, the local VMware Fusion VM on the Mac. Every run first mirrors this checkout,
including uncommitted changes and .git, with rsync into C:\windev\WinWispr\src.
Set WINWISPR_RSYNC to choose the local rsync 3 binary.
--check builds the working tree and runs tests without packaging or installing.
--check --no-build reruns the tests against the last --check build in the
mirror, for example to triage a flaky test: it neither syncs nor builds and
skips the local contract checks, but takes the VM lock and checks the desktop
like any check. It refuses unless that build completed from exactly this
checkout's current tree (file list, sizes, time stamps) and the mirror still
holds that tree, so another checkout's build or a later edit is never reused;
rebuild with --check then. The reused build is named in the output:
BUILD_MODE=reused, REUSED_BUILD_TREE and REUSED_BUILD_UTC.
--prune removes old WinWispr output from the VM; with --dry-run it only lists
it. Every other run prunes the same way before it builds.
--model-package builds the signed WinWispr Models installer for the version in
eng/model-package.json (or reuses the VM's cached one) and copies it with its
sidecar to artifacts/models/ for ./eng/publish-models.sh. Deployments and
releases install the same cached installer after Setup.
The Mac needs WINWISPR_MIN_MAC_FREE_GIB (default 15) GiB free: the local VM's
disk grows on it and never shrinks by itself.
A failed run always ends with the stderr line FAILED: <phase>: <reason>, after
any "foreground window at failure:" or "Foreground:" lines of the test output.
EOF
}

# A failed run ends with one stderr line naming the phase and the cause, so
# `tail -1` finds it however long the output above it is.
phase=options
failure_reason=''
failed_command=''
die() {
    failure_reason="$1"
    printf 'deploy-windows-vm: %s\n' "$1" >&2
    exit 1
}

# A desktop E2E failure names the window that held the foreground; these lines
# are repeated right before the FAILED line, where a narrow grep finds them.
foreground_lines() {
    local text="${task_output:-}"
    if [[ -z "$text" && -n "${local_result_path:-}" && -f "$local_result_path.progress" ]]; then
        text="$(tr -d '\r' <"$local_result_path.progress")"
    fi
    [[ -n "$text" ]] || return 0
    printf '%s\n' "$text" | grep -E 'foreground window at failure:|Foreground:' | awk '!seen[$0]++' || true
}

report_failure() {
    local status="$1" foreground="$2" reason="$failure_reason"
    ((status != 0)) || return 0
    [[ -n "$reason" ]] || reason="${failed_command:+command failed: $failed_command; }exit status $status"
    [[ -z "$foreground" ]] || printf '%s\n' "$foreground" >&2
    printf 'FAILED: %s: %s\n' "$phase" "${reason//$'\n'/ }" >&2
}

on_early_exit() {
    local status=$?
    report_failure "$status" "$(foreground_lines)"
}
trap on_early_exit EXIT
trap 'failed_command=$BASH_COMMAND' ERR

while (($# > 0)); do
    case "$1" in
        --check)
            check=1
            ;;
        --no-build)
            no_build=1
            ;;
        --test-filter)
            shift
            (($# > 0)) && [[ -n "$1" && "$1" != -* ]] || die 'missing --test-filter expression'
            test_filter="$1"
            ;;
        --rebuild-runtime)
            rebuild_runtime=1
            ;;
        --release-request)
            shift
            (($# > 0)) || die 'missing --release-request value'
            release_request="$1"
            ;;
        --dry-run)
            dry_run=1
            ;;
        --cleanup-legacy)
            cleanup_legacy=1
            ;;
        --prune)
            prune=1
            ;;
        --model-package)
            model_package=1
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            die "unknown option: $1"
            ;;
    esac
    shift
done

if ((check)); then
    ((cleanup_legacy == 0 && dry_run == 0 && rebuild_runtime == 0 && model_package == 0)) && [[ -z "$release_request" ]] ||
        die '--check cannot be combined with deployment or release options'
elif [[ -n "$test_filter" ]]; then
    die '--test-filter requires --check'
fi
((no_build == 0 || check)) || die '--no-build requires --check'

if ((cleanup_legacy)); then
    ((rebuild_runtime == 0)) || die '--cleanup-legacy cannot be combined with --rebuild-runtime'
    [[ -z "$release_request" ]] || die '--cleanup-legacy cannot be combined with --release-request'
    ((dry_run == 0)) || die '--cleanup-legacy cannot be combined with --dry-run'
    ((model_package == 0)) || die '--cleanup-legacy cannot be combined with --model-package'
fi

if ((prune)); then
    ((check == 0 && cleanup_legacy == 0 && rebuild_runtime == 0 && model_package == 0)) && [[ -z "$release_request" ]] ||
        die '--prune can only be combined with --dry-run'
fi

if ((model_package)); then
    ((dry_run == 0 && rebuild_runtime == 0 && check == 0)) || die '--model-package cannot be combined with check, dry-run, or runtime rebuild options'
    [[ -z "$release_request" ]] || die '--model-package cannot be combined with --release-request'
fi

release_mode=0
if [[ -n "$release_request" ]] || ((dry_run && !prune)); then
    release_mode=1
fi
if ((release_mode)); then
    [[ -n "$release_request" ]] || die '--release-request is required in release mode'
    [[ -f "$release_request" ]] || die "release request not found: $release_request"
    ((rebuild_runtime == 0)) || die '--rebuild-runtime cannot be combined with release mode'
fi

phase=preflight
for tool in git ssh unzip shasum grep awk sed tr basename date base64 iconv plutil; do
    command -v "$tool" >/dev/null 2>&1 || die "required command not found: $tool"
done

# macOS ships openrsync, which speaks an older protocol than the VM's rsync.
find_rsync() {
    local candidate version
    for candidate in "${WINWISPR_RSYNC:-}" /opt/homebrew/bin/rsync /usr/local/bin/rsync rsync; do
        [[ -n "$candidate" ]] && command -v "$candidate" >/dev/null 2>&1 || continue
        version="$("$candidate" --version 2>/dev/null)" || continue
        if [[ "$version" =~ ^rsync[[:space:]]+version[[:space:]]+3\. ]]; then
            command -v "$candidate"
            return 0
        fi
    done
    return 1
}
# The local VM's disk image grows on the Mac with every build and never
# shrinks by itself, so a short Mac disk fails a build before it starts.
mac_free_kib="$(df -Pk "$repo_root" | awk 'NR == 2 { print $4 }')"
min_mac_free_gib="${WINWISPR_MIN_MAC_FREE_GIB:-15}"
[[ "$mac_free_kib" =~ ^[0-9]+$ && "$min_mac_free_gib" =~ ^[0-9]+$ ]] || die 'could not determine the free space on the Mac'
if ((prune == 0 && cleanup_legacy == 0 && mac_free_kib < min_mac_free_gib * 1024 * 1024)); then
    die "only $((mac_free_kib / 1024 / 1024)) GiB free on the Mac; a VM run needs $min_mac_free_gib GiB (WINWISPR_MIN_MAC_FREE_GIB). Run ./eng/prune-artifacts.sh, or free space on the Mac"
fi

rsync="$(find_rsync)" || die 'rsync 3 not found; install it with brew install rsync or set WINWISPR_RSYNC'
# Keepalives end a stalled connection instead of letting rsync hang on it.
rsync_ssh="ssh -o BatchMode=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=4"

# In a worktree, .git is a file and the metadata lives in the common directory.
git_dir="$(git -C "$repo_root" rev-parse --path-format=absolute --git-dir)"
common_dir="$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)"
# The VM lock (artifacts/.vm-lock in the main checkout) is shared with run-vm-acceptance.sh.
source "$repo_root/eng/vm-lock.sh"

# A reused build passed these checks when it was built from this same tree.
if ((check && !no_build)); then
    phase=local-checks
    "$repo_root/eng/check-local.sh" || die 'local contract checks failed; see ./eng/check-local.sh'
    phase=preflight
fi

working_tree_state=clean
if [[ -n "$(git -C "$repo_root" status --porcelain=v1)" ]]; then
    working_tree_state=dirty
    # The model package is fully determined by its verified, pinned inputs.
    ((check || prune || model_package)) || die 'working tree is dirty; commit changes before deployment'
fi
if ((cleanup_legacy == 0 && check == 0 && prune == 0)); then
    [[ -f "$local_model" ]] || die "bundled model not found: $local_model"
    [[ "$(stat -L -f '%z' "$local_model")" == "$model_bytes" ]] || die 'bundled model size mismatch'
    [[ "$(shasum -a 256 "$local_model" | awk '{print $1}')" == "$model_sha256" ]] || die 'Bundled model SHA-256 mismatch'
    [[ -d "$local_voices" ]] || die "offline voices not found: $local_voices"
    [[ -d "$local_translation_model" ]] || die "translation model not found: $local_translation_model"
fi

run_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-deploy.XXXXXX")"
control_owned=0
leave_control=0
cleanup() {
    local status=$? foreground
    set +e
    trap - ERR
    foreground="$(foreground_lines)"
    if ((${task_created:-0})) && [[ ! -f "$local_result_path" ]]; then
        # A disconnected caller must not remove files still used by its worker.
        leave_control=1
        leave_control_remote=1
        printf 'Worker may still be running; control files preserved: %s\n' "$local_control_root" >&2
    fi
    if declare -F finish_remote >/dev/null 2>&1; then
        finish_remote
    fi
    if ((control_owned && !leave_control)); then
        rm -rf "$local_control_root"
    fi
    release_lock
    rm -rf "$run_dir"
    report_failure "$status" "$foreground"
    trap - EXIT
    exit "$status"
}
trap cleanup EXIT

archive="$run_dir/WinWispr-source.zip"
(
    cd "$repo_root"
    COPYFILE_DISABLE=1 git archive --format=zip HEAD >"$archive"
)
archive_entries="$(unzip -Z1 "$archive")"
if printf '%s\n' "$archive_entries" |
    grep -Eq '(^|/)(\.DS_Store|__MACOSX|\.AppleDouble)(/|$)|(^|/)\._'; then
    die 'source archive contains macOS metadata'
fi

source_commit="$(git -C "$repo_root" rev-parse HEAD)"
archive_sha256="$(shasum -a 256 "$archive" | awk '{print $1}')"
printf 'SOURCE_COMMIT=%s\nSOURCE_ARCHIVE_SHA256=%s\n' "$source_commit" "$archive_sha256"

request_sha256=''
phase=lock
if [[ -e "$local_control_root" || -L "$local_control_root" ]]; then
    die "control directory already exists: $local_control_root"
fi
acquire_lock
mkdir -p "$repo_root/artifacts"
mkdir "$local_control_root" || die 'another VM operation claimed the control directory'
control_owned=1

ps_quote() {
    local value
    value="$(printf '%s' "$1" | sed "s/'/''/g")"
    printf "'%s'" "$value"
}

encode_ps() {
    printf '%s' "$1" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\r\n'
}

# Filter rules for the mirror: whatever Git ignores in this checkout is left
# out, and --delete-excluded removes it from the mirror, so the mirror holds
# exactly what Git sees. The VM's build output under artifacts/ is protected
# instead, and the Mac's artifacts/ is never sent; so are the bin/ and obj/
# directories that builds and tests create in the source tree, which would
# otherwise make every build start from scratch. .git goes along without lock
# files and without the metadata of other worktrees.
write_sync_filter() {
    local path
    printf '%s\n' 'P /artifacts/***' 'H /artifacts/' 'P bin/***' 'P obj/***' '- /.git/**.lock' '- /.git/worktrees/'
    if [[ "$git_dir" != "$common_dir" ]]; then
        # A worktree's .git is a file; the shared .git is sent instead, and
        # this worktree's HEAD and index replace the main checkout's.
        printf '%s\n' '+ /.git/' '- /.git' 'H /.git/HEAD' 'P /.git/HEAD' 'H /.git/index' 'P /.git/index'
    fi
    git -C "$repo_root" ls-files -z --others --ignored --exclude-standard --directory |
        while IFS= read -r -d '' path; do
            [[ "$path" != *$'\n'* ]] || die "cannot mirror a path with a line break: $path"
            # A backslash escapes wildcards once the pattern contains one.
            path="${path//\[/\\[}"
            path="${path//\*/\\*}"
            path="${path//\?/\\?}"
            printf -- '- /%s\n' "$path"
        done
}

# Names the tree the sync sends: every file Git does not ignore, with its size
# and time stamp, but neither .git nor artifacts/. MSBuild compares time stamps, and the mirror
# keeps the Mac's, which every checkout shares; a file edited before another
# checkout's last build looks older than that build's output. So the worker
# keeps a build incremental only when its output was built from this same
# tree, and rebuilds otherwise (see eng/SourceTreeBuild.ps1).
source_tree_sha256() {
    local path
    git -C "$repo_root" ls-files -z --cached --others --exclude-standard | sort -z -u |
        while IFS= read -r -d '' path; do
            # artifacts/ is never sent, and neither is a tracked file deleted in the working tree.
            [[ "$path" != artifacts/* ]] || continue
            [[ -e "$repo_root/$path" || -L "$repo_root/$path" ]] || continue
            [[ "$path" != *$'\n'* ]] || die "cannot mirror a path with a line break: $path"
            printf '%s\0' "$path"
        done | (cd "$repo_root" && xargs -0 stat -f '%Fm %z %N') | shasum -a 256 | awk '{print $1}'
}

stats_field() {
    printf '%s\n' "$2" | sed -n "s/^$1: \([0-9,]*\).*/\1/p" | tr -d ',' | head -n 1
}

# Mirrors this checkout into the VM. Git's object files are read-only, and
# Windows can neither replace nor delete a read-only file, so everything in the
# mirror is created writable. MSYS2 can only fake POSIX modes on NTFS, with the
# executable bit derived from a file's content, and fails to change it, so
# --fake-super keeps the VM's rsync from applying modes at all. An interrupted
# transfer keeps what it received in a partial directory, so the next run
# resumes large files instead of restarting.
# With --no-build the same sync runs as a dry run: it changes nothing and fails
# the run when the mirror no longer holds this tree. Left out of that
# comparison are .git, which other checkouts change all the time, and files only
# the VM has that Git ignores here (the worker's runtime copy and generated test
# fixtures), which every sync deletes and every worker run recreates; emptied
# directories follow from their files.
sync_source() {
    local started output filter="$run_dir/mirror.filter" sources=("$repo_root/") mode=(--stats) changed deleted
    started="$(date +%s)"
    write_sync_filter >"$filter"
    [[ "$git_dir" == "$common_dir" ]] || sources+=("$common_dir")
    ((no_build == 0)) || mode=(--dry-run --itemize-changes)
    if ! output="$("$rsync" --recursive --links --times --chmod=ugo=rwX -M--fake-super --delete --delete-excluded \
        --partial-dir=.rsync-partial --mkpath "${mode[@]}" --filter=". $filter" --rsync-path="$remote_rsync" \
        -e "$rsync_ssh" "${sources[@]}" "$remote_host:$rsync_mirror" 2>&1)"; then
        printf '%s\n' "$output" | tail -n 20 >&2
        die 'could not mirror the checkout to the VM (rsync output above); a new run resumes an interrupted transfer, and rsync must be installed on the VM at C:\windev\tools\msys64'
    fi
    if ((no_build)); then
        # Itemized lines: 11 change flags, a space, the path; "*deleting" lines line up the same way.
        changed="$(printf '%s\n' "$output" | tr -d '\r' |
            awk '/^[<>ch][fdLDS]/ { path = substr($0, 13); if (path !~ /^\.git(\/|$)/) print path }')"
        deleted="$(printf '%s\n' "$output" | tr -d '\r' |
            awk '/^\*deleting/ { path = substr($0, 13); if (path !~ /^\.git(\/|$)/ && path !~ /\/$/) print path }')"
        if [[ -n "$deleted" ]]; then
            deleted="$(printf '%s\n' "$deleted" |
                grep -Fxv -f <(printf '%s\n' "$deleted" | git -C "$repo_root" check-ignore --stdin || true) || true)"
            changed="$(printf '%s\n' "$changed" "$deleted" | sed '/^$/d')"
        fi
        if [[ -n "$changed" ]]; then
            printf 'Mirror differs from this checkout:\n%s\n' "$(printf '%s\n' "$changed" | head -n 20)" >&2
            die "the VM mirror does not hold this checkout's tree ($(printf '%s\n' "$changed" | wc -l | tr -d ' ') paths differ, e.g. $(printf '%s\n' "$changed" | head -n 1)); rebuild with --check instead of --no-build"
        fi
        printf 'Mirror of %s is current; --no-build reuses the last --check build\n' "$repo_root" >&2
        return 0
    fi
    if [[ "$git_dir" != "$common_dir" ]]; then
        "$rsync" --times --chmod=ugo=rwX -M--fake-super --rsync-path="$remote_rsync" -e "$rsync_ssh" \
            "$git_dir/HEAD" "$git_dir/index" "$remote_host:$rsync_mirror.git/" >/dev/null ||
            die "could not mirror this worktree's HEAD and index to the VM"
    fi
    printf 'Mirrored %s in %ss (%s files transferred, %s deleted)\n' "$repo_root" "$(($(date +%s) - started))" \
        "$(stats_field 'Number of regular files transferred' "$output")" "$(stats_field 'Number of deleted files' "$output")" >&2
}

# Copies a directory's content into the mirror, exactly, following links.
send_directory_to_mirror() {
    "$rsync" --recursive --times --copy-links --delete --exclude=.DS_Store --chmod=ugo=rwX -M--fake-super \
        --partial-dir=.rsync-partial --mkpath --rsync-path="$remote_rsync" -e "$rsync_ssh" \
        "$1/" "$remote_host:$rsync_mirror$2/" >/dev/null ||
        die "could not copy $1 to the VM; a new run resumes an interrupted transfer"
}

# Copies one file into the mirror; rsync skips it when it is already there.
send_to_mirror() {
    "$rsync" --times --copy-links --chmod=ugo=rwX -M--fake-super --partial-dir=.rsync-partial --mkpath \
        --rsync-path="$remote_rsync" -e "$rsync_ssh" "$1" "$remote_host:$rsync_mirror$2" >/dev/null ||
        die "could not copy $1 to the VM; a new run resumes an interrupted transfer"
}

task_name="WinWispr-Interactive-$(date +%s)-$$"
task_created=0
remote_control_used=0
remote_result_path="$remote_control_root/worker-$(date +%s)-$$.out"
local_result_path="$local_control_root/$(basename "$remote_result_path")"
progress_lines=0
progress_offset=0

show_progress() {
    local progress_path="$local_result_path.progress" count
    if [[ -f "$progress_path" ]]; then
        count="$(wc -l <"$progress_path" | tr -d ' ')"
        if ((count > progress_lines)); then
            sed -n "$((progress_lines + 1)),${count}p" "$progress_path" >&2
            progress_lines="$count"
        fi
    fi
}

unregister_task() {
    if ((task_created)); then
        ssh "$remote_host" powershell.exe -NoProfile -NonInteractive -EncodedCommand \
            "$(encode_ps "Unregister-ScheduledTask -TaskName $(ps_quote "$task_name") -Confirm:\$false -ErrorAction SilentlyContinue")" \
            >/dev/null 2>&1 || true
        task_created=0
    fi
}

# Removes the task and the control files this run left in the mirror, unless
# the worker may still be running.
finish_remote() {
    unregister_task
    if ((remote_control_used && !leave_control_remote)); then
        ssh "$remote_host" powershell.exe -NoProfile -NonInteractive -EncodedCommand \
            "$(encode_ps "Remove-Item -LiteralPath $(ps_quote "$remote_control_root") -Recurse -Force -ErrorAction SilentlyContinue")" \
            >/dev/null 2>&1 || true
        remote_control_used=0
    fi
}
leave_control_remote=0

phase=sync
if ((cleanup_legacy == 0 && prune == 0)); then
    # Taken before the sync: an edit during the sync can then only cause a rebuild.
    source_tree="$(source_tree_sha256)" && [[ "$source_tree" =~ ^[0-9a-f]{64}$ ]] ||
        die 'could not fingerprint the working tree'
    printf 'SOURCE_TREE_SHA256=%s\n' "$source_tree"
fi
sync_source
if ((cleanup_legacy == 0 && check == 0 && prune == 0)); then
    # The model package build reads these local, ignored inputs from the mirror's artifacts.
    printf 'Copying the speech model, offline voices and translation model to the VM unless they are already there\n' >&2
    send_to_mirror "$local_model" "artifacts/model/$(basename -- "$local_model")"
    send_directory_to_mirror "$local_voices" 'artifacts/voices'
    send_directory_to_mirror "$local_translation_model" 'artifacts/translation-model'
elif ((check && !no_build)) && [[ -f "$local_model" ]]; then
    # The speech acceptance tests derive their model and German clip from this archive and verify it themselves.
    printf 'Copying the bundled model to the VM for the speech acceptance tests unless it is already there\n' >&2
    send_to_mirror "$local_model" "artifacts/model/$(basename -- "$local_model")"
fi

if ((release_mode)); then
    remote_control_used=1
    send_to_mirror "$release_request" "artifacts/.vm-control/WinWispr-release-request.zip"
    request_sha256="$(shasum -a 256 "$release_request" | awk '{print $1}')"
    printf 'RELEASE_REQUEST_SHA256=%s\n' "$request_sha256"
fi

schedule_task() {
    local encoded="$1" result_path="$2"
    local action_arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    local register_command
    register_command="\$ProgressPreference='SilentlyContinue'; \$ErrorActionPreference='Stop'; \$taskName=$(ps_quote "$task_name"); \$resultPath=$(ps_quote "$result_path"); if (Get-ScheduledTask -TaskName \$taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName \$taskName -Confirm:\$false }; \$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $(ps_quote "$action_arguments"); \$trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(5); \$principal=New-ScheduledTaskPrincipal -UserId \$env:USERNAME -LogonType Interactive -RunLevel Limited; Register-ScheduledTask -TaskName \$taskName -Action \$action -Trigger \$trigger -Principal \$principal -Force | Out-Null; Write-Output 'TASK_REGISTERED'; Write-Output ('TASK_RESULT_PATH=' + \$resultPath)"
    task_created=1
    remote_control_used=1
    ssh "$remote_host" powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "$(encode_ps "$register_command")"
}

# One SSH call per poll: the task state, the progress file from the given byte
# offset, and the result file once the worker wrote it. The worker closes its
# progress file before it writes the result, so a result seen before the
# progress is read means the progress is complete. Bytes travel as base64,
# since the SSH console would mangle non-ASCII text.
poll_task() {
    ssh "$remote_host" powershell.exe -NoProfile -NonInteractive -EncodedCommand \
        "$(encode_ps "# WINWISPR_POLL
\$offset = $progress_offset
\$resultPath = $(ps_quote "$remote_result_path")
\$progressPath = $(ps_quote "$remote_result_path.progress")
\$done = Test-Path -LiteralPath \$resultPath
\$task = Get-ScheduledTask -TaskName $(ps_quote "$task_name") -ErrorAction SilentlyContinue
if (\$null -eq \$task) { 'STATE=MISSING' } else { 'STATE=' + \$task.State; 'RESULT=' + (Get-ScheduledTaskInfo -TaskName $(ps_quote "$task_name")).LastTaskResult }
\$complete = \$true
if (Test-Path -LiteralPath \$progressPath) {
    \$stream = [IO.File]::Open(\$progressPath, 'Open', 'Read', 'ReadWrite')
    try {
        \$remaining = \$stream.Length - \$offset
        \$count = [int][Math]::Max(0, [Math]::Min(\$remaining, 8MB))
        \$complete = \$count -eq \$remaining
        if (\$count -gt 0) {
            \$buffer = New-Object byte[] \$count
            \$stream.Position = \$offset
            \$read = 0
            while (\$read -lt \$count) {
                \$n = \$stream.Read(\$buffer, \$read, \$count - \$read)
                if (\$n -le 0) { \$complete = \$false; break }
                \$read += \$n
            }
            'PROGRESS_BYTES=' + [Convert]::ToBase64String(\$buffer, 0, \$read)
        }
    } finally { \$stream.Dispose() }
}
if (\$done -and \$complete) { 'RESULT_FILE=' + [Convert]::ToBase64String([IO.File]::ReadAllBytes(\$resultPath)) }")" \
        2>/dev/null | tr -d '\r'
}

# The result file is what says the worker finished; the task state only hints. On windows-vm the
# task has vanished, or reported Ready with SCHED_S_TASK_RUNNING, while its worker went on and wrote
# a valid result. So an odd task state only fails the run once the worker has also gone quiet.
task_lost_grace="${WINWISPR_TASK_LOST_GRACE_SECONDS:-600}"
[[ "$task_lost_grace" =~ ^[0-9]+$ ]] || die 'WINWISPR_TASK_LOST_GRACE_SECONDS must be a whole number of seconds'

wait_for_result() {
    local deadline=$(( $(date +%s) + 7200 )) output state result chunk result_file
    local last_progress lost_since='' lost_reason=''
    last_progress="$(date +%s)"
    : >"$local_result_path.progress"
    while :; do
        (($(date +%s) < deadline)) || die "interactive Windows task timed out; verify the logged-in interactive session on $remote_host"
        output="$(poll_task || true)"
        state="$(printf '%s\n' "$output" | sed -n 's/^STATE=//p')"
        result="$(printf '%s\n' "$output" | sed -n 's/^RESULT=//p')"
        chunk="$(printf '%s\n' "$output" | sed -n 's/^PROGRESS_BYTES=//p')"
        if [[ -n "$chunk" ]]; then
            printf '%s' "$chunk" | base64 --decode >>"$local_result_path.progress"
            progress_offset="$(wc -c <"$local_result_path.progress" | tr -d ' ')"
            last_progress="$(date +%s)"
        fi
        show_progress
        result_file="$(printf '%s\n' "$output" | sed -n 's/^RESULT_FILE=//p')"
        if [[ -n "$result_file" ]]; then
            printf '%s' "$result_file" | base64 --decode >"$local_result_path"
            break
        fi
        lost_reason=''
        if [[ "$state" == 'MISSING' ]]; then
            lost_reason='interactive Windows task disappeared before writing its result'
        elif [[ "$state" == 'Ready' && -n "$result" && "$result" != '0' && "$result" != '267011' ]]; then
            lost_reason="interactive Windows task failed with LastTaskResult=$result"
        fi
        if [[ -n "$lost_reason" ]]; then
            if [[ -z "$lost_since" ]]; then
                lost_since="$(date +%s)"
                printf '%s; waiting for the worker to write its result\n' "$lost_reason" >&2
            fi
            if (( $(date +%s) - last_progress >= task_lost_grace && $(date +%s) - lost_since >= task_lost_grace )); then
                die "$lost_reason, and the worker wrote nothing for $task_lost_grace s"
            fi
        else
            lost_since=''
        fi
        [[ -n "$chunk" ]] || sleep 3
    done
    show_progress
}

run_interactive_script() {
    local target="$1" result_path="$2" arguments_ps='' argument task_script encoded
    shift 2
    for argument in "$@"; do
        case "$argument" in
            -[A-Za-z]*) arguments_ps+=" $argument" ;;
            *) arguments_ps+=" $(ps_quote "$argument")" ;;
        esac
    done
    task_script="\$ErrorActionPreference = 'Stop'
\$ProgressPreference = 'SilentlyContinue'
\$output = [Collections.Generic.List[string]]::new()
\$progress = \$null
\$exitCode = 1
# The mirror has no control directory until a run creates it.
New-Item -ItemType Directory -Force -Path $(ps_quote "$remote_control_root") | Out-Null
try {
    \$progress = [IO.StreamWriter]::new($(ps_quote "$result_path.progress"), \$false, [Text.UTF8Encoding]::new(\$false))
    \$progress.AutoFlush = \$true
    & $(ps_quote "$target")$arguments_ps *>&1 | ForEach-Object {
        \$line = \$_.ToString()
        \$output.Add(\$line)
        \$progress.WriteLine(\$line)
    }
    \$exitCode = if (\$?) { 0 } else { 1 }
} catch {
    \$output.Add(\$_.Exception.ToString())
}
finally {
    if (\$null -ne \$progress) { \$progress.Dispose() }
    \$output.Add('WORKER_EXIT_CODE=' + \$exitCode)
    # Written under a temporary name so the caller never reads a partial result.
    [IO.File]::WriteAllLines($(ps_quote "$result_path.tmp"), [string[]]\$output, [Text.UTF8Encoding]::new(\$false))
    Move-Item -LiteralPath $(ps_quote "$result_path.tmp") -Destination $(ps_quote "$result_path") -Force
}
exit \$exitCode
"
    encoded="$(encode_ps "$task_script")"
    schedule_task "$encoded" "$result_path" || die 'could not register or start the interactive Windows task'
    printf 'TASK_SCHEDULED=%s\n' "$task_name"
    wait_for_result
    unregister_task
    task_output="$(tr -d '\r' <"$local_result_path")"
    if [[ "$(printf '%s\n' "$task_output" | grep -c '^WORKER_EXIT_CODE=')" != 1 ]] ||
        ! printf '%s\n' "$task_output" | grep -Fxq 'WORKER_EXIT_CODE=0'; then
        printf '%s\n' "$task_output" >&2
        # The worker names its failing phase and the first line of the error.
        worker_failure="$(printf '%s\n' "$task_output" | sed -n 's/^WORKER_FAILURE=//p' | tail -n 1)"
        die "${worker_failure:-interactive Windows worker failed (worker output above)}"
    fi
}

phase=worker
if ((cleanup_legacy)); then
    run_interactive_script "$remote_cleanup" "$remote_result_path"
    printf '%s\n' "$task_output"
    printf '%s\n' "$task_output" | grep -Fq -- 'LEGACY_CLEANUP=PASS' || die 'legacy cleanup did not report LEGACY_CLEANUP=PASS'
    exit 0
fi

if ((prune)); then
    prune_args=(-ArtifactsPath "$remote_mirror/artifacts")
    ((dry_run == 0)) || prune_args+=(-DryRun)
    run_interactive_script "$remote_prune" "$remote_result_path" "${prune_args[@]}"
    printf '%s\n' "$task_output"
    printf '%s\n' "$task_output" | grep -Fxq 'PRUNE=PASS' || die 'VM prune did not report PRUNE=PASS'
    exit 0
fi

worker_args=(-RepositoryPath "$remote_mirror" -SourceCommit "$source_commit" -SourceArchiveSha256 "$archive_sha256"
    -SourceTreeSha256 "$source_tree")
if ((check)); then
    worker_args+=(-Check -WorkingTreeState "$working_tree_state")
    ((no_build == 0)) || worker_args+=(-NoBuild)
    if [[ -n "$test_filter" ]]; then
        worker_args+=(-TestFilter "$test_filter")
    fi
fi
if ((model_package)); then
    worker_args+=(-ModelPackage)
fi
if ((rebuild_runtime)); then
    worker_args+=(-RebuildRuntime)
fi
if ((release_mode)); then
    worker_args+=(-ReleaseRequestPath "$remote_control_root/WinWispr-release-request.zip"
        -ReleaseRequestSha256 "$request_sha256")
    if ((dry_run)); then
        worker_args+=(-DryRun)
    fi
fi

run_interactive_script "$remote_worker" "$remote_result_path" "${worker_args[@]}"
worker_output="$task_output"
phase=result

if ((check)); then
    printf '%s\n' "$worker_output"
    printf '%s\n' "$worker_output" | grep -Fxq 'CHECK=PASS' || die 'VM worker did not report CHECK=PASS'
    exit 0
fi

if ((release_mode)); then
    marker_value() {
        local name="$1" count value
        count="$(printf '%s\n' "$worker_output" | grep -Ec "^$name=" || true)"
        if [[ "$count" != '1' ]]; then
            printf '%s\n' "$worker_output" | tail -40 >&2
            die "VM worker marker $name must appear exactly once (found $count)"
        fi
        value="$(printf '%s\n' "$worker_output" | sed -n "s/^$name=//p")"
        [[ -n "$value" ]] || die "VM worker marker $name is empty"
        printf '%s\n' "$value"
    }

    release_version="$(marker_value RELEASE_VERSION)"
    [[ "$release_version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || die "invalid RELEASE_VERSION: $release_version"
    if ((dry_run)); then
        printf 'RELEASE_VERSION=%s\n' "$release_version"
        exit 0
    fi

    release_archive_remote="$(marker_value RELEASE_ARCHIVE)"
    release_sha256="$(marker_value RELEASE_SHA256)"
    app_running="$(marker_value APP_RUNNING)"
    verify="$(marker_value VERIFY)"
    [[ "$app_running" == '1' ]] || die 'VM worker did not report APP_RUNNING=1'
    [[ "$verify" == 'PASS' ]] || die 'VM worker did not report VERIFY=PASS'
    [[ "$release_sha256" =~ ^[0-9a-f]{64}$ ]] || die "invalid RELEASE_SHA256: $release_sha256"

    normalized_release_archive="${release_archive_remote//\\//}"
    expected_basename="WinWispr-release-v$release_version.zip"
    expected_remote_archive="$remote_control_root/release/$expected_basename"
    [[ "$normalized_release_archive" == "$expected_remote_archive" ]] || die "unexpected release archive path: $release_archive_remote"
    local_release_archive="$local_control_root/release/$expected_basename"
    local_release_sidecar="$local_release_archive.sha256"
    # The release evidence is the only output that comes back from the VM.
    "$rsync" --recursive --times --chmod=u=rwX,go=rX --rsync-path="$remote_rsync" -e "$rsync_ssh" \
        "$remote_host:${rsync_mirror}artifacts/.vm-control/release/" "$local_control_root/release/" >/dev/null ||
        die 'could not copy the release evidence back from the VM'
    [[ -f "$local_release_archive" && -f "$local_release_sidecar" ]] || die 'release evidence was not copied back from the VM'
    local_release_hash="$(shasum -a 256 "$local_release_archive" | awk '{print $1}')"
    [[ "$local_release_hash" == "$release_sha256" ]] || die "release evidence hash mismatch: expected $release_sha256, got $local_release_hash"

    printf '%s\n' "$worker_output" | grep -Ev '^(RELEASE_VERSION|RELEASE_ARCHIVE|RELEASE_SHA256|APP_RUNNING|VERIFY)=' || true
    printf 'RELEASE_VERSION=%s\nRELEASE_ARCHIVE=%s\nRELEASE_SHA256=%s\nAPP_RUNNING=1\nVERIFY=PASS\n' \
        "$release_version" "$local_release_archive" "$release_sha256"
    leave_control=1
    exit 0
fi

if ((model_package)); then
    printf '%s\n' "$worker_output"
    printf '%s\n' "$worker_output" | grep -Fxq 'VERIFY=PASS' || die 'VM worker did not report VERIFY=PASS'
    package_remote="$(printf '%s\n' "$worker_output" | sed -n 's/^MODEL_PACKAGE=//p' | tail -n 1)"
    package_sha256="$(printf '%s\n' "$worker_output" | sed -n 's/^MODEL_PACKAGE_SHA256=//p' | tail -n 1)"
    package_name="$(basename -- "${package_remote//\\//}")"
    [[ "$package_name" =~ ^WinWispr-Models-v[1-9][0-9]*\.exe$ && "$package_sha256" =~ ^[0-9a-f]{64}$ ]] ||
        die 'VM worker did not report MODEL_PACKAGE and MODEL_PACKAGE_SHA256'
    # Besides the release evidence, the model package is the only output that comes back from the VM.
    mkdir -p "$repo_root/artifacts/models"
    "$rsync" --times --chmod=u=rw,go=r --partial-dir=.rsync-partial --rsync-path="$remote_rsync" -e "$rsync_ssh" \
        "$remote_host:${rsync_mirror}artifacts/models/$package_name" "$remote_host:${rsync_mirror}artifacts/models/$package_name.sha256" \
        "$repo_root/artifacts/models/" >/dev/null || die 'could not copy the model package back from the VM'
    local_package_hash="$(shasum -a 256 "$repo_root/artifacts/models/$package_name" | awk '{print $1}')"
    [[ "$local_package_hash" == "$package_sha256" ]] || die "model package hash mismatch: expected $package_sha256, got $local_package_hash"
    printf 'MODEL_PACKAGE_LOCAL=%s\nSTATUS=PASS\n' "$repo_root/artifacts/models/$package_name"
    exit 0
fi

printf '%s\n' "$worker_output"
printf '%s\n' "$worker_output" | grep -Fq -- 'VERIFY=PASS' || die 'VM worker did not report VERIFY=PASS'

printf 'STATUS=PASS\n'
