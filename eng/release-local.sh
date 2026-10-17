#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
mode=''

usage() {
    cat <<'EOF'
Usage: ./eng/release-local.sh --dry-run
       ./eng/release-local.sh --self-signed

Runs the local Mac-controlled WinWispr release transaction. The Windows VM
builds, signs and verifies; the Mac validates the evidence and creates the
local tag last. --self-signed signs with WinWispr's own certificate
(eng/signing) and creates internal evidence only, enterpriseReady=false.
EOF
}

# The failing phase and reason end every non-zero exit as the last stderr line
# (see cleanup): FAILED: <phase>: <one-line reason>.
phase='startup'
failure_reason=''
reason_file=''

die() {
    printf 'release-local: %s\n' "$1" >&2
    failure_reason="$1"
    # die also runs inside $(...) subshells; the file carries the reason out.
    if [ -n "$reason_file" ]; then
        printf '%s' "$1" >"$reason_file" 2>/dev/null || true
    fi
    exit 1
}

need_tool() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

clean_tree() {
    [ -z "$("${git_cmd[@]}" status --porcelain=v1)" ]
}

require_clean_tree() {
    clean_tree || die 'working tree is dirty; commit changes before release'
}

strict_semver_tag() {
    case "$1" in
        v0.0.0|v[1-9]*.[0-9]*.[0-9]*|v0.[0-9]*.[0-9]*)
            printf '%s\n' "$1" | rg -q '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
            ;;
        *) return 1 ;;
    esac
}

version_parts() {
    local version="${1#v}"
    printf '%s %s %s\n' "${version%%.*}" "$(printf '%s' "${version#*.}" | cut -d. -f1)" "${version##*.}"
}

version_gt() {
    local a_major a_minor a_patch b_major b_minor b_patch
    set -- $(version_parts "$1") $(version_parts "$2")
    a_major="$1"; a_minor="$2"; a_patch="$3"; b_major="$4"; b_minor="$5"; b_patch="$6"
    [ "$a_major" -gt "$b_major" ] ||
        { [ "$a_major" -eq "$b_major" ] && [ "$a_minor" -gt "$b_minor" ]; } ||
        { [ "$a_major" -eq "$b_major" ] && [ "$a_minor" -eq "$b_minor" ] && [ "$a_patch" -gt "$b_patch" ]; }
}

highest_reachable_tag() {
    local head="$1" tag type best=''
    while IFS=' ' read -r tag type; do
        [ -n "$tag" ] || continue
        strict_semver_tag "$tag" || continue
        [ "$type" = 'tag' ] || continue
        if "${git_cmd[@]}" merge-base --is-ancestor "$tag^{}" "$head"; then
            if [ -z "$best" ] || version_gt "$tag" "$best"; then
                best="$tag"
            fi
        fi
    done < <("${git_cmd[@]}" for-each-ref refs/tags --format='%(refname:short) %(objecttype)')
    [ -n "$best" ] || return 1
    printf '%s\n' "$best"
}

possible_next_tags() {
    local tag="$1" major minor patch
    set -- $(version_parts "$tag")
    major="$1"; minor="$2"; patch="$3"
    printf 'v%s.%s.%s\n' "$major" "$minor" "$((patch + 1))"
    printf 'v%s.%s.0\n' "$major" "$((minor + 1))"
    printf 'v%s.0.0\n' "$((major + 1))"
}

validate_no_reserved_targets() {
    local tag output_root="$repo_root/artifacts/releases"
    while IFS= read -r tag; do
        [ -n "$tag" ] || continue
        if "${git_cmd[@]}" rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
            die "release tag already exists: $tag"
        fi
        if [ -e "$output_root/$tag" ]; then
            die "release directory already exists: $output_root/$tag"
        fi
    done < <(possible_next_tags "$1")
}

single_marker() {
    local name="$1" file="$2" count value
    count="$(LC_ALL=C rg -c "^$name=" "$file" || true)"
    [ "$count" = '1' ] || die "transport marker $name must appear exactly once"
    value="$(LC_ALL=C sed -n "s/^$name=//p" "$file")"
    [ -n "$value" ] || die "transport marker $name is empty"
    printf '%s\n' "$value"
}

write_request() {
    local previous_tag="$1" head="$2" run_dir="$3" log_file="$run_dir/git-log.z" commits_file="$run_dir/commits.rs"
    local count records=0 record
    "${git_cmd[@]}" log -z --reverse --format=%B "$previous_tag..$head" >"$log_file"
    count="$("${git_cmd[@]}" rev-list --count "$previous_tag..$head")"
    [ "$count" -gt 0 ] || die 'release commit range is empty'
    : >"$commits_file"
    while IFS= read -r -d '' record; do
        records=$((records + 1))
        if ! printf '%s' "$record" | rg -q '[^[:space:]]'; then
            die 'commit message record must not be empty or whitespace'
        fi
        if printf '%s' "$record" | LC_ALL=C rg -q "$(printf '\036')"; then
            die 'commit message record contains ASCII RS'
        fi
        if [ "$records" -gt 1 ]; then
            printf '\036' >>"$commits_file"
        fi
        printf '%s' "$record" >>"$commits_file"
    done <"$log_file"
    [ "$records" = "$count" ] || die "commit record count mismatch: expected $count, got $records"

    printf '%s\n' "$previous_tag" >"$run_dir/previous-tag.txt"
    printf '%s\n' "$head" >"$run_dir/source-commit.txt"
    (
        cd "$run_dir"
        zip -q -X release-request.zip previous-tag.txt source-commit.txt commits.rs
    )
    local entries
    entries="$(zipinfo -1 "$run_dir/release-request.zip" | LC_ALL=C sort | tr '\n' ' ')"
    [ "$entries" = 'commits.rs previous-tag.txt source-commit.txt ' ] || die "bad release request layout: $entries"
    printf '%s\n' "$run_dir/release-request.zip"
}

safe_zip_name() {
    local name="$1"
    [ -n "$name" ] || return 1
    if printf '%s' "$name" | LC_ALL=C rg -q '[[:cntrl:]]'; then
        return 1
    fi
    case "$name" in
        /*|*'\'*|../*|*/../*|*/..|..|*$'\r'*|*$'\n'*|*$'\t'*) return 1 ;;
        .DS_Store|*/.DS_Store|__MACOSX/*|*/__MACOSX/*|.AppleDouble|*/.AppleDouble|*.AppleDouble/*|._*|*/._*) return 1 ;;
        *) return 0 ;;
    esac
}

