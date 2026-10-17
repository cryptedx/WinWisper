#!/usr/bin/env bash
set -euo pipefail

# `set -e` ignores pipelines negated with `!`, so negative guards go through here.
refute() {
    if "$@"; then
        printf 'unexpected match: %s\n' "$*" >&2
        exit 1
    fi
}

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
release_local="$repo_root/eng/release-local.sh"
publish_release="$repo_root/eng/publish-release.sh"
runner="$repo_root/eng/deploy-windows-vm.sh"
legacy_release="$repo_root/eng/Release.ps1"
evidence_builder="$repo_root/eng/New-ReleaseEvidence.ps1"
request_helper="$repo_root/eng/ReleaseRequest.ps1"
request_tests="$repo_root/tests/ReleaseRequest.Tests.ps1"
evidence_tests="$repo_root/tests/ReleaseEvidence.Tests.ps1"
gitignore="$repo_root/.gitignore"

test -x "$release_local"
test -x "$publish_release"
test -x "$runner"
test -f "$legacy_release"
test -f "$evidence_builder"
test -f "$request_helper"
test -f "$request_tests"
test -f "$evidence_tests"
test -f "$gitignore"
bash -n "$release_local"
bash -n "$publish_release"
bash -n "$runner"
command -v python3 >/dev/null 2>&1 || {
    echo 'python3 is required for duplicate ZIP member fixture' >&2
    exit 1
}

