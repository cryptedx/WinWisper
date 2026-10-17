---
status: accepted
---

# Check GitHub releases for updates and install a verified Setup on request

WinWispr checks the latest GitHub release of `cryptedx/WinWisper` and offers a newer version in a tray balloon. When the user clicks it, the app downloads Setup, verifies its hash and its signature against the pinned self-signed certificate, quits and runs Setup silently. This ends the earlier boundary that the application makes no network request once WinWispr Models is installed ([ADR 0002](0002-separate-model-package-and-self-signed-code-signing.md)): the update check is the one exception, it is opt-out, and an administrator can turn it off by policy.

## Why

Since Setup carries no models it is a small download (about 80 MB) and is itself the update, but users only learned of a new version by watching the release page. Running an outdated build also means missing fixes. A check that reads public release metadata and installs only a file that passes the same verification as a deployment closes that gap without a hosted update service of our own.

## How it works

- **When.** 30 seconds after start and then every 24 hours, while the setting **Check for updates automatically** (`AutomaticUpdateChecks`, on by default, **Settings → General**) is on. **Check for updates** in the tray menu and on the General page checks on demand, whatever the setting.
- **What it reads.** One anonymous request to the GitHub REST API for the latest release of `cryptedx/WinWisper`. Only tags of the form `vX.Y.Z` count; model releases `models-vN` are never offered. The request carries a `User-Agent` with the app version and nothing else: no account, no token, no device or usage data. Nothing is sent anywhere else.
- **The offer.** If the release is newer than the running version, a tray balloon says *WinWispr X.Y.Z is available. Click to update.* Nothing is downloaded until the user clicks it.
- **Download and verification.** WinWispr downloads `WinWispr-Setup-vX.Y.Z.exe` and its `.sha256` sidecar to `%LOCALAPPDATA%\WinWispr\Updates\vX.Y.Z\`. It installs the file only if its SHA-256 matches both the sidecar and the digest GitHub reports for the asset, its Authenticode signature is intact, and the signer's thumbprint equals the pinned WinWispr certificate (`F8D419ED06D2038754C6BB2E5042BBF6DBE5903C`). Any mismatch stops the update and keeps the running version.
- **Installation.** WinWispr hands the verified Setup to a hidden Windows PowerShell helper and quits. The helper waits for WinWispr to exit, runs Setup silently into the same folder and scope it was installed with (a per-user installation with `/CURRENTUSER`, a per-machine installation with `/ALLUSERS`, which shows one UAC prompt), and then starts WinWispr again unelevated, the new version or, if Setup did not run, the old one. Setup itself stays without a `[Run]` section: the installer contract forbids execution entries in the installer script. Settings, the personal dictionary and installed models are kept, as with any Setup run.
- **Portable copies** are not changed in place; the balloon opens the release page instead.
- **Policy.** The DWORD value `DisableUpdateCheck` = `1` under `HKLM\Software\Policies\WinWispr` disables both the automatic and the manual check.

## Why the pinned certificate and not Windows trust

The releases are signed with WinWispr's own self-signed certificate, which most machines do not trust, so a plain "is the signature trusted" check would reject every genuine update there. Pinning the signer thumbprint instead accepts exactly the files signed with the WinWispr key and nothing else, on any machine. The hash checks against the sidecar and GitHub's asset digest catch a damaged or replaced download; the pin is what prevents a valid signature from another publisher from passing.

## Considered options

- **No updater, release page only.** The previous state. Users stay on old versions and must compare version numbers themselves.
- **Windows Package Manager or the Microsoft Store.** Requires package identity or a manifest in a public repository, and does not fit the self-signed certificate.
- **A general update framework.** More moving parts and its own update feed and signing keys, for a single user's tool with one installer.

## Consequences

- The GitHub repository must be public: the check and the download are anonymous.
- WinWispr now contacts `api.github.com`, `github.com` and GitHub's release-asset hosts; organizations that do not want this set the policy value or block those hosts. Dictation, translation and the speech model remain offline.
- The update path trusts whoever holds the WinWispr private key on the build VM. Losing or replacing that key means a new pin, and installed versions with the old pin cannot update themselves to a build signed with the new key; those users install the next Setup by hand.
- A release must keep publishing Setup, its `.sha256` sidecar and the GitHub asset digest, or installed versions cannot update.
- The check is not a replacement for enterprise software distribution: there is no staged rollout, no rollback, and no signature from a certificate the organization trusts.