allowed_pack_name() {
    local name="$1" tag="$2"
    case "$name" in
        SBOM.cdx.json|VM-ACCEPTANCE.txt|SECURITY-REPORT.md|THIRD-PARTY-NOTICES.md|BUILD-INFO.json|SHA256SUMS.txt) return 0 ;;
        "WinWispr-portable-$tag.zip"|"WinWispr-portable-$tag.zip.sha256"|"WinWispr-Setup-$tag.exe"|"WinWispr-Setup-$tag.exe.sha256"|"CHANGELOG-$tag.md") return 0 ;;
        logs/*) [ "${name#logs/}" != "$name" ] && [ -n "${name#logs/}" ] && return 0 ;;
        *) return 1 ;;
    esac
}

validate_zip_entries() {
    local archive="$1" tag="$2" entries="$3" duplicates
    zipinfo -1 "$archive" >"$entries"
    duplicates="$(LC_ALL=C sort "$entries" | uniq -d)"
    [ -z "$duplicates" ] || die "release ZIP contains duplicate entries: $duplicates"
    while IFS= read -r name; do
        safe_zip_name "$name" || die "unsafe release ZIP entry: $name"
        case "$name" in */) die "release ZIP must not contain directory entries: $name" ;; esac
        allowed_pack_name "$name" "$tag" || die "unexpected release ZIP entry: $name"
    done <"$entries"
    for required in \
        "WinWispr-portable-$tag.zip" "WinWispr-portable-$tag.zip.sha256" \
        "WinWispr-Setup-$tag.exe" "WinWispr-Setup-$tag.exe.sha256" \
        SBOM.cdx.json VM-ACCEPTANCE.txt "CHANGELOG-$tag.md" SECURITY-REPORT.md \
        THIRD-PARTY-NOTICES.md BUILD-INFO.json SHA256SUMS.txt; do
        rg -qx --fixed-strings "$required" "$entries" || die "release ZIP missing entry: $required"
    done
    if zipinfo -l "$archive" | awk 'NR > 3 && $1 ~ /^l/ { found=1 } END { exit found ? 0 : 1 }'; then
        die 'release ZIP contains a symlink entry'
    fi
}

