# WinWispr enterprise security and transparency

## Purpose and release boundary

WinWispr is an unpackaged, local Windows 11 dictation application. Its
supported release path is local Git only:

```bash
./eng/install-git-hooks.sh
./eng/release-local.sh --dry-run
./eng/release-local.sh --self-signed
```

`eng/Release.ps1 is not a supported release entry point`. `hooks validate and
remind; they never build, tag, push, publish, or start a release
automatically`; `the developer consciously starts the release`;
`the complete commit range is revalidated fail-closed`; `VM acceptance passes
before the local annotated tag is created last`. `SSH host-key trust stays at
the configured known_hosts entry for the VM host (windows-vm)`. The local release tooling does not push,
publish, or create a hosted release, and adds no application telemetry service.
Publishing the GitHub release is a separate, explicit step; the application's
update check reads that public release (see [Update check](#update-check)).

Releases are signed with WinWispr's own self-signed code-signing certificate
(`CN=WinWispr`); the output is explicitly internal and non-enterprise-ready.
The enterprise-release gate is defined in `SECURITY.md`. The repository keeps
only the public certificate and its pin (`eng/signing/`); the non-exportable
private key exists only in the build VM's user certificate store.

## Data inventory and retention

| Data | Location and use | Retention / deletion |
| --- | --- | --- |
| Settings | `%LOCALAPPDATA%\WinWispr\settings.json`: recording mode, configured hotkeys (including the translate hotkey, the English dictation hotkey and its mode), optional microphone device identifier, `AutomaticUpdateChecks` | Kept until the user or uninstaller removes app data. |
| Downloaded updates | `%LOCALAPPDATA%\WinWispr\Updates\vX.Y.Z\`: `WinWispr-Setup-vX.Y.Z.exe` and its `.sha256` sidecar, downloaded only after the user clicks an update offer | Kept until the user or uninstaller removes app data; a file that fails verification is never run. |
| Model | `%LOCALAPPDATA%\WinWispr\Models\`: verified model files and active-model pointer | Replaced model installation removes the previous active installation; uninstall keeps it unless the user explicitly confirms deletion. |
| Translation model | `TranslationData\` in the installation folder: the ten verified Opus-MT translation files of both directions (German to English, English to German), read in place | Installed by the WinWispr Models installer and removed by its uninstaller, which uninstalling WinWispr runs first; Setup leaves it in place. WinWispr never writes to it. If a file is missing or has the wrong size, Settings reports the model as not installed and running the Models installer restores it. A copy in `%LOCALAPPDATA%\WinWispr\TranslationModels\` that an earlier version downloaded is no longer used and is deleted on the first start with the bundled model. |
| Audio | Captured in memory and passed to the local speech runtime; normal completion creates a `float[]` sample array for recognition | Cancel and shutdown discard the capture buffer; after normal recognition the sample array becomes eligible for garbage collection, with no explicit zeroing or timing guarantee. The application does not write captured audio files. |
| Recognized text | Sent to the Windows clipboard and, when the foreground window remains unchanged, pasted into that window | No WinWispr transcript/history store. Clipboard contents and any destination application's retention are controlled by Windows and that application. |
| Translated text | Selected text, clipboard text and dictation transcripts are translated in memory by the local translation model; the source and the translation are shown in the preview or pasted. The text is not stored, logged or sent to any service. | No WinWispr store. The translation (and, for a failed dictation translation, the German transcript) can stay on the clipboard, excluded from Windows clipboard history and roaming; the application you copy a selection from may record its own copy in clipboard history. |
| Technical logs | `%LOCALAPPDATA%\WinWispr\Logs\*.jsonl`: timestamp, event type, state, exception type, HRESULT, duration, delivery result | Entries older than 7 days are removed; total retained log size is capped at 1 MiB. Exception messages are not written. |
| Release evidence | Local `artifacts/releases/vX.Y.Z/` during a local release | Retained by the release owner under the organization's evidence-retention policy; the directory is ignored by Git. |

No application audio archive, transcript history, clipboard history, account
profile, API key, or private certificate store is implemented by this source.
The application data directory is not encrypted by WinWispr; Windows account,
disk-encryption, backup, and endpoint controls remain necessary.
The VM release path builds from the repository mirror `C:\windev\WinWispr\src`,
an rsync copy of the Mac checkout, and uses `C:\windev\WinWispr\src\artifacts`
for build output plus
`%USERPROFILE%\WinWispr\test-data` for retained manual fixtures; it does not delete or replace
`%LOCALAPPDATA%\WinWispr`, which contains settings, models, and logs.

## Privileged and user-visible integrations

- **Microphone:** recording starts from the user-selected microphone and uses
  in-memory samples for local recognition.
- **Keyboard handling:** a global input handler recognizes the configured
  application hotkey and recording controls. It is not an application
  keystroke-history feature, but endpoint owners should assess global-hotkey
  use under their keyboard-monitoring policy.
- **Clipboard and active application:** recognized text first reaches the
  Windows clipboard. The application may issue a paste command only if the
  target window remains the same; otherwise the text stays in the clipboard.
  The dictation stays in the clipboard after the paste, replacing the previous
  clipboard content, so it can be pasted again if the destination did not accept
  it. Windows does not acknowledge whether the destination accepted the paste.
  WinWispr excludes this clipboard entry from Windows clipboard history and roaming.
- **Selection translation:** the translate hotkey makes WinWispr inject a
  `Ctrl+C` into the foreground application, after the hotkey's modifier keys are
  released, to copy the selection to the Windows clipboard. WinWispr saves the
  previous clipboard (text, HTML, RTF, bitmap, files and web link; application-private
  formats cannot be saved), reads the copied text and restores the previous
  clipboard. Replace re-activates the window the selection came from and sends
  `Ctrl+V`, like a dictation. **Translate clipboard** in the tray reads the
  clipboard without changing it. None of this text leaves the machine, and failures
  are logged by kind and exception type only, never with text. Endpoint owners who
  restrict injected keystrokes or clipboard access should assess this feature
  together with the dictation paste.
- **Autostart:** the opt-in setting writes the current executable path to the
  per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value
  `WinWispr`. It does not require administrator rights.
- **Installer:** a per-user install defaults below `%LOCALAPPDATA%`; an
  all-users install requests elevation. The separate WinWispr Models installer
  adds `ModelData\`, `TranslationData\` and `VoiceData\` to the folder that
  WinWispr's uninstall entry names, or to a folder the user picks, and refuses a
  folder without `WinWispr.exe`; its uninstaller in `ModelsUninstall\` removes
  exactly those files. Interactive uninstall asks before
  deleting `%LOCALAPPDATA%\WinWispr`, with **No** as the default; silent
  uninstall does not prompt or delete the app data.

## Network boundary

### Runtime

The WinWispr Models installer contains the verified speech model at
`ModelData/model.tar.bz2` and the verified translation model below
`TranslationData/`; with it installed, installation, speech recognition and
translation have no application HTTP dependency, and WinWispr contacts no host
to translate. Setup and Portable contain no models. The source still contains a
compatibility path for non-bundled development builds, also offered in Settings
when no speech model is installed, for the speech model: its endpoint is fixed in
`src/WinWispr/Speech/ModelManifest.cs`, accepts HTTPS only, allows at most five
HTTPS redirects, checks the advertised archive length, and verifies the pinned
SHA-256 before model activation. That path is not used once WinWispr Models is
installed.

Apart from the update check below, the current source contains no remote
configuration, cloud transcription or translation, or runtime telemetry client.
This statement concerns the application source; Windows, a proxy, endpoint
security software, the model-hosting service and GitHub have their own network
and logging behavior.

### Update check

The application contains an update check (decision record:
`docs/adr/0003-in-app-update-check.md`). It is the only application network
access once WinWispr Models is installed.

- **When:** 30 seconds after start and every 24 hours while the per-user
  setting **Check for updates automatically** (`AutomaticUpdateChecks` in
  `settings.json`, on by default, **Settings → General**) is on, and on demand
  through **Check for updates** in the tray menu and on the General page.
- **What is sent:** one anonymous HTTPS request for the latest release of the
  public GitHub repository `cryptedx/WinWisper`. It carries only a
  `User-Agent` with the WinWispr version: no account, token, device
  identifier, usage data or telemetry. Only tags of the form `vX.Y.Z` are
  offered; model releases `models-vN` are ignored.
- **Network destinations:** `api.github.com` (release metadata) for the check;
  after the user clicks an offer, `github.com` and GitHub's release-asset
  download hosts under `githubusercontent.com` (currently
  `release-assets.githubusercontent.com` and `objects.githubusercontent.com`,
  which GitHub may change) for Setup and its `.sha256` sidecar. HTTPS only.
- **User consent:** a newer version is announced in a tray balloon; nothing is
  downloaded or installed until the user clicks it.
- **Verification before installation:** the SHA-256 of
  `WinWispr-Setup-vX.Y.Z.exe` must match both its `.sha256` sidecar and the
  digest GitHub reports for the asset, its Authenticode signature must be
  intact, and the signer's thumbprint must equal the pinned self-signed
  WinWispr certificate (`F8D419ED06D2038754C6BB2E5042BBF6DBE5903C`, from
  `eng/signing/code-signing.json`). The pin is checked whether or not the
  machine trusts that certificate. Any failure keeps the running version.
- **Installation:** WinWispr starts a hidden helper, Windows PowerShell
  (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile
  -NonInteractive -Command ...`), and quits. The helper waits for WinWispr to
  exit, runs the verified Setup silently into the same folder and scope
  (per-user installations with `/CURRENTUSER` without elevation, per-machine
  installations with `/ALLUSERS` after one UAC prompt) and then starts
  WinWispr again unelevated; if the UAC prompt is declined, the installed
  version starts again unchanged. Setup writes its log next to the download.
  Settings, the personal dictionary and models are kept. Portable copies are
  never changed; they get a link to the release page.
- **Controls:** users turn the automatic check off in Settings. Administrators
  disable both the automatic and the manual check with the machine policy
  value `DisableUpdateCheck` = `1` (DWORD) under
  `HKLM\Software\Policies\WinWispr`; alternatively, block the hosts above.
  Organizations that distribute WinWispr through their own software
  deployment should set the policy.
- **Limits:** the update path trusts the holder of WinWispr's self-signed
  private key; a self-signed signature is no publisher identity the
  organization approved, earns no SmartScreen reputation, and is no
  enterprise approval. There is no staged rollout or automatic rollback, and
  availability depends on GitHub.

### Bundled translation model

The Opus-MT translation model ships in the WinWispr Models installer as ten
loose files below `TranslationData/`, and the application reads them in place. There is no
download, no Settings action that fetches it, and no network destination to
allow.

- **Contents and provenance:** the Opus-MT models `opus-mt-de-en` and
  `opus-mt-en-de` as int8 ONNX exports (Hugging Face repositories
  `Xenova/opus-mt-de-en` and `Xenova/opus-mt-en-de`) and their SentencePiece and
  vocabulary files, five per direction, license CC-BY-4.0, with the attribution
  in `THIRD-PARTY-NOTICES.md`. The pinned commits of the exports and of the
  original models, and the path, size and SHA-256 of every file, are recorded in
  `eng/translation-model-pins.json` and in the SBOM.
- **Build controls:** the model package build takes the files only from the
  local, Git-ignored build input `artifacts/translation-model/`, copies each
  file only if it matches its pinned size and SHA-256, and checks every copy
  again. Setup and Portable must not contain it. `eng/model-package.json`
  versions the package and pins its content, so a changed pin needs a new
  package version. After installing Setup and the Models installer, the
  deployment checks all ten installed files against their pinned SHA-256.
- **Run-time controls:** the application uses the model only while all ten files
  are present at their pinned sizes (`src/WinWispr/Speech/ModelManifest.cs`, the
  `Translation` entry); otherwise translation reports that it is not installed.
  The files are protected like the rest of the installation folder.

### Build and release

Builds are not air-gapped by default. Locked restore can access pinned NuGet
sources, and `eng/Build-SherpaRuntime.ps1` clones the pinned sherpa-onnx
source revision from its upstream source. The release script then creates only
local artifacts and a local tag. Network egress must be permitted, proxied,
or mirrored according to the organization's build policy. The translation model is
a local build input like the speech model archive: the release build reads it from
`artifacts/translation-model/` and does not download it.

## Supply chain and artifact verification

- Application and test package versions and NuGet content hashes are locked in
  their respective `packages.lock.json` files.
- `eng/New-Sbom.ps1` generates `SBOM.cdx.json` from both lock files and adds
  model provenance (the speech model and the bundled translation model, with the
  source URL and SHA-256 of each of its ten files and the pinned upstream commits) plus
  native-runtime file hashes. It refuses a missing
  native-runtime hash file unless explicitly told that it is absent.
- `THIRD-PARTY-NOTICES.md` is the human-readable direct-component, model, and
  native-runtime license/provenance inventory. The locked package files and
  `SBOM.cdx.json` provide the complete application/test package inventory; a
  separate license reconciliation is still required. The locally built native
  runtime is marked `NOASSERTION`; that legal reconciliation blocks enterprise
  distribution.
- `SHA256SUMS.txt`, `BUILD-INFO.json` (including the source-archive SHA-256),
  build logs, and the local Git tag record an evidence pack's association with
  one source commit.
  Hashes alone do not prevent replacement; independently recompute them and
  use controlled storage and external signing before distribution.
- The Mac creates a clean `COPYFILE_DISABLE=1 git archive --format=zip HEAD`
  source archive only for provenance and SHA-256 evidence; it is not uploaded
  to the VM, which receives the source through the rsync mirror instead. The archive rejects `.DS_Store`, `._*`, `__MACOSX`, and
  `.AppleDouble`.
- Release output exposes the source-archive SHA-256 as provenance in
  `BUILD-INFO.json`, `SOURCE_ARCHIVE_SHA256`, and
  `RELEASE_REQUEST_SHA256`; the worker reads the repository mirror and writes
  evidence under its ignored `artifacts\.vm-control\` directory. The Mac copies
  the release evidence back over SSH, and the directory is removed after the run.
- Deployments and releases sign `WinWispr.exe`, `WinWispr.dll`, Setup, the
  WinWispr Models installer and both uninstallers with the self-signed
  certificate pinned in `eng/signing/code-signing.json`, with an RFC 3161
  timestamp, and verify each signature (valid, pinned signer, timestamped)
  after installation. `BUILD-INFO.json` records `signingThumbprint`, which the
  Mac checks against the pin. This proves that a file matches what the build VM
  signed; Windows shows a verified publisher only where the certificate is
  trusted, and it gives no SmartScreen reputation.
- The repository currently has no root product `LICENSE`. Product licensing
  must be decided and recorded before enterprise distribution.

The local release script does not currently perform a dependency-vulnerability
scan. Treat a current scan and documented decision for known findings as an
external release gate, not as an implied SBOM property; any known High- or
Critical-severity finding without an approved remediation/acceptance decision
blocks enterprise distribution.
The additional external gates are a protected Authenticode private key, RFC
3161 timestamp, independent vulnerability and malware scan, SBOM and license
review including NOASSERTION and native runtime status, product-license
decision, and endpoint EDR, WDAC, SmartScreen, and IT pilot approval.
`--self-signed is not a distribution approval`.

## Threat model, controls, and residual risks

| Area | Implemented control | Residual risk / required enterprise control |
| --- | --- | --- |
| Update tampering | Anonymous HTTPS check; download only after user consent; SHA-256 against sidecar and GitHub asset digest; Authenticode signature with pinned signer thumbprint before Setup runs; policy `DisableUpdateCheck` | Trust rests on the self-signed WinWispr key and on GitHub's availability; enterprise distribution should disable the check and deploy a build signed with a certificate the organization trusts. |
| Model tampering in transit | Bundled archive size/SHA-256 validation, staged activation; translation files checked against their pinned SHA-256 when packaged and after installation; compatibility downloads remain HTTPS-only with redirect limit, expected size, and pinned SHA-256 | Build-input custody, artifact replacement, availability, and model licensing still require supplier review and approved release storage. At run time the translation files are checked by size only, so their integrity rests on the protection of the installation folder and on the signature of the Models installer. |
| Dependency drift | Locked restore, exact native source commit, SBOM, hashes | A lock does not replace a current vulnerability assessment or approved package source. |
| Artifact tampering | Source-commit provenance archive/hash, SHA-256 manifests, self-signed Authenticode signatures verified against the pinned certificate | A self-signed signature identifies the publisher only where its certificate is trusted, and trusting it as a root lets its key holder sign other code; signing with a certificate the organization trusts, and its verification, are mandatory for enterprise distribution. |
| Local data exposure | No app audio/transcript archive; bounded technical logs; optional app-data deletion | Clipboard, destination application, Windows profile, backups, paging, and local administrator access are outside WinWispr control. |
| Input/output misuse | Explicit hotkey, user-selected microphone, target-window check before paste | Global hotkey and clipboard use require endpoint-policy approval; text can be sensitive once handed to Windows or another application. |
| Native runtime | Runtime hashes and package/source provenance recorded | Legal status is `NOASSERTION`; current signed-binary and endpoint acceptance evidence is required. |

## Verification status and required evidence

| Evidence class | Current repository status | Enterprise requirement |
| --- | --- | --- |
| Code-verified | Source and automated tests cover log bounds, model integrity paths, clipboard behavior, startup registry behavior, and package/archive contracts. | Run the current deterministic suite and retain its release log. |
| VM-verified | A current local release pack records `vmVerified=true` only when the matching source commit/provenance hash, portable ZIP, installer, and sidecars, SBOM, full third-party notices, changelog, build info, security report, command logs and interactive-session VM acceptance, and `SHA256SUMS.txt` pass. | Re-run on the exact current signed artifact in an interactive desktop session. |
| Signature-verified | Deployments and releases verify the self-signed WinWispr signature (`SIGNATURES=valid <thumbprint>`); there is no signature from a certificate the organization trusts. | Verify signer, chain, timestamp, and signature on installer, binaries, and uninstaller. |
| Not run | Current signed release acceptance, current vulnerability scan, and enterprise endpoint review are not produced by this repository. | Complete and retain those records before distribution. |

interactive-session VM acceptance must record exactly one app-data state. Existing
`%LOCALAPPDATA%\WinWispr` must be reported as `APP_DATA=preserved`; a fresh VM
where the directory is absent may report `APP_DATA=not-present`. Missing,
duplicate, conflicting, or other `APP_DATA=` values are not valid evidence.

The evidence pack must not contain secrets, models, audio, transcripts,
certificates, private keys, passwords, or tokens; models, audio, transcripts,
certificates, private keys, passwords, and tokens are not release-evidence
contents.

## Operational controls

1. Restrict build-input retrieval and build egress to approved endpoints or
   internal mirrors, while preserving the pinned-hash validation.
2. Store release evidence and signed artifacts in access-controlled internal
   storage; local Git tags alone are not a backup or authorization system.
3. Apply Windows account, full-disk encryption, clipboard, endpoint protection,
   backup, and least-privilege policies to the user device.
4. Keep application data when preserving a model/configuration is intended;
   use the uninstaller's explicit delete choice only when removal is intended.
5. Decide on the update check: set `DisableUpdateCheck` = `1` under
   `HKLM\Software\Policies\WinWispr` where WinWispr is distributed centrally,
   or allow the update hosts listed under [Update check](#update-check).
6. Treat every dependency update, model change, runtime rebuild, signing
   certificate change, and change to microphone/clipboard/hotkey behavior as
   a release-security review trigger.