rg -q --fixed-strings 'LC_ALL=C rg -c "^$name="' "$release_local"
rg -q --fixed-strings 'LC_ALL=C sed -n "s/^$name=//p"' "$release_local"
rg -q --fixed-strings 'exact_crlf_line_count' "$release_local"
rg -q --fixed-strings '\AppData\Local\Programs\WinWispr\WinWispr.exe' "$release_local"
rg -q --fixed-strings 'BUILD-INFO tag mismatch' "$release_local"
rg -q --fixed-strings 'CHANGELOG title mismatch' "$release_local"
rg -q --fixed-strings 'SECURITY-REPORT enterpriseReady=false count mismatch' "$release_local"
rg -q --fixed-strings "printf 'FAILED: %s: %s\\n' \"\$phase\" \"\$reason\"" "$release_local"
rg -q --fixed-strings -- '--dry-run --assume-release "$version"' "$release_local"
# App releases carry Setup, Portable and their sidecars; the models have their own release.
refute rg -q --fixed-strings 'WinWispr-Update-$tag.exe' "$publish_release"
refute rg -q --fixed-strings 'WinWispr-update-$tag.zip' "$publish_release"
refute rg -q --fixed-strings 'WinWispr-Update-' "$release_local"
rg -q --fixed-strings 'ASSETS=4' "$publish_release"
rg -q --fixed-strings 'BUILD-INFO signingThumbprint does not match eng/signing/code-signing.json' "$release_local"
refute rg -q --fixed-strings -- '--allow-unsigned' "$release_local"
rg -q --fixed-strings 'GitHub asset set mismatch' "$publish_release"
rg -q --fixed-strings 'Assert-AcceptanceLog' "$evidence_builder"
rg -q --fixed-strings 'Get-TreeFiles' "$evidence_builder"
refute rg -q --fixed-strings 'Get-ChildItem -LiteralPath $stagingDirectory -Recurse' "$evidence_builder"
refute rg -q --fixed-strings 'Get-ChildItem -LiteralPath $finalDirectory -Recurse' "$evidence_builder"
rg -q --fixed-strings 'APP_DATA=preserved' "$evidence_builder"
rg -q --fixed-strings 'APP_DATA=not-present' "$evidence_builder"
rg -q --fixed-strings 'Programs\WinWispr\WinWispr.exe' "$evidence_builder"
rg -q --fixed-strings 'artifacts/releases/' "$gitignore"
rg -q --fixed-strings 'artifacts/.vm-control/' "$gitignore"
rg -q --fixed-strings "eng/Release.ps1 is no longer a release entry point. Run ./eng/release-local.sh from the Mac Git workspace." "$legacy_release"
if rg -n --fixed-strings 'git tag' "$repo_root"/eng/*.ps1; then
    echo 'PowerShell release/build scripts must not create Git tags.' >&2
    exit 1
fi
if rg -n --fixed-strings 'git push' "$repo_root"/eng/*.ps1 "$release_local" "$runner"; then
    echo 'Release scripts must not push tags or artifacts.' >&2
    exit 1
fi
if rg -n 'GitHub release|github release|telemetry|updater' "$legacy_release" "$release_local" "$runner"; then
    echo 'Release scripts must stay local-only.' >&2
    exit 1
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-release-contract.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT

write_fake_transport() {
    local repo="$1"
    cat >"$repo/eng/deploy-windows-vm.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

scenario="${RELEASE_FAKE_SCENARIO:-ok}"
request=''
dry_run=0

die() {
    printf 'fake transport: %s\n' "$1" >&2
    exit 1
}

while (($# > 0)); do
    case "$1" in
        --release-request)
            shift
            (($# > 0)) || die 'missing --release-request value'
            request="$1"
            ;;
        --dry-run)
            dry_run=1
            ;;
        *)
            die "unknown option: $1"
            ;;
    esac
    shift
done

test -n "$request" || die 'missing release request'
test -f "$request" || die 'release request not found'
if [ -n "${RELEASE_FAKE_CALLED_FILE:-}" ]; then
    : >"$RELEASE_FAKE_CALLED_FILE"
fi

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
control_root="$repo_root/artifacts/.vm-control/release"

entries="$(zipinfo -1 "$request" | LC_ALL=C sort | tr '\n' ' ')"
test "$entries" = 'commits.rs previous-tag.txt source-commit.txt ' || die "bad request entries: $entries"

previous_tag="$(unzip -p "$request" previous-tag.txt | tr -d '\r\n')"
source_commit="$(unzip -p "$request" source-commit.txt | tr -d '\r\n')"
commits_file="$(mktemp "${TMPDIR:-/tmp}/winwispr-fake-commits.XXXXXX")"
unzip -p "$request" commits.rs >"$commits_file"
commit_count="$(awk 'BEGIN{RS=sprintf("%c",30); count=0} { if ($0 ~ /[^[:space:]]/) count++ } END{print count}' "$commits_file")"
test "$commit_count" != 0 || die 'empty commit range'
source_hash="$(printf 'source\n' | shasum -a 256 | awk '{print $1}')"
request_hash="$(shasum -a 256 "$request" | awk '{print $1}')"
printf 'SOURCE_ARCHIVE_SHA256=%s\nRELEASE_REQUEST_SHA256=%s\n' "$source_hash" "$request_hash"

major="${previous_tag#v}"
major="${major%%.*}"
minor_patch="${previous_tag#v$major.}"
minor="${minor_patch%%.*}"
patch="${minor_patch##*.}"
bump="$(awk '
BEGIN { RS=sprintf("%c",30); bump="none" }
{
    split($0, lines, /\n/)
    subject=lines[1]
    if (subject ~ /!.*:/ || $0 ~ /(^|\n)BREAKING CHANGE:/) {
        bump="major"
    } else if (subject ~ /^feat(:|\()/ && bump != "major") {
        bump="minor"
    } else if (subject ~ /^fix(:|\()/ && bump == "none") {
        bump="patch"
    } else if (subject ~ /^(docs|test|chore|refactor)(:|\()/) {
    } else {
        print "invalid commit subject: " subject >"/dev/stderr"
        exit 2
    }
}
END { print bump }
' "$commits_file")" || die 'invalid commit history'

case "$bump" in
    major) major=$((major + 1)); minor=0; patch=0 ;;
    minor) minor=$((minor + 1)); patch=0 ;;
    patch) patch=$((patch + 1)) ;;
    none) die 'no release-worthy commits' ;;
esac
version="$major.$minor.$patch"
tag="v$version"

case "$scenario" in
    transport-failure) exit 42 ;;
    transport-foreground-failure)
        printf 'foreground window at failure: Notepad (pid 4242)\n'
        printf 'Foreground: Calculator (pid 99)\n' >&2
        printf 'FAILED: desktop E2E: test lost the foreground\n' >&2
        exit 43
        ;;
    non-utf8-transport-output)
        printf 'worker \374\r\n'
        ;;
    duplicate-markers)
        printf 'RELEASE_VERSION=%s\nRELEASE_VERSION=%s\n' "$version" "$version"
        exit 0
        ;;
    malformed-markers)
        printf 'RELEASE_VERSION=1.2\nRELEASE_ARCHIVE=/tmp/nope\nRELEASE_SHA256=abc\nVERIFY=PASS\n'
        exit 0
        ;;
    change-head)
        git commit --allow-empty -qm 'chore: drift after transport'
        ;;
    dirty-after)
        printf 'drift\n' >after-transport.txt
        ;;
esac

if ((dry_run)); then
    printf 'RELEASE_VERSION=%s\n' "$version"
    exit 0
fi

mkdir -p "$control_root"
pack_root="$(mktemp -d "${TMPDIR:-/tmp}/winwispr-fake-pack.XXXXXX")"
trap 'rm -f "$commits_file"; rm -rf "$pack_root"' EXIT
mkdir -p "$pack_root/logs"
printf 'portable\n' >"$pack_root/WinWispr-portable-$tag.zip"
shasum -a 256 "$pack_root/WinWispr-portable-$tag.zip" | awk -v n="WinWispr-portable-$tag.zip" '{print $1 " *" n}' >"$pack_root/WinWispr-portable-$tag.zip.sha256"
printf 'installer\n' >"$pack_root/WinWispr-Setup-$tag.exe"
shasum -a 256 "$pack_root/WinWispr-Setup-$tag.exe" | awk -v n="WinWispr-Setup-$tag.exe" '{print $1 " *" n}' >"$pack_root/WinWispr-Setup-$tag.exe.sha256"
printf '{"bomFormat":"CycloneDX"}\n' >"$pack_root/SBOM.cdx.json"
case "$scenario" in
    missing-app-running)
        printf 'INTERACTIVE_SESSION=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=preserved\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    missing-app-data)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    wrong-app-data)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=changed\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    duplicate-app-data)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=preserved\r\nAPP_DATA=preserved\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    both-app-data)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=preserved\r\nAPP_DATA=not-present\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    extra-app-data)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=preserved\r\nAPP_DATA=changed\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    app-data-not-present)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=not-present\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    wrong-install-path)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Temp\\WinWispr.exe\r\nAPP_DATA=preserved\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    extra-install-path)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nINSTALL_PATH=C:\\Temp\\WinWispr.exe\r\nAPP_DATA=preserved\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
    *)
        printf 'INTERACTIVE_SESSION=1\r\nAPP_RUNNING=1\r\nINSTALL_PATH=C:\\Users\\user\\AppData\\Local\\Programs\\WinWispr\\WinWispr.exe\r\nAPP_DATA=preserved\r\nVERIFY=PASS\r\n' >"$pack_root/VM-ACCEPTANCE.txt"
        ;;
esac
case "$scenario" in
    changelog-mismatch)
        printf '\357\273\277# Wrong %s\r\n\r\nPrevious tag: %s\r\nSource commit: %s\r\n' "$tag" "$previous_tag" "$source_commit" >"$pack_root/CHANGELOG-$tag.md"
        ;;
    *)
        printf '\357\273\277# WinWispr %s\r\n\r\nPrevious tag: %s\r\nSource commit: %s\r\n\r\n## Changes\r\n' "$tag" "$previous_tag" "$source_commit" >"$pack_root/CHANGELOG-$tag.md"
        ;;
esac
if [ "$scenario" = 'security-report-duplicate' ]; then
    printf 'enterpriseReady=false\nenterpriseReady=false\nvmVerified=true\n' >"$pack_root/SECURITY-REPORT.md"
else
    printf 'enterpriseReady=false\nvmVerified=true\n' >"$pack_root/SECURITY-REPORT.md"
fi
printf 'notices\n' >"$pack_root/THIRD-PARTY-NOTICES.md"
build_commit="$source_commit"
build_previous="$previous_tag"
if [ "$scenario" = 'build-info-mismatch' ]; then
    build_commit='0000000000000000000000000000000000000000'
fi
build_tag="$tag"
if [ "$scenario" = 'build-info-tag-mismatch' ]; then
    build_tag='v9.9.9'
fi
build_signer="$(plutil -extract thumbprint raw -o - eng/signing/code-signing.json)"
if [ "$scenario" = 'build-info-signer-mismatch' ]; then
    build_signer='0000000000000000000000000000000000000000'
fi
cat >"$pack_root/BUILD-INFO.json" <<JSON
{
  "version": "$version",
  "tag": "$build_tag",
  "previousTag": "$build_previous",
  "sourceCommit": "$build_commit",
  "sourceArchiveSha256": "$source_hash",
  "vmVerified": true,
  "enterpriseReady": false,
  "signingThumbprint": "$build_signer",
  "modelPackage": "WinWispr-Models-v1.exe"
}
JSON
printf 'runtime log\n' >"$pack_root/logs/runtime.log"

if [ "$scenario" = 'unsafe-symlink' ]; then
    ln -s BUILD-INFO.json "$pack_root/logs/link"
fi

(
    cd "$pack_root"
    find . -type f ! -name SHA256SUMS.txt | sed 's#^\./##' | LC_ALL=C sort |
        while IFS= read -r file; do
            if [ "$scenario" = 'inner-manifest-mismatch' ] && [ "$file" = 'BUILD-INFO.json' ]; then
                printf '%064d *%s\r\n' 0 "$file"
            else
                shasum -a 256 "$file" | awk -v n="$file" '{printf "%s *%s\r\n", $1, n}'
            fi
        done >SHA256SUMS.txt
    if [ "$scenario" = 'manifest-path-escape' ]; then
        printf '%064d *../evil\r\n' 0 >>SHA256SUMS.txt
    fi
)

archive="$control_root/WinWispr-release-$tag.zip"
case "$scenario" in
    unsafe-zip-name)
        printf 'evil\n' >"$pack_root/evil"
        (cd "$pack_root"; find . -type f | sed 's#^\./##' | zip -q -X -@ "$archive")
        (cd "$pack_root"; zip -q "$archive" evil)
        ;;
    unsafe-symlink)
        (cd "$pack_root"; find . \( -type f -o -type l \) | sed 's#^\./##' | zip -q -X -y -@ "$archive")
        ;;
    *)
        (cd "$pack_root"; find . -type f | sed 's#^\./##' | zip -q -X -@ "$archive")
        ;;
esac
if [ "$scenario" = 'duplicate-zip-member' ]; then
    python3 - "$archive" "$pack_root/BUILD-INFO.json" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "a") as archive:
    archive.write(sys.argv[2], "BUILD-INFO.json")
PY
fi
actual="$(shasum -a 256 "$archive" | awk '{print $1}')"
sidecar_hash="$actual"
if [ "$scenario" = 'corrupt-sidecar' ]; then
    sidecar_hash='1111111111111111111111111111111111111111111111111111111111111111'
fi
printf '%s *%s\r\n' "$sidecar_hash" "$(basename "$archive")" >"$archive.sha256"
printf 'RELEASE_VERSION=%s\nRELEASE_ARCHIVE=%s\nRELEASE_SHA256=%s\nAPP_RUNNING=1\nVERIFY=PASS\n' "$version" "$archive" "$actual"
EOF
    chmod +x "$repo/eng/deploy-windows-vm.sh"
}

setup_release_repo() {
    local name="$1"
    local repo="$tmp_dir/$name"
    mkdir -p "$repo/eng" "$repo/.githooks"
    cp "$release_local" "$repo/eng/release-local.sh"
    cp "$repo_root/eng/prune-artifacts.sh" "$repo/eng/prune-artifacts.sh"
    mkdir -p "$repo/eng/signing"
    cp "$repo_root/eng/signing/code-signing.json" "$repo/eng/signing/code-signing.json"
    chmod +x "$repo/eng/release-local.sh"
    write_fake_transport "$repo"
    (
        cd "$repo"
        git init -q
        git config user.email test@example.invalid
        git config user.name 'Release Contract'
        git config core.hooksPath .githooks
        printf '#!/usr/bin/env bash\nexit 0\n' >.githooks/commit-msg
        chmod +x .githooks/commit-msg
        printf 'base\n' >README.md
        printf 'artifacts/releases/\nartifacts/.vm-control/\n' >.gitignore
        git add README.md .gitignore eng .githooks
        git commit -qm 'chore: baseline'
        git tag -a v1.2.3 -m 'bootstrap'
        git commit --allow-empty -qm 'feat: releasable change'
    )
    printf '%s\n' "$repo"
}

assert_no_release_mutation() {
    local repo="$1"
    local tag="$2"
    if git -C "$repo" tag --list "$tag" | rg -q .; then
        echo "unexpected tag exists: $tag" >&2
        exit 1
    fi
    if [ -e "$repo/artifacts/releases/$tag" ]; then
        echo "unexpected release directory exists: $tag" >&2
        exit 1
    fi
    if [ -d "$repo/artifacts/releases" ] &&
        find "$repo/artifacts/releases" \( -name '.staging-*' -o -name '.claim-*' -o -name '.winwispr-release-owner' \) | rg -q .; then
        echo "unexpected release ownership residue under artifacts/releases" >&2
        exit 1
    fi
    if [ -e "$repo/artifacts/.vm-control" ]; then
        echo 'unexpected VM control residue under artifacts/.vm-control' >&2
        exit 1
    fi
}

# Every failure ends with the shared FAILED line as the last output line.
expect_failed_line() {
    local label="$1" phase="$2" last
    last="$(tail -n 1 "$tmp_dir/$label.out")"
    # $phase is a glob on purpose ('*' accepts any phase).
    # shellcheck disable=SC2053
    if ! [[ "$last" == "FAILED: "$phase": "?* ]]; then
        printf 'last line of %s must be "FAILED: %s: <reason>", got: %s\n' "$label" "$phase" "$last" >&2
        exit 1
    fi
}

expect_fail() {
    local label="$1"
    shift
    if "$@" >"$tmp_dir/$label.out" 2>&1; then
        echo "expected failure: $label" >&2
        exit 1
    fi
    expect_failed_line "$label" '*'
}

repo="$(setup_release_repo happy)"
(
    cd "$repo"
    ./eng/release-local.sh --self-signed >"$tmp_dir/happy.out"
    rg -q '^SOURCE_ARCHIVE_SHA256=[0-9a-f]{64}$' "$tmp_dir/happy.out"
    rg -q '^RELEASE_REQUEST_SHA256=[0-9a-f]{64}$' "$tmp_dir/happy.out"
    refute rg -q '^REMOTE_' "$tmp_dir/happy.out"
    rg -q '^APP_RUNNING=1$' "$tmp_dir/happy.out"
    test "$(git tag --points-at HEAD --list v1.3.0)" = 'v1.3.0'
    test -f artifacts/releases/v1.3.0/BUILD-INFO.json
)

repo="$(setup_release_repo non-utf8-transport-output)"
(
    cd "$repo"
    LC_ALL=en_US.UTF-8 RELEASE_FAKE_SCENARIO=non-utf8-transport-output ./eng/release-local.sh --self-signed >"$tmp_dir/non-utf8.out"
    rg -q '^APP_RUNNING=1$' "$tmp_dir/non-utf8.out"
    test "$(git tag --points-at HEAD --list v1.3.0)" = 'v1.3.0'
    test -f artifacts/releases/v1.3.0/BUILD-INFO.json
)

repo="$(setup_release_repo app-data-not-present)"
(
    cd "$repo"
    RELEASE_FAKE_SCENARIO=app-data-not-present ./eng/release-local.sh --self-signed >"$tmp_dir/app-data-not-present.out"
    rg -q '^APP_RUNNING=1$' "$tmp_dir/app-data-not-present.out"
    test "$(git tag --points-at HEAD --list v1.3.0)" = 'v1.3.0'
    test -f artifacts/releases/v1.3.0/VM-ACCEPTANCE.txt
)

repo="$(setup_release_repo dry)"
(
    cd "$repo"
    ./eng/release-local.sh --dry-run >"$tmp_dir/dry.out"
    rg -q '^SOURCE_ARCHIVE_SHA256=[0-9a-f]{64}$' "$tmp_dir/dry.out"
    rg -q '^RELEASE_REQUEST_SHA256=[0-9a-f]{64}$' "$tmp_dir/dry.out"
    refute rg -q '^REMOTE_' "$tmp_dir/dry.out"
    assert_no_release_mutation "$repo" v1.3.0
)

# --dry-run previews the prune that ends a real release: the new release (v1.3.0)
# is not on disk yet, so it takes one of the three kept slots and the oldest
# of the three existing releases (v1.0.0) is listed. Nothing is removed.
repo="$(setup_release_repo dry-prune)"
(
    cd "$repo"
    for version in 1.0.0 1.1.0 1.2.0; do
        mkdir -p "artifacts/releases/v$version"
        printf 'evidence\n' >"artifacts/releases/v$version/BUILD-INFO.json"
    done
    ./eng/release-local.sh --dry-run >"$tmp_dir/dry-prune.out"
    rg -q '^PRUNE_PREVIEW=.*v1\.3\.0' "$tmp_dir/dry-prune.out"
    rg -q '^PRUNE_ASSUMED_RELEASE=v1\.3\.0$' "$tmp_dir/dry-prune.out"
    rg -q '^PRUNE_MODE=dry-run$' "$tmp_dir/dry-prune.out"
    rg -q "^PRUNE_WOULD_REMOVE=.*/artifacts/releases/v1\.0\.0 \([0-9]+ MB, " "$tmp_dir/dry-prune.out"
    refute rg -q 'artifacts/releases/v1\.[12]\.0' "$tmp_dir/dry-prune.out"
    rg -q '^DRY_RUN=PASS$' "$tmp_dir/dry-prune.out"
    for version in 1.0.0 1.1.0 1.2.0; do
        test -f "artifacts/releases/v$version/BUILD-INFO.json"
    done
    assert_no_release_mutation "$repo" v1.3.0
)

