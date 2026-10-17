#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
repository='cryptedx/WinWisper'
publish=0

usage() {
    printf '%s\n' \
        'Usage: ./eng/publish-models.sh models-vN [--publish]' \
        'Publishes the WinWispr Models installer as its own GitHub release models-vN.' \
        'N must be the version in eng/model-package.json, and artifacts/models/ must hold' \
        'WinWispr-Models-vN.exe and its sidecar (./eng/deploy-windows-vm.sh --model-package).' \
        'The draft release comes from Forgejo once the models-vN tag is pushed to origin;' \
        'this script uploads and verifies both assets;' \
        '--publish then publishes it. It never becomes the latest release: that stays the app.' \
        'On a metered network (e.g. iPhone hotspot) it asks before uploading;' \
        'non-interactive runs stop unless ALLOW_METERED=1 is set.'
}

die() {
    printf 'publish-models: %s\n' "$1" >&2
    exit 1
}

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    usage >&2
    exit 1
fi
if [ "$1" = '--help' ] || [ "$1" = '-h' ]; then
    usage
    exit 0
fi

tag="$1"
if [ "$#" = 2 ]; then
    [ "$2" = '--publish' ] || die "unknown option: $2"
    publish=1
fi
printf '%s\n' "$tag" | rg -q '^models-v[1-9][0-9]*$' || die "invalid model release tag: $tag"

for tool in gh rg awk tr wc shasum plutil; do
    command -v "$tool" >/dev/null 2>&1 || die "required command not found: $tool"
done

version="${tag#models-v}"
pinned="$(plutil -extract version raw -o - "$repo_root/eng/model-package.json")" || die 'could not read eng/model-package.json'
[ "$version" = "$pinned" ] || die "eng/model-package.json pins model package version $pinned, not $version"

payload="WinWispr-Models-v$version.exe"
assets=("$payload" "$payload.sha256")
models_dir="$repo_root/artifacts/models"
for asset in "${assets[@]}"; do
    [ -f "$models_dir/$asset" ] || die "model package asset is missing: $models_dir/$asset (run ./eng/deploy-windows-vm.sh --model-package)"
done
sidecar="$models_dir/$payload.sha256"
line_count="$(tr -d '\r' <"$sidecar" | awk 'END { print NR + 0 }')"
[ "$line_count" = 1 ] || die "sidecar must contain exactly one line: $sidecar"
line="$(tr -d '\r' <"$sidecar")"
expected_hash="$(printf '%s\n' "$line" | awk 'NF == 2 { print $1 }')"
referenced_file="$(printf '%s\n' "$line" | awk 'NF == 2 { print $2 }')"
referenced_file="${referenced_file#\*}"
printf '%s' "$expected_hash" | rg -q '^[0-9a-f]{64}$' || die "invalid sidecar hash: $sidecar"
[ "$referenced_file" = "$payload" ] || die "sidecar references unexpected file: $sidecar"
actual_hash="$(shasum -a 256 "$models_dir/$payload" | awk '{print $1}')"
[ "$actual_hash" = "$expected_hash" ] || die "sidecar hash mismatch: $payload"

# Draft releases are hidden from the tag endpoint; look them up in the collection.
find_release_id() {
    gh api "repos/$repository/releases?per_page=100" --jq ".[] | select(.tag_name == \"$tag\") | .id" | head -n 1
}
release_id="$(find_release_id)" || die 'could not list GitHub releases'
if [ -z "$release_id" ]; then
    # The draft comes from Forgejo, not from this script: the Forgejo workflow
    # "Publish model release to GitHub" creates it for a pushed models-vN tag.
    die "GitHub release $tag not found: tag the commit $tag, push the tag to origin and wait for the Forgejo workflow \"Publish model release to GitHub\" to create the draft"
fi
api_root="repos/$repository/releases/$release_id"
release_draft="$(gh api "$api_root" --jq '.draft')" || die "could not read GitHub release: $tag"