read_sidecar_hash() {
    local sidecar="$1" basename="$2" line hash file line_count
    line_count="$(sed -n '$=' "$sidecar")"
    [ "$line_count" = '1' ] || die "sidecar must contain exactly one line: $sidecar"
    IFS= read -r line <"$sidecar" || die "empty sidecar: $sidecar"
    line="${line%$'\r'}"
    hash="${line%% *}"
    file="${line#* }"
    file="${file#\*}"
    [ "$hash" != "$line" ] || die "invalid sidecar format: $sidecar"
    printf '%s' "$hash" | rg -q '^[0-9a-f]{64}$' || die "invalid sidecar hash: $sidecar"
    [ "$file" = "$basename" ] || die "sidecar references unexpected file: $file"
    printf '%s\n' "$hash"
}

validate_sha256sums() {
    local release_dir="$1" tag="$2" line hash path
    local sums="$release_dir/SHA256SUMS.txt"
    local listed="$release_dir/.sha256-listed" actual="$release_dir/.sha256-actual" normalized="$release_dir/.sha256-normalized"
    : >"$listed"
    : >"$normalized"
    while IFS= read -r line; do
        line="${line%$'\r'}"
        hash="${line%% *}"
        path="${line#* }"
        path="${path#\*}"
        printf '%s' "$hash" | rg -q '^[0-9a-f]{64}$' || die "invalid SHA256SUMS hash: $line"
        [ "$path" != "$line" ] || die "invalid SHA256SUMS line: $line"
        safe_zip_name "$path" || die "unsafe SHA256SUMS path: $path"
        allowed_pack_name "$path" "$tag" || die "unexpected SHA256SUMS path: $path"
        printf '%s\n' "$path" >>"$listed"
        printf '%s\n' "$line" >>"$normalized"
    done <"$sums"
    (
        cd "$release_dir"
        find . -type f ! -name SHA256SUMS.txt ! -name .sha256-listed ! -name .sha256-actual ! -name .sha256-normalized |
            sed 's#^\./##' |
            sort
    ) >"$actual"
    sort "$listed" -o "$listed"
    cmp -s "$listed" "$actual" || die 'SHA256SUMS does not cover the extracted release files exactly'
    rm -f "$listed" "$actual"
    (cd "$release_dir" && shasum -a 256 -c .sha256-normalized >/dev/null) || die 'inner SHA256SUMS verification failed'
    rm -f "$normalized"
}

plutil_raw() {
    plutil -extract "$1" raw -o - "$2" 2>/dev/null
}

validate_build_info() {
    local file="$1" version="$2" previous_tag="$3" head="$4" source_archive_sha="$5"
    [ "$(plutil_raw version "$file")" = "$version" ] || die 'BUILD-INFO version mismatch'
    [ "$(plutil_raw tag "$file")" = "v$version" ] || die 'BUILD-INFO tag mismatch'
    [ "$(plutil_raw previousTag "$file")" = "$previous_tag" ] || die 'BUILD-INFO previousTag mismatch'
    [ "$(plutil_raw sourceCommit "$file")" = "$head" ] || die 'BUILD-INFO sourceCommit mismatch'
    [ "$(plutil_raw sourceArchiveSha256 "$file")" = "$source_archive_sha" ] || die 'BUILD-INFO source archive SHA-256 mismatch'
    case "$(plutil_raw vmVerified "$file")" in true|1) ;; *) die 'BUILD-INFO vmVerified mismatch' ;; esac
    case "$(plutil_raw enterpriseReady "$file")" in false|0) ;; *) die 'BUILD-INFO enterpriseReady mismatch' ;; esac
    [ "$(plutil_raw signingThumbprint "$file")" = "$(plutil_raw thumbprint "$repo_root/eng/signing/code-signing.json")" ] ||
        die 'BUILD-INFO signingThumbprint does not match eng/signing/code-signing.json'
    plutil_raw modelPackage "$file" | rg -q '^WinWispr-Models-v[1-9][0-9]*\.exe$' || die 'BUILD-INFO modelPackage mismatch'
}

exact_crlf_line_count() {
    local file="$1" expected="$2" line count=0 line_number=0
    while IFS= read -r line; do
        line_number=$((line_number + 1))
        line="${line%$'\r'}"
        if [ "$line_number" = 1 ]; then
            line="${line#$'\xef\xbb\xbf'}"
        fi
        if [ "$line" = "$expected" ]; then
            count=$((count + 1))
        fi
    done <"$file"
    printf '%s\n' "$count"
}

