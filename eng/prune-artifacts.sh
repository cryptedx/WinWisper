#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
artifacts="$repo_root/artifacts"
keep_releases=3
keep_packages=2
max_age_days=14
dry_run=0
assume_release=''

usage() {
    cat <<'EOF'
Usage: ./eng/prune-artifacts.sh [--dry-run] [--assume-release MAJOR.MINOR.PATCH]

Keeps this checkout's artifacts/ bounded; release-local.sh runs it after every
release, and ./eng/deploy-windows-vm.sh --prune does the same on the VM.
Removes only what it recognizes: release evidence beyond the newest three
versions, versioned packages beyond the newest two per kind (with their
.sha256 sidecars), WinWispr Models installers older than the newest, an unversioned alias (WinWispr-Setup.exe,
WinWispr-portable-current.zip) that duplicates a versioned package, orphaned
sidecars, stale temporary packages, build output in dev-check (bin, obj),
dev-check entries without a change in 14 days, and leftovers without a change
in 14 days: the retired teams-build directory and runtime-cache entries the
VM worker does not use. The model, voices, the translation model, the runtime
cache's win-x64 and build-script.sha256, and the VM control and lock
directories stay.
--dry-run lists what would be removed.
--assume-release counts a release that is about to be added (not yet below
artifacts/releases) as the newest one, so a dry run before a release lists
exactly what the prune after it will remove. release-local.sh --dry-run uses it.
EOF
}

die() {
    printf 'prune-artifacts: %s\n' "$1" >&2
    exit 1
}

