#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo_root/eng/run-vm-acceptance.sh"

test -x "$script"
bash -n "$script"
test -f "$repo_root/eng/vm-lock.sh"
grep -Fq -- "remote_host='windows-vm'" "$script"
grep -Fq -- 'eng/vm-lock.sh' "$script"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-acceptance-contract.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT

# A fake checkout holding only the scripts under test, and an ssh shim that
# plays the VM: it decodes the -EncodedCommand payload, records it, and answers
# with the markers the real PowerShell script prints.
repo="$tmp_dir/repo"
mkdir -p "$repo/eng" "$tmp_dir/bin"
cp "$script" "$repo/eng/run-vm-acceptance.sh"
cp "$repo_root/eng/vm-lock.sh" "$repo/eng/vm-lock.sh"
(
    cd "$repo"
    git init -q
    git config user.email test@example.invalid
    git config user.name 'Acceptance Contract'
    printf 'source\n' >README.md
    git add README.md eng
    git commit -qm 'chore: baseline'
)

cat >"$tmp_dir/bin/ssh" <<'SHIM'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == *" -EncodedCommand "* ]] || { echo 'ssh payload must be an -EncodedCommand' >&2; exit 1; }
printf '%s\n' "$1" >>"$FAKE_SSH_LOG"
printf '%s' "${!#}" | base64 --decode | iconv -f UTF-16LE -t UTF-8 >"$FAKE_PAYLOAD"
b64() { printf '%s' "$1" | base64 | tr -d '\r\n'; }
if [[ "${FAKE_RUNNER_MISSING:-0}" == 1 ]]; then
    printf 'RUNNER_MISSING=1\r\n'
    exit 0
fi
printf 'RUNNER_UTC=%s\r\n' "${FAKE_RUNNER_UTC:-9999999999}"
printf 'RUNNER_EXIT_CODE=%s\r\n' "${FAKE_RUNNER_EXIT:-0}"
printf 'RUNNER_STDOUT_B64=%s\r\n' "$(b64 $'fake runner output\nline two')"
printf 'RUNNER_STDERR_B64=%s\r\n' "$(b64 "${FAKE_STDERR:-}")"
printf 'TRX_RESULTS_B64=%s\r\n' "$(b64 "${FAKE_TRX:-$'Passed\t0.27\tFirst_test\nPassed\t12.50\tSecond_test'}")"
SHIM
chmod +x "$tmp_dir/bin/ssh"

run() {
    FAKE_SSH_LOG="$tmp_dir/ssh.log" FAKE_PAYLOAD="$tmp_dir/payload.ps1" PATH="$tmp_dir/bin:$PATH" \
        "$repo/eng/run-vm-acceptance.sh" "$@"
}

# A passing run: the payload is PowerShell with Start-Process and redirected output, the
# output and the per-test list come back, the lock is released.
run 'FullyQualifiedName~Name' WINWISPR_A=one "WINWISPR_B=it's here" >"$tmp_dir/pass.out" 2>"$tmp_dir/pass.err"
grep -Fxq 'fake runner output' "$tmp_dir/pass.out"
grep -Fq 'Passed' "$tmp_dir/pass.out"
grep -Eq '^Passed +12\.50s  Second_test$' "$tmp_dir/pass.out"
grep -Fxq 'TESTS=2 FAILED=0 NOT_EXECUTED=0 RUNNER_EXIT_CODE=0' "$tmp_dir/pass.out"
grep -Fxq 'ACCEPTANCE=PASS' "$tmp_dir/pass.out"
grep -Fxq 'windows-vm' <(head -n 1 "$tmp_dir/ssh.log")
grep -Fq 'Start-Process' "$tmp_dir/payload.ps1"
grep -Fq -- '-RedirectStandardOutput' "$tmp_dir/payload.ps1"
grep -Fq -- '-RedirectStandardError' "$tmp_dir/payload.ps1"
grep -Fq -- "'--report-trx'" "$tmp_dir/payload.ps1"
grep -Fq -- "\$filter = 'FullyQualifiedName~Name'" "$tmp_dir/payload.ps1"
grep -Fq -- "\$environment['WINWISPR_A'] = 'one'" "$tmp_dir/payload.ps1"
grep -Fq -- "\$environment['WINWISPR_B'] = 'it''s here'" "$tmp_dir/payload.ps1"
test ! -e "$repo/artifacts/.vm-lock"