crlf_line_prefix_count() {
    local file="$1" prefix="$2" line count=0 line_number=0
    while IFS= read -r line; do
        line_number=$((line_number + 1))
        line="${line%$'\r'}"
        if [ "$line_number" = 1 ]; then
            line="${line#$'\xef\xbb\xbf'}"
        fi
        case "$line" in
            "$prefix"*) count=$((count + 1)) ;;
        esac
    done <"$file"
    printf '%s\n' "$count"
}

require_exact_crlf_line() {
    local file="$1" expected="$2" name="$3" count
    count="$(exact_crlf_line_count "$file" "$expected")"
    [ "$count" = 1 ] || die "$name count mismatch for $expected: $count"
}

require_exact_key_value() {
    local file="$1" key="$2" value="$3" name="$4" key_count value_count
    key_count="$(crlf_line_prefix_count "$file" "$key=")"
    value_count="$(exact_crlf_line_count "$file" "$key=$value")"
    [ "$key_count" = 1 ] && [ "$value_count" = 1 ] || die "$name key/value count mismatch for $key=$value: key=$key_count value=$value_count"
}

# The VM acceptance installs per user, so the path holds the VM account's name; any single account is accepted.
require_per_user_install_path() {
    local file="$1" line value account line_number=0
    local prefix='C:\Users\' suffix='\AppData\Local\Programs\WinWispr\WinWispr.exe'
    [ "$(crlf_line_prefix_count "$file" 'INSTALL_PATH=')" = 1 ] || die 'VM-ACCEPTANCE INSTALL_PATH count mismatch'
    while IFS= read -r line; do
        line_number=$((line_number + 1))
        line="${line%$'\r'}"
        if [ "$line_number" = 1 ]; then
            line="${line#$'\xef\xbb\xbf'}"
        fi
        case "$line" in
            INSTALL_PATH=*) value="${line#INSTALL_PATH=}" ;;
        esac
    done <"$file"
    account="${value#"$prefix"}"
    [ "$account" != "$value" ] || die "VM-ACCEPTANCE INSTALL_PATH is not per user: $value"
    account="${account%"$suffix"}"
    [ "$prefix$account$suffix" = "$value" ] && [ -n "$account" ] && [[ "$account" != *'\'* ]] ||
        die "VM-ACCEPTANCE INSTALL_PATH is not per user: $value"
}

require_exact_one_app_data_state() {
    local file="$1" preserved_count not_present_count app_data_count total
    preserved_count="$(exact_crlf_line_count "$file" 'APP_DATA=preserved')"
    not_present_count="$(exact_crlf_line_count "$file" 'APP_DATA=not-present')"
    app_data_count="$(crlf_line_prefix_count "$file" 'APP_DATA=')"
    total=$((preserved_count + not_present_count))
    [ "$total" = 1 ] && [ "$app_data_count" = 1 ] || die "VM-ACCEPTANCE APP_DATA state count mismatch: total=$app_data_count preserved=$preserved_count not-present=$not_present_count"
}

validate_vm_acceptance() {
    local file="$1"
    require_exact_key_value "$file" 'INTERACTIVE_SESSION' '1' 'VM-ACCEPTANCE'
    require_exact_key_value "$file" 'APP_RUNNING' '1' 'VM-ACCEPTANCE'
    require_exact_one_app_data_state "$file"
    require_exact_key_value "$file" 'VERIFY' 'PASS' 'VM-ACCEPTANCE'
    require_per_user_install_path "$file"
}

validate_changelog() {
    local file="$1" tag="$2" previous_tag="$3" head="$4"
    require_exact_crlf_line "$file" "# WinWispr $tag" 'CHANGELOG title mismatch'
    require_exact_crlf_line "$file" "Previous tag: $previous_tag" 'CHANGELOG previous tag mismatch'
    require_exact_crlf_line "$file" "Source commit: $head" 'CHANGELOG source commit mismatch'
}

validate_security_report() {
    local file="$1"
    require_exact_crlf_line "$file" 'enterpriseReady=false' 'SECURITY-REPORT enterpriseReady=false count mismatch'
    require_exact_crlf_line "$file" 'vmVerified=true' 'SECURITY-REPORT vmVerified=true count mismatch'
}

cleanup_run_dir=''
release_control_dir=''
promoted_dir=''
staging_dir=''
claim_dir=''
owner_token=''

tag_is_annotated_on_head() {
    local release_tag="${tag:-}" head="${head_before:-}"
    [ -n "$release_tag" ] || return 1
    [ -n "$head" ] || return 1
    [ "$("${git_cmd[@]}" cat-file -t "refs/tags/$release_tag" 2>/dev/null || true)" = 'tag' ] || return 1
    [ "$("${git_cmd[@]}" rev-parse -q --verify "refs/tags/$release_tag^{}" 2>/dev/null || true)" = "$head" ]
}

remove_owned_promoted() {
    if [ -z "$promoted_dir" ] || [ -z "$owner_token" ] || [ ! -d "$promoted_dir" ]; then
        return
    fi
    if [ -f "$promoted_dir/.winwispr-release-owner" ] &&
        [ "$(cat "$promoted_dir/.winwispr-release-owner")" = "$owner_token" ]; then
        if tag_is_annotated_on_head; then
            rm -f "$promoted_dir/.winwispr-release-owner"
            promoted_dir=''
            return
        fi
        rm -rf "$promoted_dir"
    fi
}

failed_command=''
trap 'failed_command="$BASH_COMMAND"' ERR

# Keeps the captured transport output (the run directory is removed on exit)
# and prints the foreground-window lines found in it, so a failed desktop run
# names its cause right above the FAILED line.
report_transport_failure() {
    local stamp log_dir="$repo_root/artifacts/dev-check" saved
    [ -s "${transport_output:-}" ] || [ -s "${transport_errors:-}" ] || return 0
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    mkdir -p "$log_dir" 2>/dev/null || return 0
    saved="$log_dir/release-local-$stamp.log"
    {
        printf '== deploy-windows-vm.sh stdout ==\n'
        cat "${transport_output:-/dev/null}"
        if [ -s "${transport_errors:-}" ]; then
            printf '\n== deploy-windows-vm.sh stderr ==\n'
            cat "$transport_errors"
        fi
    } >"$saved" 2>/dev/null || return 0
    printf 'release-local: full log: %s\n' "$saved" >&2
    awk '/foreground window at failure:|Foreground:/ && !seen[$0]++' "$saved" >&2
}

cleanup() {
    local status=$? reason=''
    set +e
    if [ "$status" -ne 0 ]; then
        reason="$failure_reason"
        if [ -z "$reason" ] && [ -n "$reason_file" ] && [ -s "$reason_file" ]; then
            reason="$(cat "$reason_file")"
        fi
        if [ -z "$reason" ]; then
            if [ "$status" -eq 143 ]; then
                reason='interrupted by a signal (exit 143)'
            elif [ -n "$failed_command" ]; then
                reason="command failed with exit $status: $failed_command"
            else
                reason="exited with status $status"
            fi
        fi
        report_transport_failure
    fi
    remove_owned_promoted
    if [ -n "$staging_dir" ] && [ -d "$staging_dir" ]; then
        rm -rf "$staging_dir"
    fi
    if [ -n "$claim_dir" ] && [ -d "$claim_dir" ]; then
        rm -rf "$claim_dir"
    fi
    if [ -n "$cleanup_run_dir" ] && [ -d "$cleanup_run_dir" ]; then
        rm -rf "$cleanup_run_dir"
    fi
    if [ "$release_control_dir" = "$repo_root/artifacts/.vm-control" ] && [ -d "$release_control_dir" ]; then
        rm -rf "$release_control_dir"
    fi
    trap - EXIT
    if [ "$status" -ne 0 ]; then
        reason="$(printf '%s' "$reason" | tr '\r\n' '  ')"
        printf 'FAILED: %s: %s\n' "$phase" "$reason" >&2
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 143' HUP INT TERM

if [ "$#" -ne 1 ]; then
    usage >&2
    failure_reason='expected exactly one option'
    exit 1
fi

case "$1" in
    --dry-run) mode='dry-run' ;;
    --self-signed) mode='self-signed' ;;
    --help|-h)
        usage
        exit 0
        ;;
    *) die "unknown option: $1" ;;