repo="$(setup_release_repo dirty-untracked)"
(cd "$repo"; printf 'dirty\n' >untracked.txt; expect_fail dirty-untracked ./eng/release-local.sh --self-signed; assert_no_release_mutation "$repo" v1.3.0)

repo="$(setup_release_repo dirty-tracked)"
(cd "$repo"; printf 'dirty\n' >>README.md; expect_fail dirty-tracked ./eng/release-local.sh --self-signed; assert_no_release_mutation "$repo" v1.3.0)

repo="$(setup_release_repo no-bootstrap)"
(cd "$repo"; git tag -d v1.2.3 >/dev/null; expect_fail no-bootstrap ./eng/release-local.sh --self-signed)

repo="$(setup_release_repo hooks-inactive)"
(cd "$repo"; git config --unset core.hooksPath; expect_fail hooks-inactive ./eng/release-local.sh --self-signed; assert_no_release_mutation "$repo" v1.3.0)

repo="$(setup_release_repo no-release)"
(cd "$repo"; git reset --hard v1.2.3 >/dev/null; git commit --allow-empty -qm 'docs: no release'; expect_fail no-release ./eng/release-local.sh --self-signed; assert_no_release_mutation "$repo" v1.3.0)

repo="$(setup_release_repo invalid-commit)"
(cd "$repo"; git reset --hard v1.2.3 >/dev/null; git -c core.hooksPath=/dev/null commit --allow-empty -qm 'update release'; expect_fail invalid-commit ./eng/release-local.sh --self-signed; assert_no_release_mutation "$repo" v1.3.0)

