#!/usr/bin/env bash
set -euo pipefail

# Contract of the bundled translation model: ModelManifest.Translation, the list the app checks in the package's
# TranslationData folder, names exactly the ten files that eng/translation-model-pins.json pins (same path, size and
# SHA-256); the app has no download path for it; and nothing of the abandoned mirrored archive design is left in the
# documents, the sources or the engineering scripts.

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
pins="$repo_root/eng/translation-model-pins.json"
manifest_cs="$repo_root/src/WinWispr/Speech/ModelManifest.cs"

test -f "$pins"
test -f "$manifest_cs"

python3 - "$pins" "$manifest_cs" <<'PY'
import json
import re
import sys
from pathlib import Path

pins = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
manifest = Path(sys.argv[2]).read_text(encoding="utf-8")


def fail(message):
    print(f"translation-model-contract: {message}", file=sys.stderr)
    sys.exit(1)


block = re.search(r"BundledModelSet Translation\s*=\s*new\((.*?)\);\r?\n", manifest, re.S)
if not block:
    fail("ModelManifest.cs has no bundled Translation set")
body = block.group(1)

if 'PackageDirectory: "TranslationData"' not in body:
    fail("the translation model must be read from the package's TranslationData folder")
if "Uri" in body or "http" in body:
    fail("the bundled translation model must not name a download source")

entries = re.findall(r'new\("([^"]+)",\s*([0-9_]+),\s*"([0-9a-f]{64})"\)', body)
actual = {(name, int(size.replace("_", "")), sha) for name, size, sha in entries}
if len(actual) != len(entries):
    fail("the translation manifest lists a file twice")

expected = set()
for direction, spec in pins["directions"].items():
    for file in spec["files"]:
        expected.add((f"{spec['folder']}/{file['path']}", file["bytes"], file["sha256"]))

if len(expected) != 10:
    fail(f"the pins must list ten files, found {len(expected)}")
if actual != expected:
    for entry in sorted(actual - expected):
        print(f"only in ModelManifest.cs: {entry}", file=sys.stderr)
    for entry in sorted(expected - actual):
        print(f"only in translation-model-pins.json: {entry}", file=sys.stderr)
    fail("ModelManifest.Translation and eng/translation-model-pins.json disagree")

if "release_tag" in pins:
    fail("the pins still name a release tag; nothing is published for the translation model")
if sum(size for _, size, _ in actual) != 217_948_232:
    fail("the ten files no longer add up to the qualified 217,948,232 bytes")
PY

# Nothing of the mirrored-archive design may remain. Explicit checks: a negated command does not stop a script
# that runs under set -e.
if [ -e "$repo_root/eng/build-translation-archive.py" ] || [ -e "$repo_root/tests/translation-archive-contract.sh" ]; then
    echo 'The translation archive builder or its contract test is back.' >&2
    exit 1
fi
if rg -l -i 'models-translation-v1|build-translation-archive|translation-model-archive|opus-mt-int8-v1\.tar' \
    "$repo_root/README.md" "$repo_root/SECURITY.md" "$repo_root/CHANGELOG.md" "$repo_root/THIRD-PARTY-NOTICES.md" \
    "$repo_root/docs" "$repo_root/src" "$repo_root/eng" "$repo_root/installer" "$repo_root/tests/WinWispr.Tests" \
    "$repo_root/tests/WinWispr.SettingsUiAcceptance"; then
    echo 'A document, source or script still refers to the mirrored translation archive.' >&2
    exit 1
fi
# The local packaging input is ignored by Git, like the speech model and the voices.
rg -q --fixed-strings 'artifacts/translation-model/' "$repo_root/.gitignore"

# The app never downloads the translation model: no download path, no Settings action, no network source.
if rg -n 'TranslationModelSetup|CreateTranslation|ModelFileSetSpec|ModelDownloadFile|TranslationDownload' "$repo_root/src"; then
    echo 'The app still has a download path for the translation model.' >&2
    exit 1
fi
if rg -n -i 'huggingface|hf\.co' "$repo_root/src"; then
    echo 'The app source still names a Hugging Face download source.' >&2
    exit 1
fi

echo 'translation model contract: PASS'