esac

for tool in git zip unzip zipinfo shasum awk sed rg plutil cut sort cmp find uniq; do
    need_tool "$tool"
done

git_cmd=(git -C "$repo_root")

phase='preflight'
require_clean_tree
[ "$("${git_cmd[@]}" config --local --get core.hooksPath || true)" = '.githooks' ] || die 'core.hooksPath must be .githooks'

head_before="$("${git_cmd[@]}" rev-parse HEAD)"
previous_tag="$(highest_reachable_tag "$head_before")" || die 'no reachable annotated strict vMAJOR.MINOR.PATCH tag found'
validate_no_reserved_targets "$previous_tag"

run_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-release-local.XXXXXX")"
cleanup_run_dir="$run_dir"
reason_file="$run_dir/failure-reason.txt"
phase='release request'
request_zip="$(write_request "$previous_tag" "$head_before" "$run_dir")"
transport_output="$run_dir/transport.out"
transport_errors="$run_dir/transport.err"

transport_args=(--release-request "$request_zip")
if [ "$mode" = 'dry-run' ]; then
    transport_args+=(--dry-run)
fi

phase='VM transport'
# stdout is captured for the markers; stderr stays live and is also kept (the
# pipeline status is the runner's, because pipefail is on).
if ! { "$repo_root/eng/deploy-windows-vm.sh" "${transport_args[@]}" 2>&1 1>&3 | tee "$transport_errors" >&2; } 3>"$transport_output"; then
    cat "$transport_output" >&2
    # Carry the runner's own FAILED line (if it wrote one) into ours.
    runner_failure="$(grep '^FAILED: ' "$transport_errors" 2>/dev/null | tail -n 1 || true)"
    die "deploy-windows-vm.sh failed${runner_failure:+ (${runner_failure#FAILED: })}; see the lines above"