while (($# > 0)); do
    case "$1" in
        --dry-run) dry_run=1 ;;
        --assume-release)
            shift
            (($# > 0)) || die '--assume-release needs a MAJOR.MINOR.PATCH version'
            [[ "$1" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || die "invalid --assume-release version: $1"
            assume_release="$1"
            ;;
        --help|-h) usage; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
    shift
done

freed_kib=0
removed=0

remove() {
    local path="$1" reason="$2" kib
    kib="$(du -sk -- "$path" | awk '{ print $1 }')"
    if ((dry_run)); then
        printf 'PRUNE_WOULD_REMOVE=%s (%s MB, %s)\n' "$path" "$((kib / 1024))" "$reason"
    else
        rm -rf -- "$path"
        printf 'PRUNE_REMOVED=%s (%s MB, %s)\n' "$path" "$((kib / 1024))" "$reason"
    fi
    freed_kib=$((freed_kib + kib))
    removed=$((removed + 1))
}

# True when nothing in the tree changed within the given number of days.
is_stale() {
    [[ -z "$(find "$1" -mtime "-$2" -print 2>/dev/null | head -n 1)" ]]
}

# Keeps the newest versions of "<prefix><MAJOR.MINOR.PATCH><suffix>" in a
# directory; a removed item's .sha256 sidecar goes with it. A fifth argument
# reserves that many of the kept slots for items that are about to be added.
keep_newest() {
    local dir="$1" prefix="$2" suffix="$3" keep="$4" reserve="${5:-0}" path name version reason
    [[ -d "$dir" && ! -L "$dir" ]] || return 0
    reason="older than the newest $keep"
    if ((reserve > 0)); then
        reason="$reason (the release being added counts as one)"
        keep=$((keep > reserve ? keep - reserve : 0))
    fi
    while IFS= read -r version; do
        remove "$dir/$prefix$version$suffix" "$reason"
        [[ ! -f "$dir/$prefix$version$suffix.sha256" ]] ||
            remove "$dir/$prefix$version$suffix.sha256" 'sidecar of a removed package'
    done < <(
        for path in "$dir/$prefix"*"$suffix"; do
            name="${path##*/}"
            version="${name#"$prefix"}"
            version="${version%"$suffix"}"
            [[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] && printf '%s\n' "$version"
        done | sort -t. -k1,1nr -k2,2nr -k3,3nr | tail -n "+$((keep + 1))"
    )
}

# An unversioned alias that is byte-identical to a versioned package of the
# same kind holds no build of its own; nothing on the Mac reads it.
remove_duplicate_alias() {
    local dir="$1" alias="$2" pattern="$3" package
    [[ -d "$dir" && ! -L "$dir" && -f "$dir/$alias" && ! -L "$dir/$alias" ]] || return 0
    for package in "$dir"/$pattern; do
        [[ -f "$package" && "$package" != "$dir/$alias" ]] || continue
        if cmp -s -- "$dir/$alias" "$package"; then
            remove "$dir/$alias" "duplicate of ${package##*/}"
            [[ ! -f "$dir/$alias.sha256" ]] || remove "$dir/$alias.sha256" 'sidecar of a removed package'
            return 0
        fi
    done
}

remove_orphaned_sidecars() {
    local dir="$1" sidecar
    [[ -d "$dir" && ! -L "$dir" ]] || return 0
    for sidecar in "$dir"/*.sha256; do
        [[ -f "$sidecar" && ! -e "${sidecar%.sha256}" ]] || continue
        remove "$sidecar" 'sidecar without its package'
    done
}

if [[ -d "$artifacts" ]]; then
    # Before the version cut, so a dry run lists exactly what a real run removes.
    remove_duplicate_alias "$artifacts/installer" 'WinWispr-Setup.exe' 'WinWispr-Setup-v*.exe'
    remove_duplicate_alias "$artifacts/portable" 'WinWispr-portable-current.zip' 'WinWispr-portable-v*.zip'
    # The assumed release is added after the preview, so it takes one slot unless it is already there.
    reserve=0
    [[ -z "$assume_release" || -e "$artifacts/releases/v$assume_release" ]] || reserve=1
    keep_newest "$artifacts/releases" 'v' '' "$keep_releases" "$reserve"
    for kind in Setup Update; do
        keep_newest "$artifacts/installer" "WinWispr-$kind-v" '.exe' "$keep_packages"
    done
    for kind in portable update; do
        keep_newest "$artifacts/portable" "WinWispr-$kind-v" '.zip' "$keep_packages"
    done
    for path in "$artifacts"/portable/WinWispr-*-tmp-*.zip "$artifacts"/portable/WinWispr-*-tmp-*.zip.sha256; do
        [[ -f "$path" ]] && is_stale "$path" 1 && remove "$path" 'stale temporary package'
    done
    # The newest WinWispr Models installer is what ./eng/publish-models.sh publishes; older ones are not.
    model_packages="$(for path in "$artifacts"/models/WinWispr-Models-v*.exe; do
        name="${path##*/}"; version="${name#WinWispr-Models-v}"; version="${version%.exe}"
        if [[ -f "$path" && "$version" =~ ^[1-9][0-9]*$ ]]; then printf '%s\n' "$version"; fi
    done | sort -nr | tail -n +2)"
    for version in $model_packages; do
        remove "$artifacts/models/WinWispr-Models-v$version.exe" 'older than the newest model package'
        [[ ! -f "$artifacts/models/WinWispr-Models-v$version.exe.sha256" ]] ||
            remove "$artifacts/models/WinWispr-Models-v$version.exe.sha256" 'sidecar of a removed package'
    done
    remove_orphaned_sidecars "$artifacts/installer"
    remove_orphaned_sidecars "$artifacts/portable"
    remove_orphaned_sidecars "$artifacts/models"
    # Nothing builds on the Mac, so dev-check holds copied logs and scratch only;
    # build output that reached it is never needed here.
    for path in "$artifacts"/dev-check/bin "$artifacts"/dev-check/obj; do
        [[ -e "$path" && ! -L "$path" ]] && remove "$path" 'build output on the Mac'
    done
    for path in "$artifacts"/dev-check/*; do
        [[ -e "$path" ]] && is_stale "$path" "$max_age_days" && remove "$path" "no change in $max_age_days days"
    done
    # Retired: no script builds or reads teams-build any more.
    path="$artifacts/teams-build"
    [[ -d "$path" && ! -L "$path" ]] && is_stale "$path" "$max_age_days" && remove "$path" "retired, no change in $max_age_days days"
    # The VM worker uses only these two runtime-cache entries.
    for path in "$artifacts"/runtime-cache/*; do
        [[ -e "$path" ]] || continue
        case "${path##*/}" in win-x64|build-script.sha256) continue ;; esac
        is_stale "$path" "$max_age_days" && remove "$path" "unused runtime-cache entry, no change in $max_age_days days"
    done
fi

[[ -z "$assume_release" ]] || printf 'PRUNE_ASSUMED_RELEASE=v%s\n' "$assume_release"
printf 'PRUNE_FREED_MB=%s\nPRUNE_ITEMS=%s\nPRUNE_MODE=%s\nPRUNE=PASS\n' \
    "$((freed_kib / 1024))" "$removed" "$([[ "$dry_run" == 1 ]] && echo dry-run || echo remove)"