repo="$(setup_release_repo empty-commit-message)"
(
    cd "$repo"
    git reset --hard v1.2.3 >/dev/null
    empty="$tmp_dir/empty-message.txt"
    : >"$empty"
    new_commit="$(git commit-tree HEAD^{tree} -p HEAD <"$empty")"
    git reset --hard "$new_commit" >/dev/null
    expect_fail empty-message ./eng/release-local.sh --self-signed
    assert_no_release_mutation "$repo" v1.3.0
)

repo="$(setup_release_repo embedded-rs)"
(
    cd "$repo"
    git reset --hard v1.2.3 >/dev/null
    msg="$tmp_dir/rs-message.txt"
    printf 'feat: bad rs\n\nbody \036 marker\n' >"$msg"
    new_commit="$(git commit-tree HEAD^{tree} -p HEAD <"$msg")"
    git reset --hard "$new_commit" >/dev/null
    expect_fail embedded-rs ./eng/release-local.sh --self-signed
    assert_no_release_mutation "$repo" v1.3.0
)

for scenario in transport-failure transport-foreground-failure duplicate-markers malformed-markers corrupt-sidecar unsafe-zip-name unsafe-symlink duplicate-zip-member inner-manifest-mismatch manifest-path-escape build-info-mismatch build-info-tag-mismatch build-info-signer-mismatch missing-app-running missing-app-data wrong-app-data duplicate-app-data both-app-data extra-app-data wrong-install-path extra-install-path changelog-mismatch security-report-duplicate change-head dirty-after; do
    repo="$(setup_release_repo "$scenario")"
    (cd "$repo"; RELEASE_FAKE_SCENARIO="$scenario" expect_fail "$scenario" ./eng/release-local.sh --self-signed; assert_no_release_mutation "$repo" v1.3.0)
