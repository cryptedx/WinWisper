#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo_root/eng/setup-worktree.sh"
test -x "$script"
bash -n "$script"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-worktree-contract.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT
main="$tmp_dir/main"
worktree="$tmp_dir/worktree"
mkdir -p "$main/eng" "$main/artifacts/model" "$main/artifacts/voices/v1" "$main/artifacts/translation-model/opus-mt-de-en"
cp "$script" "$main/eng/setup-worktree.sh"
printf 'model\n' >"$main/artifacts/model/archive.tar.bz2"
printf 'voice\n' >"$main/artifacts/voices/v1/model.onnx"
printf 'translation\n' >"$main/artifacts/translation-model/opus-mt-de-en/source.spm"
(
    cd "$main"
    git init -q
    git config user.email test@example.invalid
    git config user.name 'Worktree Contract'
    printf 'artifacts/model/\nartifacts/voices/\nartifacts/translation-model/\n' >.gitignore
    printf 'source\n' >README.md
    git add .gitignore README.md eng
    git commit -qm 'chore: baseline'
    git worktree add -q "$worktree" -b feature
)

# The main checkout is refused and left untouched.
if "$main/eng/setup-worktree.sh" >"$tmp_dir/main.out" 2>&1; then
    echo 'The main checkout must be refused.' >&2
    exit 1
fi
grep -Fq 'main checkout' "$tmp_dir/main.out"
test -d "$main/artifacts/model" && test ! -L "$main/artifacts/model"

# In the worktree, the links appear, point at the main checkout's directories,
# and Git does not offer them for staging.
"$worktree/eng/setup-worktree.sh" >"$tmp_dir/first.out"
grep -Fxq 'SETUP_WORKTREE=PASS linked=3' "$tmp_dir/first.out"
test -L "$worktree/artifacts/model"
test "$(cd "$worktree/artifacts/model" && pwd -P)" = "$(cd "$main/artifacts/model" && pwd -P)"
test -f "$worktree/artifacts/model/archive.tar.bz2"
test -L "$worktree/artifacts/voices"
test -L "$worktree/artifacts/translation-model"
test -f "$worktree/artifacts/translation-model/opus-mt-de-en/source.spm"
test -z "$(git -C "$worktree" status --porcelain=v1 --untracked-files=all)"

# A second run changes nothing, including the exclude file.
exclude_before="$(cat "$main/.git/info/exclude")"
"$worktree/eng/setup-worktree.sh" >"$tmp_dir/second.out"
grep -Fxq 'SETUP_WORKTREE=PASS linked=0' "$tmp_dir/second.out"
test "$exclude_before" = "$(cat "$main/.git/info/exclude")"
test "$(grep -Fxc '/artifacts/model' "$main/.git/info/exclude")" = 1

# An existing directory is left as is, and a missing source is not an error.
rm "$worktree/artifacts/model"
mkdir "$worktree/artifacts/model"
"$worktree/eng/setup-worktree.sh" >/dev/null
test ! -L "$worktree/artifacts/model"
rm -rf "$main/artifacts/voices" "$worktree/artifacts/voices"
"$worktree/eng/setup-worktree.sh" >/dev/null 2>&1
test ! -e "$worktree/artifacts/voices"

echo 'setup-worktree contract: PASS'
