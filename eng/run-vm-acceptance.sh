#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
remote_host='windows-vm'
remote_mirror='C:\windev\WinWispr\src'
remote_runner="$remote_mirror\\artifacts\\dev-check\\bin\\WinWispr.Tests\\release_win-x64\\WinWispr.Tests.exe"
remote_scratch_root="$remote_mirror\\artifacts\\dev-check\\acceptance"
timeout_seconds="${WINWISPR_ACCEPTANCE_TIMEOUT_SECONDS:-1800}"

usage() {
    cat <<'USAGE'
Usage: ./eng/run-vm-acceptance.sh <test-filter> [NAME=VALUE ...]

Runs the test runner that the last ./eng/deploy-windows-vm.sh --check built on
windows-vm, with the given environment variables, and prints the runner's
output and one line per test (outcome, duration) from its trx report. It
builds and syncs nothing: build first with
  ./eng/deploy-windows-vm.sh --check --test-filter <expression>
It warns when the working tree is newer than the built runner. The exit
status is non-zero when a test failed or the runner failed. A test reported
NotExecuted is inconclusive and is not acceptance evidence.

The runner starts through SSH in the non-interactive session, so tests that
need the logged-in desktop (hotkeys, windows) belong to the deployment check.
It takes the VM lock of the other eng scripts. WINWISPR_ACCEPTANCE_TIMEOUT_SECONDS
(default 1800) ends a hung runner.
USAGE
}

die() {
    printf 'run-vm-acceptance: %s\n' "$1" >&2
    exit 1
}

filter=''
env_names=()
env_values=()
while (($# > 0)); do
    case "$1" in
        --help|-h)
            usage
            exit 0
            ;;
        -*)
            die "unknown option: $1"
            ;;
        *=*)
            [[ "$1" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || die "invalid NAME=VALUE: $1"
            env_names+=("${1%%=*}")
            env_values+=("${1#*=}")
            ;;
        *)
            [[ -z "$filter" ]] || die "unexpected argument: $1 (environment variables are NAME=VALUE)"
            filter="$1"
            ;;
    esac
    shift
done
[[ -n "$filter" ]] || { usage >&2; exit 2; }
[[ "$filter" != *'"'* ]] || die 'the test filter must not contain a double quote'
[[ "$timeout_seconds" =~ ^[0-9]+$ ]] || die 'WINWISPR_ACCEPTANCE_TIMEOUT_SECONDS must be a whole number of seconds'

for tool in git ssh base64 iconv sed tr awk; do
    command -v "$tool" >/dev/null 2>&1 || die "required command not found: $tool"
done

source "$repo_root/eng/vm-lock.sh"
trap 'release_lock' EXIT

ps_quote() {
    local value
    value="$(printf '%s' "$1" | sed "s/'/''/g")"
    printf "'%s'" "$value"
}

encode_ps() {
    printf '%s' "$1" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\r\n'
}

# The script is sent with -EncodedCommand: the SSH login shell is PowerShell and
# would expand a plain -Command argument before it reaches the worker. The
# header carries the values; the body is static.
build_remote_script() {
    local i
    printf '$filter = %s\n$runner = %s\n$scratchRoot = %s\n$timeoutSeconds = %s\n' \
        "$(ps_quote "$filter")" "$(ps_quote "$remote_runner")" "$(ps_quote "$remote_scratch_root")" "$timeout_seconds"
    printf '$environment = [ordered]@{}\n'
    for i in "${!env_names[@]}"; do
        printf '$environment[%s] = %s\n' "$(ps_quote "${env_names[$i]}")" "$(ps_quote "${env_values[$i]}")"
    done
    cat <<'POWERSHELL'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Encode([string]$text) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) }