fi
release_control_dir="$repo_root/artifacts/.vm-control"

phase='transport markers'

source_archive_sha="$(single_marker SOURCE_ARCHIVE_SHA256 "$transport_output")"
release_request_sha="$(single_marker RELEASE_REQUEST_SHA256 "$transport_output")"
for hash in "$source_archive_sha" "$release_request_sha"; do
    printf '%s' "$hash" | rg -q '^[0-9a-f]{64}$' || die "invalid transport hash marker: $hash"
done

version="$(single_marker RELEASE_VERSION "$transport_output")"
printf '%s' "$version" | rg -q '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' || die "invalid release version: $version"
tag="v$version"

valid_step=0
while IFS= read -r possible; do
    if [ "$possible" = "$tag" ]; then
        valid_step=1
    fi
done < <(possible_next_tags "$previous_tag")
[ "$valid_step" = 1 ] || die "release version $tag is not one legal step from $previous_tag"

if "${git_cmd[@]}" rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    die "release tag already exists: $tag"
fi
if [ -e "$repo_root/artifacts/releases/$tag" ]; then
    die "release directory already exists: artifacts/releases/$tag"
fi

if [ "$mode" = 'dry-run' ]; then
    # Preview the prune that closes a real release. The new release is not on
    # disk in a dry run, so it is counted as the newest one: the keep-three rule
    # then drops one more than a prune of the current tree alone would.
    printf 'PRUNE_PREVIEW=the prune after release %s would remove (the new release counted as added):\n' "$tag"
    if ! "$repo_root/eng/prune-artifacts.sh" --dry-run --assume-release "$version"; then
        printf 'PRUNE_PREVIEW=unavailable\n'
        printf 'release-local: warning: ./eng/prune-artifacts.sh --dry-run failed; the prune preview is missing\n' >&2
    fi
    printf 'SOURCE_ARCHIVE_SHA256=%s\nRELEASE_REQUEST_SHA256=%s\nRELEASE_VERSION=%s\nSOURCE_COMMIT=%s\nPREVIOUS_TAG=%s\nDRY_RUN=PASS\n' \
        "$source_archive_sha" "$release_request_sha" "$version" "$head_before" "$previous_tag"
    exit 0
fi

phase='release evidence validation'
release_archive="$(single_marker RELEASE_ARCHIVE "$transport_output")"
release_sha="$(single_marker RELEASE_SHA256 "$transport_output")"
app_running_marker="$(single_marker APP_RUNNING "$transport_output")"
verify_marker="$(single_marker VERIFY "$transport_output")"
[ "$app_running_marker" = '1' ] || die 'transport APP_RUNNING marker is not 1'
[ "$verify_marker" = 'PASS' ] || die 'transport VERIFY marker is not PASS'
printf '%s' "$release_sha" | rg -q '^[0-9a-f]{64}$' || die 'invalid RELEASE_SHA256 marker'
require_clean_tree
[ "$("${git_cmd[@]}" rev-parse HEAD)" = "$head_before" ] || die 'HEAD changed during release'