done

# The FAILED line names the phase that failed.
expect_failed_line transport-failure 'VM transport'
expect_failed_line transport-foreground-failure 'VM transport'
expect_failed_line duplicate-markers 'transport markers'
expect_failed_line malformed-markers 'transport markers'
for scenario in corrupt-sidecar unsafe-zip-name inner-manifest-mismatch build-info-mismatch changelog-mismatch; do
    expect_failed_line "$scenario" 'release evidence validation'
done
expect_failed_line dirty-tracked 'preflight'
expect_failed_line hooks-inactive 'preflight'

# A failed desktop run repeats the foreground-window lines right above FAILED
# and names the kept full log, which holds the transport stdout and stderr.
fg_out="$tmp_dir/transport-foreground-failure.out"
tail -n 4 "$fg_out" | head -n 3 | rg -q --fixed-strings 'full log:'
test "$(tail -n 3 "$fg_out" | sed -n 1p)" = 'foreground window at failure: Notepad (pid 4242)'
test "$(tail -n 2 "$fg_out" | sed -n 1p)" = 'Foreground: Calculator (pid 99)'
full_log="$(sed -n 's/^release-local: full log: //p' "$fg_out" | tail -n 1)"
test -f "$full_log"
rg -q --fixed-strings 'foreground window at failure: Notepad (pid 4242)' "$full_log"
rg -q --fixed-strings 'Foreground: Calculator (pid 99)' "$full_log"
case "$full_log" in
    */transport-foreground-failure/artifacts/dev-check/release-local-*.log) ;;
    *) echo "full log must be kept under artifacts/dev-check: $full_log" >&2; exit 1 ;;
