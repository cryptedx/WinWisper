#!/usr/bin/env bash
set -euo pipefail

# The in-app update check trusts only Setups signed with the certificate the release flow signs with.
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
pin="$repo_root/eng/signing/code-signing.json"
service="$repo_root/src/WinWispr/Core/Updates/UpdateService.cs"
feed="$repo_root/src/WinWispr/Core/Updates/ReleaseFeed.cs"
release_script="$repo_root/eng/publish-release.sh"

thumbprint="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["thumbprint"])' "$pin")"
rg -q --fixed-strings "SigningThumbprint = \"$thumbprint\"" "$service" || {
    echo "UpdateService.SigningThumbprint must equal the pinned thumbprint $thumbprint in eng/signing/code-signing.json" >&2
    exit 1
}

# The checked repository is the one releases are published to.
repository="$(sed -n "s/^repository='\([^']*\)'$/\1/p" "$release_script" | head -n 1)"
[ -n "$repository" ] || { echo 'could not read the release repository from eng/publish-release.sh' >&2; exit 1; }
rg -q --fixed-strings "Repository = \"$repository\"" "$feed" || {
    echo "ReleaseFeed.Repository must equal the release repository $repository" >&2
    exit 1
}
