#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
    cat <<'USAGE'
Usage: ./eng/vm-ps.sh <script.ps1>
       ./eng/vm-ps.sh - <<'PS'
       ...
       PS

Runs a PowerShell script on windows-vm over SSH and prints its output as plain
text. The script is sent with -EncodedCommand, so the outer SSH shell expands
nothing. Progress records are silenced and errors become text lines, so the
output carries no CLIXML. Errors stop the script ($ErrorActionPreference is
Stop; the script may lower it) and exit 1; an explicit exit code is kept. The
script runs in SSH Session 0, without the interactive desktop.
USAGE
}

die() {
    printf 'vm-ps: %s\n' "$1" >&2
    exit 1
}

(($# == 1)) || { usage >&2; exit 2; }
case "$1" in
    -h|--help) usage; exit 0 ;;
    -) body="$(cat)" ;;
    *) [[ -f "$1" ]] || die "script not found: $1"; body="$(cat -- "$1")" ;;
esac

script="\$ProgressPreference = 'SilentlyContinue'
\$ErrorActionPreference = 'Stop'
try {
$body
} catch {
    \"ERROR: \$_\"
    exit 1
}"

encoded="$(printf '%s' "$script" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"
# The SSH login shell is PowerShell, which reports any failed command as 1; pass the real code on.
exec ssh windows-vm "powershell -NoProfile -NonInteractive -EncodedCommand $encoded; exit \$LASTEXITCODE"