esac

# The runner's own FAILED reason is carried into the release line.
test "$(tail -n 1 "$fg_out")" = 'FAILED: VM transport: deploy-windows-vm.sh failed (desktop E2E: test lost the foreground); see the lines above'

# Usage errors end with the line too.
(
    cd "$tmp_dir/transport-foreground-failure"
    expect_fail bogus-option ./eng/release-local.sh --bogus
    expect_failed_line bogus-option 'startup'
    expect_fail no-option ./eng/release-local.sh
    expect_failed_line no-option 'startup'
)

repo="$(setup_release_repo promote-race)"
(
    cd "$repo"
    fake_bin="$tmp_dir/promote-race-bin"
    mkdir -p "$fake_bin"
    real_mv="$(command -v mv)"
    cat >"$fake_bin/mv" <<EOF
#!/usr/bin/env bash
if [ "\$#" = 2 ]; then
    case "\$2" in
        */artifacts/releases/v1.3.0)
            mkdir -p "\$2"
            printf 'foreign\n' >"\$2/sentinel.txt"
            ;;
    esac
fi
exec "$real_mv" "\$@"
EOF
    chmod +x "$fake_bin/mv"
    PATH="$fake_bin:$PATH" expect_fail promote-race ./eng/release-local.sh --self-signed
    expect_failed_line promote-race 'release promotion'
    test -f artifacts/releases/v1.3.0/sentinel.txt
    test "$(cat artifacts/releases/v1.3.0/sentinel.txt)" = 'foreign'
    test ! -d artifacts/releases/v1.3.0/.staging-*
    test -z "$(git tag --list v1.3.0)"
    if find artifacts/releases \( -name '.staging-*' -o -name '.claim-*' -o -name '.winwispr-release-owner' \) | rg -q .; then
        echo 'promotion race left release ownership residue' >&2
        exit 1
    fi
)

