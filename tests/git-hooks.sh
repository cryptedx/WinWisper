#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

repo="$tmp_dir/repo"
mkdir -p "$repo/eng" "$repo/.githooks"

cp "$repo_root/eng/install-git-hooks.sh" "$repo/eng/install-git-hooks.sh"
cp "$repo_root/.githooks/commit-msg" "$repo/.githooks/commit-msg"
cp "$repo_root/.githooks/post-commit" "$repo/.githooks/post-commit"
cp "$repo_root/.githooks/pre-push" "$repo/.githooks/pre-push"
cat >"$repo/eng/check-local.sh" <<'EOF'
#!/usr/bin/env bash
echo 'local checks ran'
exit "${FAKE_LOCAL_CHECKS_EXIT:-0}"
EOF
chmod +x "$repo/eng/install-git-hooks.sh" "$repo/eng/check-local.sh" \
    "$repo/.githooks/commit-msg" "$repo/.githooks/post-commit" "$repo/.githooks/pre-push"

cd "$repo"
git init -q
git config user.email test@example.invalid
git config user.name 'Git Hooks Contract'
git commit --allow-empty -m 'chore: baseline' --no-verify >/dev/null

./eng/install-git-hooks.sh
test "$(git config --local --get core.hooksPath)" = '.githooks'

git commit --allow-empty -m 'feat(ui): add shortcut' >/dev/null

if git commit --allow-empty -m 'update shortcut'; then
    echo 'commit-msg accepted an unknown subject' >&2
    exit 1
fi

git commit --allow-empty -m 'docs: explain shortcut' >docs.out 2>&1
if rg -q 'release pending' docs.out; then
    echo 'docs commit printed a release reminder' >&2
    exit 1
fi

git commit --allow-empty -m 'fix: repair shortcut' >fix.out 2>&1
rg -q 'release pending' fix.out

git commit --allow-empty -m $'refactor: change format\n\nBREAKING CHANGE: updates transcript format' >refactor.out 2>&1
rg -q 'release pending' refactor.out

shim_dir="$tmp_dir/shims"
mkdir -p "$shim_dir"
ln -s "$(command -v sed)" "$shim_dir/sed"
ln -s "$(command -v tr)" "$shim_dir/tr"
cat >"$shim_dir/git" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
chmod +x "$shim_dir/git"

PATH="$shim_dir" "$(command -v bash)" ./.githooks/post-commit >direct.out 2>&1
test $? -eq 0
if rg -q 'release pending' direct.out; then
    echo 'post-commit printed a release reminder without git' >&2
    exit 1
fi

git init -q --bare "$tmp_dir/remote.git"
git remote add origin "$tmp_dir/remote.git"
if FAKE_LOCAL_CHECKS_EXIT=1 git push -q origin HEAD:refs/heads/main >push-fail.out 2>&1; then
    echo 'pre-push accepted a failing local check' >&2
    exit 1
fi
rg -q 'push blocked' push-fail.out
if git -C "$tmp_dir/remote.git" rev-parse -q --verify refs/heads/main >/dev/null; then
    echo 'blocked push updated the remote' >&2
    exit 1
fi
git push -q origin HEAD:refs/heads/main >push.out 2>&1
rg -q 'local checks ran' push.out
test "$(git -C "$tmp_dir/remote.git" rev-parse refs/heads/main)" = "$(git rev-parse HEAD)"