archive_basename="$(basename "$release_archive")"
[ "$archive_basename" = "WinWispr-release-$tag.zip" ] || die "unexpected release archive basename: $archive_basename"
release_control_dir="$(dirname "$(dirname "$release_archive")")"
[ "$release_control_dir" = "$repo_root/artifacts/.vm-control" ] || die "release archive is outside the VM control directory: $release_archive"
local_archive="$release_archive"
local_sidecar="$local_archive.sha256"
test -f "$local_archive" || die "release archive was not copied back from the VM: $local_archive"
test -f "$local_sidecar" || die "release archive sidecar was not copied back from the VM: $local_sidecar"

sidecar_sha="$(read_sidecar_hash "$local_sidecar" "$archive_basename")"
[ "$sidecar_sha" = "$release_sha" ] || die 'outer sidecar does not match RELEASE_SHA256 marker'
actual_sha="$(shasum -a 256 "$local_archive" | awk '{print $1}')"
[ "$actual_sha" = "$release_sha" ] || die 'outer release archive SHA-256 mismatch'

entries="$run_dir/release-entries.txt"
validate_zip_entries "$local_archive" "$tag" "$entries"

releases_root="$repo_root/artifacts/releases"
mkdir -p "$releases_root"
staging="$(mktemp -d "$releases_root/.staging-$tag.XXXXXX")"
staging_dir="$staging"
final="$releases_root/$tag"
owner_token="$head_before-$$-$RANDOM"
claim_dir="$releases_root/.claim-$tag-$owner_token"
test ! -e "$final" || die "release directory already exists: $final"
mkdir "$claim_dir"
unzip -q "$local_archive" -d "$staging"
validate_sha256sums "$staging" "$tag"
validate_build_info "$staging/BUILD-INFO.json" "$version" "$previous_tag" "$head_before" "$source_archive_sha"
validate_vm_acceptance "$staging/VM-ACCEPTANCE.txt"
validate_changelog "$staging/CHANGELOG-$tag.md" "$tag" "$previous_tag" "$head_before"
validate_security_report "$staging/SECURITY-REPORT.md"
phase='release promotion'
printf '%s\n' "$owner_token" >"$staging/.winwispr-release-owner"
mv "$staging" "$final"
staging_dir=''
if [ ! -f "$final/.winwispr-release-owner" ] ||
    [ "$(cat "$final/.winwispr-release-owner" 2>/dev/null || true)" != "$owner_token" ]; then
    nested="$final/$(basename "$staging")"
    if [ -f "$nested/.winwispr-release-owner" ] &&
        [ "$(cat "$nested/.winwispr-release-owner")" = "$owner_token" ]; then
        rm -rf "$nested"
    fi
    die 'release directory appeared during promotion; foreign final preserved'
fi
promoted_dir="$final"
rmdir "$claim_dir"
claim_dir=''

phase='release tag'
require_clean_tree
[ "$("${git_cmd[@]}" rev-parse HEAD)" = "$head_before" ] || die 'HEAD changed during release'
if ! "${git_cmd[@]}" tag -a "$tag" "$head_before" -m "WinWispr $tag"; then
    if tag_is_annotated_on_head; then
        die "git tag failed after creating annotated $tag on expected HEAD; evidence preserved"
    fi
    remove_owned_promoted
    promoted_dir=''
    die "failed to create local tag $tag"
fi
rm -f "$final/.winwispr-release-owner"
promoted_dir=''

# The release is complete; pruning older local output must not fail it.
# ./eng/release-local.sh --dry-run previews exactly this prune.
phase='local prune'
"$repo_root/eng/prune-artifacts.sh" >&2 || printf 'release-local: warning: ./eng/prune-artifacts.sh failed; run it again\n' >&2

printf 'SOURCE_ARCHIVE_SHA256=%s\nRELEASE_REQUEST_SHA256=%s\nRELEASE_VERSION=%s\nRELEASE_DIR=%s\nRELEASE_TAG=%s\nAPP_RUNNING=1\nSTATUS=PASS\n' \
    "$source_archive_sha" "$release_request_sha" "$version" "$final" "$tag"