repo="$(setup_release_repo existing-dir)"
(cd "$repo"; mkdir -p artifacts/releases/v1.3.0; RELEASE_FAKE_CALLED_FILE="$repo/transport.called" expect_fail existing-dir ./eng/release-local.sh --self-signed; test ! -f transport.called)

repo="$(setup_release_repo existing-tag)"
(
    cd "$repo"
    current="$(git rev-parse HEAD)"
    git checkout --orphan side >/dev/null 2>&1
    git rm -rf . >/dev/null 2>&1 || true
    printf 'side\n' >SIDE.md
    git add SIDE.md
    git commit -qm 'chore: side'
    git tag -a v1.3.0 -m 'reserved'
    git checkout -q "$current"
    RELEASE_FAKE_CALLED_FILE="$repo/transport.called" expect_fail existing-tag ./eng/release-local.sh --self-signed
    test ! -f transport.called
)

repo="$(setup_release_repo tag-failure)"
(
    cd "$repo"
    fake_bin="$tmp_dir/tag-failure-bin"
    mkdir -p "$fake_bin"
    real_git="$(command -v git)"
    cat >"$fake_bin/git" <<EOF
#!/usr/bin/env bash
args=("\$@")
index=0
if [ "\${args[0]:-}" = -C ]; then
    index=2
fi
if [ "\${args[\$index]:-}" = tag ] && [ "\${args[\$((index + 1))]:-}" = -a ]; then
    exit 66
fi
exec "$real_git" "\$@"
EOF
    chmod +x "$fake_bin/git"
    PATH="$fake_bin:$PATH" expect_fail tag-failure ./eng/release-local.sh --self-signed
    expect_failed_line tag-failure 'release tag'
    assert_no_release_mutation "$repo" v1.3.0
)

repo="$(setup_release_repo tag-partial-success)"
(
    cd "$repo"
    fake_bin="$tmp_dir/tag-partial-bin"
    mkdir -p "$fake_bin"
    real_git="$(command -v git)"
    cat >"$fake_bin/git" <<EOF
#!/usr/bin/env bash
args=("\$@")
index=0
if [ "\${args[0]:-}" = -C ]; then
    index=2
fi
if [ "\${args[\$index]:-}" = tag ] && [ "\${args[\$((index + 1))]:-}" = -a ]; then
    "$real_git" "\$@"
    exit 66
fi
exec "$real_git" "\$@"
EOF
    chmod +x "$fake_bin/git"
    PATH="$fake_bin:$PATH" expect_fail tag-partial-success ./eng/release-local.sh --self-signed
    test "$(git tag --points-at HEAD --list v1.3.0)" = 'v1.3.0'
    test "$(git cat-file -t refs/tags/v1.3.0)" = 'tag'
    test -f artifacts/releases/v1.3.0/BUILD-INFO.json
    assert_no_release_residue="$(find artifacts/releases \( -name '.staging-*' -o -name '.claim-*' -o -name '.winwispr-release-owner' \) | wc -l | tr -d ' ')"
    test "$assert_no_release_residue" = 0
    refute rg -q '^STATUS=PASS$' "$tmp_dir/tag-partial-success.out"
)

