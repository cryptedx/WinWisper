#!/usr/bin/env bash
set -euo pipefail

# Prepares a linked worktree for ./eng/deploy-windows-vm.sh. The bundled speech
# model, the offline voices and the translation model are ignored by Git and
# live only in the main checkout's artifacts/, so a fresh worktree has none of
# them. This links them in.
# It is idempotent and refuses to run in the main checkout.

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

die() {
    printf 'setup-worktree: %s\n' "$1" >&2
    exit 1
}

git_dir="$(git -C "$repo_root" rev-parse --path-format=absolute --git-dir)"
common_dir="$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)"
[[ "$git_dir" != "$common_dir" ]] || die 'this is the main checkout; run it in a linked worktree (git worktree add)'
main_root="$(dirname -- "$common_dir")"

# .gitignore lists "artifacts/model/", "artifacts/voices/" and
# "artifacts/translation-model/", which match
# directories only; a symlink is not one, so Git would offer it for staging.
# info/exclude lives in the common Git directory, so every worktree of this
# repository shares the entries; they match the real directories of the main
# checkout too, where .gitignore already covers them.
exclude_file="$(git -C "$repo_root" rev-parse --path-format=absolute --git-path info/exclude)"
mkdir -p "$(dirname -- "$exclude_file")"
touch "$exclude_file"
mkdir -p "$repo_root/artifacts"

linked=0
for name in model voices translation-model; do
    entry="/artifacts/$name"
    grep -Fxq -- "$entry" "$exclude_file" || printf '%s\n' "$entry" >>"$exclude_file"
    source_dir="$main_root/artifacts/$name"
    target="$repo_root/artifacts/$name"
    if [[ -e "$target" || -L "$target" ]]; then
        printf 'artifacts/%s exists, left as is\n' "$name"
        continue
    fi
    if [[ ! -d "$source_dir" ]]; then
        printf 'artifacts/%s: not in the main checkout (%s), nothing to link\n' "$name" "$source_dir" >&2
        continue
    fi
    ln -s "$source_dir" "$target"
    printf 'linked artifacts/%s -> %s\n' "$name" "$source_dir"
    linked=$((linked + 1))
done
printf 'SETUP_WORKTREE=PASS linked=%s\n' "$linked"