# A redirected .NET console writes in the OEM code page.
$oem = try { [Text.Encoding]::GetEncoding([int](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').OEMCP) } catch { [Text.Encoding]::UTF8 }
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { 'RUNNER_MISSING=1'; exit 0 }
'RUNNER_UTC=' + [DateTimeOffset]::new((Get-Item -LiteralPath $runner).LastWriteTimeUtc).ToUnixTimeSeconds()
$scratch = Join-Path $scratchRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$stdoutPath = Join-Path $scratch 'stdout.txt'
$stderrPath = Join-Path $scratch 'stderr.txt'
try {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    # Plain SSH loses the runner's console output, so it is redirected to files.
    $process = Start-Process -FilePath $runner -NoNewWindow -PassThru `
        -ArgumentList @('--filter', ('"' + $filter + '"'), '--output', 'Detailed', '--report-trx',
            '--report-trx-filename', 'acceptance.trx', '--results-directory', $scratch) `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $null = $process.Handle
    $exitCode = 124
    if ($process.WaitForExit($timeoutSeconds * 1000)) {
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    } else {
        & taskkill.exe /F /T /PID $process.Id *> $null
    }
    'RUNNER_EXIT_CODE=' + $exitCode
    foreach ($pair in @(@('RUNNER_STDOUT_B64=', $stdoutPath), @('RUNNER_STDERR_B64=', $stderrPath))) {
        $text = if (Test-Path -LiteralPath $pair[1]) { [IO.File]::ReadAllText($pair[1], $oem) } else { '' }
        $pair[0] + (Encode $text)
    }
    $lines = [Collections.Generic.List[string]]::new()
    $trx = Get-ChildItem -LiteralPath $scratch -Filter '*.trx' -Recurse -File | Select-Object -First 1
    if ($null -ne $trx) {
        [xml]$report = [IO.File]::ReadAllText($trx.FullName)
        $namespaces = New-Object Xml.XmlNamespaceManager($report.NameTable)
        $namespaces.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        foreach ($result in $report.SelectNodes('//t:UnitTestResult', $namespaces)) {
            $seconds = 0.0
            if ($result.duration) { $seconds = [TimeSpan]::Parse($result.duration).TotalSeconds }
            $lines.Add($result.outcome + "`t" + $seconds.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture) + "`t" + $result.testName)
        }
    }
    'TRX_RESULTS_B64=' + (Encode ($lines -join "`n"))
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
POWERSHELL
}

acquire_lock

printf 'Running on %s: filter %s\n' "$remote_host" "$filter" >&2
remote_output="$(ssh "$remote_host" powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass \
    -EncodedCommand "$(encode_ps "$(build_remote_script)")" | tr -d '\r')" ||
    die "the run on $remote_host failed or $remote_host is unreachable; ask the user to start the VM if it is down"

field() { printf '%s\n' "$remote_output" | sed -n "s/^$1=//p" | head -n 1; }
decoded_field() { field "$1" | base64 --decode; }

if [[ "$(field RUNNER_MISSING)" == 1 ]]; then
    die "no test runner on $remote_host; build it first with ./eng/deploy-windows-vm.sh --check --test-filter '<expression>'"
fi
exit_code="$(field RUNNER_EXIT_CODE)"
[[ "$exit_code" =~ ^[0-9]+$ ]] || { printf '%s\n' "$remote_output" | tail -n 20 >&2; die 'the VM did not report the runner exit code'; }

# The mirror keeps the Mac's file times, so a source file newer than the runner
# means the runner was built before the latest edit.
runner_utc="$(field RUNNER_UTC)"
newest_source="$(git -C "$repo_root" ls-files -z --cached --others --exclude-standard |
    (cd "$repo_root" && xargs -0 stat -f %m 2>/dev/null) | sort -n | tail -n 1 || true)"
if [[ "$runner_utc" =~ ^[0-9]+$ && "$newest_source" =~ ^[0-9]+$ ]] && ((newest_source > runner_utc)); then
    printf 'run-vm-acceptance: warning: the working tree is newer than the built runner; rebuild with ./eng/deploy-windows-vm.sh --check --test-filter <expression> for current results\n' >&2
fi

decoded_field RUNNER_STDOUT_B64
stderr_text="$(decoded_field RUNNER_STDERR_B64)"
[[ -z "$stderr_text" ]] || printf '%s\n' "$stderr_text" >&2

printf '\nPer-test outcomes (trx report):\n'
results="$(decoded_field TRX_RESULTS_B64)"
failed=0 not_executed=0 total=0
if [[ -n "$results" ]]; then
    while IFS=$'\t' read -r outcome seconds name; do
        [[ -n "$outcome" ]] || continue
        total=$((total + 1))
        [[ "$outcome" != Failed ]] || failed=$((failed + 1))
        [[ "$outcome" != NotExecuted ]] || not_executed=$((not_executed + 1))
        printf '%-12s %7ss  %s\n' "$outcome" "$seconds" "$name"
    done <<<"$results"
else
    printf '(no trx results)\n'
fi
printf 'TESTS=%s FAILED=%s NOT_EXECUTED=%s RUNNER_EXIT_CODE=%s\n' "$total" "$failed" "$not_executed" "$exit_code"
((not_executed == 0)) || printf 'run-vm-acceptance: %s test(s) NotExecuted: inconclusive, not acceptance evidence\n' "$not_executed" >&2

if ((failed > 0 || exit_code != 0)); then
    die "failed: $failed failed test(s), runner exit code $exit_code"
fi
printf 'ACCEPTANCE=PASS\n'
