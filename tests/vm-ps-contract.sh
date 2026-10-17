#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
runner="$repo_root/eng/vm-ps.sh"

test -x "$runner"
bash -n "$runner"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-vm-ps-contract.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT

# A fake ssh records the host and decodes the -EncodedCommand payload.
mkdir -p "$tmp_dir/bin"
cat >"$tmp_dir/bin/ssh" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$1" >"$FAKE_SSH_DIR/host"
encoded="${2##* -EncodedCommand }"
encoded="${encoded%%;*}"
printf '%s\n' "${2##*; }" >"$FAKE_SSH_DIR/tail"
printf '%s' "$encoded" | base64 -D | iconv -f UTF-16LE -t UTF-8 >"$FAKE_SSH_DIR/script"
EOF
chmod +x "$tmp_dir/bin/ssh"
export FAKE_SSH_DIR="$tmp_dir" PATH="$tmp_dir/bin:$PATH"

printf 'Get-Item "C:\\windev\\$env:USERNAME"\n' >"$tmp_dir/input.ps1"
"$runner" "$tmp_dir/input.ps1"
grep -Fxq 'windows-vm' "$tmp_dir/host"
grep -Fxq 'exit $LASTEXITCODE' "$tmp_dir/tail"
grep -Fxq "\$ProgressPreference = 'SilentlyContinue'" "$tmp_dir/script"
grep -Fxq "\$ErrorActionPreference = 'Stop'" "$tmp_dir/script"
# The body arrives unexpanded, so the outer shell touched nothing.
grep -Fxq 'Get-Item "C:\windev\$env:USERNAME"' "$tmp_dir/script"
grep -Fq '"ERROR: $_"' "$tmp_dir/script"

printf 'Write-Output from-stdin\n' | "$runner" -
grep -Fxq 'Write-Output from-stdin' "$tmp_dir/script"

if "$runner" "$tmp_dir/missing.ps1" 2>/dev/null; then
    echo 'a missing script must fail' >&2
    exit 1
fi

echo 'vm-ps contract: PASS'
