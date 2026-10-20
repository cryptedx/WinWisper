# Changelog

This file records user- and IT-relevant WinWispr changes. It is maintained
alongside the local tag-driven release process; it is not a substitute for the
release-specific `CHANGELOG-vX.Y.Z.md` in the evidence pack. The local pack is
not tamper-proof until copied to controlled storage and externally signed.

## Unreleased

### Added

- Update check: 30 seconds after start and every 24 hours WinWispr anonymously
  reads the latest GitHub release of `cryptedx/WinWisper` (tags `vX.Y.Z` only,
  never `models-vN`); **Check for updates** in the tray menu and on the
  Settings General page checks on demand. A newer version is offered in a tray
  balloon. On click, WinWispr downloads Setup to
  `%LOCALAPPDATA%\WinWispr\Updates\vX.Y.Z\`, verifies its SHA-256 against the
  sidecar and GitHub's asset digest and its signature against the pinned
  self-signed WinWispr certificate, quits and runs Setup silently into the same
  folder and scope (per-machine installations show one UAC prompt); WinWispr
  starts again with settings, dictionary and models kept. Portable copies get a
  link to the release page. The request carries only a `User-Agent` with the
  app version; no telemetry is sent. **Check for updates automatically**
  (Settings → General, on by default) turns the automatic check off, and the
  machine policy `DisableUpdateCheck` = `1` (DWORD) under
  `HKLM\Software\Policies\WinWispr` disables both checks. The network hosts
  are listed in `docs/ENTERPRISE-SECURITY.md`; the GitHub repository must be
  public for the check to work.

### Changed

- The models left Setup and Portable. The speech model, the translation model
  and the offline voices ship in the new **WinWispr Models** installer
  (`WinWispr-Models-vN.exe`), published as its own GitHub release `models-vN`
  only when a model changes. It installs them into an existing WinWispr folder
  (found through WinWispr's uninstall entry, or chosen by the user for a
  portable copy) and refuses a folder without `WinWispr.exe`; its own
  uninstaller removes exactly those files, and uninstalling WinWispr runs it
  first. Setup and Portable are now roughly 80 MB each instead of roughly 1 GB.
- The model-free Update (`WinWispr-Update-*.exe`, `WinWispr-update-*.zip`) is
  retired: Setup contains no models and is itself the update, leaving installed
  models in place. GitHub app releases now carry four assets: Setup, Portable
  and their SHA-256 sidecars.
- Releases and deployments sign `WinWispr.exe`, `WinWispr.dll`, Setup, the
  Models installer and both uninstallers with WinWispr's own self-signed
  certificate (`CN=WinWispr`) and an RFC 3161 timestamp, and verify every
  signature after installation. `./eng/release-local.sh --allow-unsigned` is
  now `./eng/release-local.sh --self-signed`. Windows shows the publisher as
  verified only on machines that trust the certificate
  (`eng\Trust-CodeSigningCertificate.ps1`); this is no public code-signing
  certificate and gives no SmartScreen reputation.
- Settings is no longer one long scrolling page. A navigation pane on the left
  groups the settings into General, Dictation, Task board, Speech model, and
  Translation pages, and a new Settings window opens at 840 x 680 DIP. A saved
  window size is kept.
- The translation model is no longer an optional download from Hugging Face in
  Settings: it ships in WinWispr Models, and WinWispr makes no network request
  for it. The Download, Cancel, and Remove translation actions are gone. A copy
  that an earlier version downloaded into `%LOCALAPPDATA%\WinWispr\TranslationModels`
  is no longer used and is deleted on the first start with the installed model.
  Without it, Settings and the overlay point to the WinWispr Models installer.
- The recording overlay is now drawn with smooth, antialiased capsule edges
  instead of a hard-clipped window shape, with a blurred, tinted desktop backdrop
  (acrylic) and a soft shadow. Without Windows transparency effects, with energy
  saver on, or in a remote session it uses a tinted surface without blur. Clicks
  on the transparent shadow margin reach the window underneath. The waveform
  rests as round dots, and the overlay is sized for the DPI of the monitor it
  appears on and follows DPI changes.
- VM release runs and filtered checks refuse to start their tests while hung,
  elevated, or other projects' windows are open on the VM desktop, because
  desktop E2E tests lose the foreground to them; routine checks report them.
  E2E timeouts name the foreground window and held modifier keys.
- The default Windows VM host is now `windows-vm`, the local VMware Fusion VM on
  the Mac. The Proxmox VM `windev-pve` and the `WINWISPR_VM_HOST` override
  are no longer supported.
- VM checks, deployments, and releases no longer read the
  repository through the `Z:` share. Every run first mirrors the checkout,
  including uncommitted changes and `.git`, with rsync over SSH into
  `C:\windev\WinWispr\src`; this also works from Git worktrees. Build output
  stays on the VM, and only release evidence and the WinWispr Models installer
  are copied back to the Mac, which
  now needs rsync 3 (`brew install rsync`).
- Bound Teams UI Automation during microphone preparation and cache microphone
  device resolution, so slow desktop discovery no longer blocks dictation
  startup.
- Filter native Teams windows before UI Automation during dictation preparation,
  avoiding queries against unrelated desktop applications. Meeting identity,
  mute confirmation, and owned-state restoration checks remain active.
- Record numeric preparation timings (hotkey dispatch, Teams discovery and mute
  confirmation, WinMM device start, and total capture preparation) in the bounded
  technical log without audio, transcripts, or window titles. Durations also
  cover failed attempts and are not success indicators.

### Added

- Translation between German and English, offline (overview of the entries below).
  The model (about 218 MB) is included in WinWispr Models, so nothing has to be
  downloaded; no text leaves the machine when translating.
  Selection translation (default `Ctrl + Alt + E`) and English dictation (default
  `Shift + Alt + E`) are designed to finish every translation within three seconds (a target measured
  on one test machine, not a guarantee);
  selections are limited to 3,000 characters, a very long dictation takes
  proportionally longer, and the output is plain text. Terminology and idioms can be
  translated wrongly, and English dictation pastes without a preview, so review it.
  Known behaviors: in non-editable text (a web page or a PDF) Replace has no visible
  effect and the translation stays on the clipboard, the copied selection can appear
  in the Windows clipboard history, and `Ctrl+Z` undoes a Replace. The Windows
  acceptance procedure lists the translation checks and the end-to-end scenarios.
- Selection translation: select text in any application and press the translate
  hotkey (default `Ctrl + Alt + E`, configurable in **Settings → Translation**).
  WinWispr copies the selection with Ctrl+C once you have released the hotkey,
  detects whether it is German or English, translates it into the other language
  offline and shows the result with its direction (for example "DE → EN") in a
  preview next to the overlay position. **Copy** (button or `Ctrl+C`) puts the
  translation on the clipboard, out of the Windows clipboard history, and
  **Cancel** (button, `Esc` or a click elsewhere) closes the preview; the
  selection itself is never changed. Your previous clipboard is restored after
  the selection is read. The overlay shows "Loading translation…" while the model
  loads and "Translating…" while it runs, and `Esc` cancels a running translation.
  Notices: *No text selected*, *Selection too long* (naming the limit), *Translation
  not installed* and *Translation failed*. The personal dictionary does not apply.
  The hotkey does nothing while a dictation is active, and a press of any dictation
  trigger (including the task board's button) cancels a running translation or closes
  an open preview and starts the dictation from that same press. Failures are logged by kind without any text.
- Translate clipboard in the tray menu: translates the text on the clipboard
  through the same translation as the hotkey (automatic direction, length limit,
  overlay statuses, `Esc`, start guards) without Ctrl+C or a focus change. The
  preview offers only Copy and Cancel; Replace, `Enter` and the swap button are
  not offered. An empty clipboard or one without text shows *Clipboard has no
  text*, and without the model *Translation not installed*. Reading never
  modifies the clipboard; Copy replaces it with the translation.
- Replace and swap direction in the translation preview: **Replace** (button or
  `Enter`) closes the preview, reactivates the window the selection came from,
  waits for the keys to be released and pastes the translation over the
  selection like a dictation; the translation stays on the clipboard and `Ctrl+Z`
  in the application undoes the paste. If the window is gone, Replace copies and
  shows *Copied to clipboard*. The swap button (⇄) translates the original text
  again in the opposite direction while the actions are disabled and the preview
  shows "Translating…"; a failed swap keeps the previous result and shows a
  notice, and a second swap returns to the first result.
- English dictation: the English dictation hotkey (default `Shift + Alt + E`,
  Hold or Toggle, configurable in **Settings → Translation**) records German
  speech, applies the personal dictionary, translates the transcript into
  English offline and pastes it without a preview. English transcripts are
  pasted unchanged. The translation model starts loading at the press so the
  load overlaps the recording, and the overlay shows "Loading translation…" or
  "Translating…". Without the installed model the press reports *Translation not
  installed* and does not record; when the translation fails or times out the
  German transcript is copied to the clipboard with *Translation failed –
  transcript copied*. Failures are logged by kind without any transcript text.
- The German–English translation model (both directions, about 218 MB) is part
  of WinWispr Models, in `TranslationData` next to `WinWispr.exe`, so
  translation works right after installing it and needs no download, also on
  networks that block one. The model package build copies the ten files only
  after checking each against its pinned size and SHA-256, and the deployment
  checks them again after installing the Models installer. Setup leaves an
  installed model alone; **Settings → Translation** shows whether the model is
  ready, and a missing model is added by running the WinWispr Models installer. The model is listed in the SBOM,
  the third-party notices (CC-BY-4.0 attribution), and the enterprise security
  document.
- A fail-closed GitHub release publisher that verifies Setup and Portable with
  their SHA-256 sidecars before publication, and `./eng/publish-models.sh` for
  the separate `models-vN` release.
- Local SemVer release orchestration from annotated Git tags and Conventional
  Commits, with versioned portable and installer artifacts.
- Local release evidence: source archive, SHA-256 manifest, build metadata,
  logs, SBOM, security report, and local annotated tag.
- Release acceptance now records exactly one app-data state: preserved for an
  existing `%LOCALAPPDATA%\WinWispr`, or not-present for a fresh VM.
- `./eng/install-git-hooks.sh`, `./eng/release-local.sh --dry-run`, and
  `./eng/release-local.sh --self-signed` are the documented release path;
  `eng/Release.ps1 is not a supported release entry point`.
- CycloneDX inventory from locked application and test dependencies, model
  provenance, native-runtime hashes, and expanded third-party notices.
- Enterprise transparency documentation for local data, network, signing,
  supply-chain, and acceptance boundaries.

### Fixed

- Dictations were still sometimes not pasted although the caret stayed in the
  text box, without any notice. The default dictation hotkey `Shift+Alt+X`
  contains Alt, and Windows swallows the press of `X` but passes Alt to the
  application. When Alt was released first, the application saw a lone Alt
  press: Notepad++ and other programs with a menu bar entered their menu, and
  Windows 11 Notepad moved the focus to its title bar (also after `Ctrl+Alt`
  hotkeys when Alt went up before Ctrl), so `Ctrl+V` went there. Whether it
  happened depended on the order in which the keys were released. WinWispr now
  taps an unassigned key (`0xE8`) while Alt or a Windows key of one of its
  hotkeys is still held, so the application sees no lone Alt or Windows press;
  `0xE8` can no longer be chosen as a hotkey key. If a window is in menu mode
  anyway, WinWispr no longer sends `Ctrl+V` into the menu: it shows *Automatic
  paste unavailable – Press Esc, then Ctrl+V*, the dictation is on the
  clipboard, and the technical log records `TargetMenuOpen`.
- Dictations were often not pasted although the target window kept the focus,
  while a manual `Ctrl+V` afterwards worked. Clipboard monitors such as
  security agents and clipboard histories open the clipboard as soon as it
  changes, and an application whose paste finds it open pastes nothing.
  WinWispr now waits until the clipboard has been free for two checks in a row
  (about 50 ms) before it sends `Ctrl+V`, and retries setting the clipboard for
  up to a second while another program holds it. If the clipboard stays open
  for a second, the dictation stays on the clipboard with *Automatic paste
  unavailable*.
- Editing a task description with more than one line lost every line after the
  first: the editor opened with only the first line, and typing or closing it
  saved that shortened text. The editor now shows and keeps every line. The
  same applied to the description in the *New task* window.
- Dictating only one or two German words often produced English or Dutch
  (*Hallo* as *Hello*, *Nein* as *Nine*): the speech model has no language
  setting and guesses the language from the audio alone. A short result that is
  not already German is now recognized a second time with a German phrase
  spoken in front of it, which keeps the words German; that phrase is removed
  from the result. This adds about 0.1 seconds to such short dictations, and a
  short English dictation such as *Hello* can now come out as *Hallo*.

### Security

- Self-signed output is explicitly marked `enterpriseReady=false`; signing with
  a certificate the organization trusts, and its verification, are required
  before enterprise distribution.
- Model download integrity checks, log retention limits, and archive exclusions
  are documented as controls, with residual risks and external release gates.
- `hooks validate and remind; they never build, tag, push, publish, or start a
  release automatically`; `the developer consciously starts the release`;
  `the complete commit range is revalidated fail-closed`; `VM acceptance passes
  before the local annotated tag is created last`. `SSH host-key trust stays at
  the configured known_hosts entry for the VM host (windows-vm)`.
- Release logs expose `SOURCE_ARCHIVE_SHA256` and `RELEASE_REQUEST_SHA256`;
  the VM builds from an rsync mirror of the repository and uses only the
  temporary ignored `artifacts/.vm-control/` for release control, from which
  only the release evidence (and, for `--model-package`, the WinWispr Models
  installer) is copied back; `--self-signed is not a distribution approval`.
- Open Enterprise gates remain: protected Authenticode private key, RFC 3161
  timestamp, independent vulnerability and malware scan, SBOM and license
  review including NOASSERTION and native runtime status, product-license
  decision, endpoint EDR, WDAC, SmartScreen, and IT pilot approval.

## Changelog discipline

- Use Conventional Commits: `fix:` for PATCH, `feat:` for MINOR, and `!` or a
  valid `BREAKING CHANGE` footer for MAJOR releases.
- Add an Unreleased entry for a user-, IT-, legal-, security-, or deployment-
  relevant change in the same source change. Do not log secrets, audio,
  transcripts, private paths, certificates, or credentials.
- Before creating a release, move the reviewed Unreleased entries to a
  `MAJOR.MINOR.PATCH` section that matches the local release tag. Keep the
  release-specific evidence changelog generated by `./eng/release-local.sh`
  with the evidence pack; tag last after local evidence and VM acceptance.
- Do not create a changelog version from a branch name, remote release, or
  deployment date. The annotated local Git tag remains the release identity.