read_remote_assets() {
    remote_assets="$(gh api "$api_root" --jq '.assets[] | [.name, (.size | tostring), (.digest // "")] | @tsv')" ||
        die "could not read GitHub assets: $tag"
}

remote_field() {
    printf '%s\n' "$remote_assets" | awk -F '\t' -v name="$1" -v field="$2" '$1 == name { print $field; exit }'
}

read_remote_assets
to_upload=()
for asset in "${assets[@]}"; do
    local_path="$models_dir/$asset"
    local_size="$(wc -c <"$local_path" | tr -d '[:space:]')"
    local_hash="$(shasum -a 256 "$local_path" | awk '{print $1}')"
    remote_size="$(remote_field "$asset" 2)"
    if [ -z "$remote_size" ]; then
        to_upload+=("$local_path")
        printf 'missing=%s\n' "$asset"
    elif [ "$remote_size" != "$local_size" ] || [ "$(remote_field "$asset" 3)" != "sha256:$local_hash" ]; then
        die "remote asset mismatch; refusing to overwrite: $asset"
    else
        printf 'verified-remote=%s\n' "$asset"
    fi
done

on_metered_network() {
    # macOS marks iPhone hotspots and other metered links as expensive.
    local iface
    iface="$(route -n get default 2>/dev/null | awk '/interface:/ { print $2 }')" || return 1
    [ -n "$iface" ] || return 1
    ipconfig getsummary "$iface" 2>/dev/null | rg -q 'IsExpensive : TRUE'
}

if [ "${#to_upload[@]}" -gt 0 ] && [ "${ALLOW_METERED:-0}" != 1 ] && on_metered_network; then
    upload_mb="$(cat "${to_upload[@]}" | wc -c | awk '{ printf "%d", $1 / 1048576 }')"
    printf 'publish-models: metered network (e.g. iPhone hotspot); upload needs %s MB\n' "$upload_mb" >&2
    if [ -t 0 ]; then
        read -r -p 'Upload over mobile data anyway? [y/N] ' answer || answer=''
        [ "$answer" = y ] || [ "$answer" = Y ] || die 'upload cancelled on metered network'
    else
        die 'refusing to upload on metered network; set ALLOW_METERED=1 to override'
    fi
fi

if [ "${#to_upload[@]}" -gt 0 ]; then
    gh release upload "$tag" "${to_upload[@]}" --repo "$repository"
fi

verify_remote_assets() {
    read_remote_assets
    expected_names="$(printf '%s\n' "${assets[@]}" | sort)"
    actual_names="$(printf '%s\n' "$remote_assets" | awk -F '\t' 'NF { print $1 }' | sort)"
    [ "$actual_names" = "$expected_names" ] || die "GitHub asset set mismatch for $tag"
    for asset in "${assets[@]}"; do
        local_path="$models_dir/$asset"
        [ "$(remote_field "$asset" 2)" = "$(wc -c <"$local_path" | tr -d '[:space:]')" ] || die "GitHub size mismatch: $asset"
        [ "$(remote_field "$asset" 3)" = "sha256:$(shasum -a 256 "$local_path" | awk '{print $1}')" ] ||
            die "GitHub SHA-256 mismatch: $asset"
    done
}

verify_remote_assets

if [ "$publish" = 1 ] && [ "$release_draft" = 'true' ]; then
    gh release edit "$tag" --repo "$repository" --draft=false --latest=false
    release_draft="$(gh api "$api_root" --jq '.draft')" || die "could not verify publication: $tag"
    [ "$release_draft" = 'false' ] || die "GitHub release is still a draft: $tag"
    verify_remote_assets
fi

release_url="$(gh api "$api_root" --jq '.html_url')" || die "could not read release URL: $tag"
printf 'RELEASE_TAG=%s\nRELEASE_URL=%s\nDRAFT=%s\nASSETS=2\nSTATUS=PASS\n' "$tag" "$release_url" "$release_draft"