repo="$(setup_release_repo tag-lightweight-nonzero)"
(
    cd "$repo"
    fake_bin="$tmp_dir/tag-lightweight-bin"
    mkdir -p "$fake_bin"
    real_git="$(command -v git)"
    cat >"$fake_bin/git" <<EOF
#!/usr/bin/env bash
args=("\$@")
index=0
if [ "\${args[0]:-}" = -C ]; then
    index=2
fi
if [ "\${args[\$index]:-}" = tag ] && [ "\${args[\$((index + 1))]:-}" = -a ]; then
    repo="."
    if [ "\${args[0]:-}" = -C ]; then
        repo="\${args[1]}"
    fi
    "$real_git" -C "\$repo" tag v1.3.0 HEAD
    exit 66
fi
exec "$real_git" "\$@"
EOF
    chmod +x "$fake_bin/git"
    PATH="$fake_bin:$PATH" expect_fail tag-lightweight-nonzero ./eng/release-local.sh --self-signed
    test "$(git tag --points-at HEAD --list v1.3.0)" = 'v1.3.0'
    test "$(git cat-file -t refs/tags/v1.3.0)" = 'commit'
    test ! -e artifacts/releases/v1.3.0
    assert_no_release_residue="$(find artifacts/releases \( -name '.staging-*' -o -name '.claim-*' -o -name '.winwispr-release-owner' \) | wc -l | tr -d ' ')"
    test "$assert_no_release_residue" = 0
    refute rg -q '^STATUS=PASS$' "$tmp_dir/tag-lightweight-nonzero.out"
)

repo="$(setup_release_repo tag-signal-after-create)"
(
    cd "$repo"
    fake_bin="$tmp_dir/tag-signal-bin"
    mkdir -p "$fake_bin"
    real_git="$(command -v git)"
    cat >"$fake_bin/git" <<EOF
#!/usr/bin/env bash
args=("\$@")
index=0
if [ "\${args[0]:-}" = -C ]; then
    index=2
fi
if [ "\${args[\$index]:-}" = tag ] && [ "\${args[\$((index + 1))]:-}" = -a ]; then
    "$real_git" "\$@"
    kill -TERM "\$PPID"
    sleep 1
    exit 66
fi
exec "$real_git" "\$@"
EOF
    chmod +x "$fake_bin/git"
    if PATH="$fake_bin:$PATH" ./eng/release-local.sh --self-signed >"$tmp_dir/tag-signal-after-create.out" 2>&1; then
        echo 'expected signal-window release command to fail' >&2
        exit 1
    fi
    test "$(git tag --points-at HEAD --list v1.3.0)" = 'v1.3.0'
    test "$(git cat-file -t refs/tags/v1.3.0)" = 'tag'
    test -f artifacts/releases/v1.3.0/BUILD-INFO.json
    assert_no_release_residue="$(find artifacts/releases \( -name '.staging-*' -o -name '.claim-*' -o -name '.winwispr-release-owner' \) | wc -l | tr -d ' ')"
    test "$assert_no_release_residue" = 0
    refute rg -q '^STATUS=PASS$' "$tmp_dir/tag-signal-after-create.out"
    expect_failed_line tag-signal-after-create 'release tag'
    rg -q --fixed-strings 'interrupted by a signal' "$tmp_dir/tag-signal-after-create.out"
)

if [ "$(uname -s)" = Darwin ]; then
    # The PowerShell release tests target Windows paths, git.exe, and NTFS
    # reparse-point semantics; macOS /var is a symlink they correctly reject.
    echo 'SKIP: PowerShell release tests are Windows-only; static release contract checks passed.'
    exit 0
fi

runner=''
if command -v pwsh >/dev/null 2>&1; then
    runner="$(command -v pwsh)"
elif command -v powershell >/dev/null 2>&1; then
    runner="$(command -v powershell)"
fi

if [ -z "$runner" ]; then
    echo 'SKIP: no PowerShell runner on PATH; static release contract checks passed.'
    exit 0
fi

"$runner" -NoLogo -NoProfile -File "$evidence_tests"
