# Sourced by the eng scripts that use windows-vm (deploy-windows-vm.sh,
# run-vm-acceptance.sh). All checkouts share one mirror on the VM, so the lock
# lives in the main checkout at artifacts/.vm-lock; in a worktree, .git is a
# file and the metadata lives there too.
#
# The caller defines repo_root and die(); this file defines lock_dir,
# lock_owned, acquire_lock and release_lock. The caller's EXIT trap calls
# release_lock.

lock_dir="$(dirname -- "$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)")/artifacts/.vm-lock"
lock_owned=0

# A lock whose owner has exited is stale; a live owner fails this run fast.
acquire_lock() {
    local owner_pid
    mkdir -p "$(dirname -- "$lock_dir")"
    if ! mkdir "$lock_dir" 2>/dev/null; then
        owner_pid="$(sed -n 's/^PID=//p' "$lock_dir/owner" 2>/dev/null || true)"
        if [[ -n "$owner_pid" ]] && ! kill -0 "$owner_pid" 2>/dev/null; then
            rm -rf "$lock_dir"
            mkdir "$lock_dir" 2>/dev/null || die "another VM operation claimed $lock_dir"
        else
            die "another VM operation holds $lock_dir: $(tr '\n' ' ' <"$lock_dir/owner" 2>/dev/null || true)"
        fi
    fi
    lock_owned=1
    printf 'PID=%s\nCHECKOUT=%s\nSTARTED=%s\n' "$$" "$repo_root" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$lock_dir/owner"
}

release_lock() {
    if ((lock_owned)); then
        rm -rf "$lock_dir"
        lock_owned=0
    fi
}
