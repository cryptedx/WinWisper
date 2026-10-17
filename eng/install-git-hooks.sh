#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

git -C "$repo_root" rev-parse --show-toplevel >/dev/null

for hook in "$repo_root/.githooks/commit-msg" "$repo_root/.githooks/post-commit" "$repo_root/.githooks/pre-push"; do
    if [ ! -x "$hook" ]; then
        echo "hook is not executable: $hook" >&2
        exit 1
    fi
done

git -C "$repo_root" config --local core.hooksPath .githooks
test "$(git -C "$repo_root" config --local --get core.hooksPath)" = '.githooks'