# A failed test fails the run, but the whole list is still printed.
if FAKE_TRX=$'Passed\t0.10\tOk_test\nFailed\t3.20\tBad_test' FAKE_RUNNER_EXIT=2 run 'Name~x' >"$tmp_dir/fail.out" 2>"$tmp_dir/fail.err"; then
    echo 'A failed test must fail the run.' >&2
    exit 1
fi
grep -Eq '^Failed +3\.20s  Bad_test$' "$tmp_dir/fail.out"
grep -Fxq 'TESTS=2 FAILED=1 NOT_EXECUTED=0 RUNNER_EXIT_CODE=2' "$tmp_dir/fail.out"
test ! -e "$repo/artifacts/.vm-lock"

# A runner failure without a failed test (for example no test matched) fails too.
if FAKE_TRX='' FAKE_RUNNER_EXIT=8 run 'Name~none' >"$tmp_dir/zero.out" 2>"$tmp_dir/zero.err"; then
    echo 'A non-zero runner exit code must fail the run.' >&2
    exit 1
fi

# An inconclusive test passes but is called out as no evidence.
FAKE_TRX=$'NotExecuted\t0.00\tNeeds_model' run 'Name~y' >"$tmp_dir/inconclusive.out" 2>"$tmp_dir/inconclusive.err"
grep -Fq 'inconclusive, not acceptance evidence' "$tmp_dir/inconclusive.err"

# A runner older than the working tree gets a warning, not a failure.
FAKE_RUNNER_UTC=1 run 'Name~z' >/dev/null 2>"$tmp_dir/stale.err"
grep -Fq 'working tree is newer than the built runner' "$tmp_dir/stale.err"

# Without a built runner the script says how to build one.
if FAKE_RUNNER_MISSING=1 run 'Name~x' >/dev/null 2>"$tmp_dir/missing.err"; then
    echo 'A missing runner must fail the run.' >&2
    exit 1
fi
grep -Fq 'deploy-windows-vm.sh --check --test-filter' "$tmp_dir/missing.err"

# Bad arguments are rejected before any SSH call.
: >"$tmp_dir/ssh.log"
for args in '' '--bogus x' 'Name~x BAD-NAME=1' 'Name~x second-filter' 'Name~"x'; do
    # shellcheck disable=SC2086
    if run $args >/dev/null 2>&1; then
        echo "Expected rejection: $args" >&2
        exit 1
    fi
done
test ! -s "$tmp_dir/ssh.log"

# A live owner holds the VM lock: the run fails fast, and the lock stays.
mkdir "$repo/artifacts/.vm-lock" 2>/dev/null || { mkdir -p "$repo/artifacts"; mkdir "$repo/artifacts/.vm-lock"; }
printf 'PID=%s\n' "$$" >"$repo/artifacts/.vm-lock/owner"
if run 'Name~x' >/dev/null 2>"$tmp_dir/locked.err"; then
    echo 'A run must not start while another operation holds the VM lock.' >&2
    exit 1
fi
grep -Fq 'another VM operation holds' "$tmp_dir/locked.err"
test -d "$repo/artifacts/.vm-lock"
test ! -s "$tmp_dir/ssh.log"
# A lock whose owner has exited is taken over and released again.
printf 'PID=999999\n' >"$repo/artifacts/.vm-lock/owner"
run 'Name~x' >/dev/null 2>&1
test ! -e "$repo/artifacts/.vm-lock"

echo 'run-vm-acceptance contract: PASS'
