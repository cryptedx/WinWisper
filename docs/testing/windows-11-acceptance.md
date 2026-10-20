# WinWispr Windows 11 Acceptance (Historical Run)

> **Status:** This evidence applies only to the unsigned portable run dated 2026-08-20
> identified below. It is not evidence for a current release and provides
> neither signature verification nor enterprise approval.

This file documents the private portable distribution. Passing unit tests alone
proves neither WinUI app startup nor the native speech runtime.

## Historical artifact under test

| Field | Value |
| --- | --- |
| Distribution | Self-contained portable ZIP |
| Package identity | None |
| Signing certificate | None |
| Architecture | x64 |
| Minimum build | Windows 11 Build 22000 |
| SDK | `10.0.400` |
| Source commit | `18f498ce1bbbc2f7eb55286f6582e269cfcde5df` |
| Source archive SHA-256 | `413cff7b7debc6d2e0c5e70f38d2dcecf51ea02b70982bad696b521e55893e04` |
| Historical source transfer | This run still uploaded to `%USERPROFILE%\WinWispr\incoming`; that path has been retired for current runs |
| Portable build | `eng\Build-Portable.ps1`, verified by the existing VM worker |
| Historical final artifact | `%USERPROFILE%\WinWispr\artifacts\portable\WinWispr-portable-current.zip` |
| ZIP SHA-256 | `7b6736b2ff0b253037b8ad40da27fb5134163908106b1fb70988c20e740c21bd` |
| Installer SHA-256 | `659d02a59820a66bbab266c76305b82626254d079fa7123a6836418a62905c3b` |
| Installation folder | `%LOCALAPPDATA%\Programs\WinWispr` |
| Model archive SHA-256 | `5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf` |
| Native runtime | sherpa-onnx, DirectML |

The `ModelManifest` contract expects exactly these files and sizes after extraction:

| File | Bytes |
| --- | ---: |
| `encoder.int8.onnx` | 652184281 |
| `decoder.int8.onnx` | 11845275 |
| `joiner.int8.onnx` | 6355277 |
| `tokens.txt` | 93939 |

## Historical acceptance matrix

Statuses are deliberately distinct: `PASS` means acceptance was executed and
supported by evidence; `CODE-VERIFIED` means code/test coverage is established
without interactive confirmation; `PENDING` means manual evidence is still
missing. Code tests are not presented as manual Windows evidence.

### Build and distribution evidence

| Check | Status | Evidence |
| --- | --- | --- |
| Portable ZIP contains exactly one `WinWispr.exe` | PASS | ZIP listing: `WinWispr.exe`, `WinWispr.pri` |
| Native runtime and third-party notices included | PASS | Runtime DLLs, `runtime.sha256`, `WinWispr.pri`, and `THIRD-PARTY-NOTICES.md` included |
| No models, audio, or transcripts in archive | PASS | Exclusion scan found no matches |
| Startup in interactive VM desktop | PASS | `WinWispr.exe`, PID 6432, Session 1; process path `%LOCALAPPDATA%\Programs\WinWispr\WinWispr.exe`, `Responding=true` |
| Second launch creates no second instance | CODE-VERIFIED | Single-instance contract tested; no new second interactive launch executed with build `18f498c` |
| Startup on/off without administrator privileges | CODE-VERIFIED | HKCU Run implementation and contract test; UI toggle not clicked interactively |
| Settings and models under `%LOCALAPPDATA%\WinWispr` | CODE-VERIFIED | `AppDataPaths` uses `LocalApplicationData`; no package identity API |
| Release build | PASS | 0 errors; known `PRI263` warning |
| Deterministic tests without acceptance gates | PASS | 129/129 |
| Settings UI acceptance test | PASS | Canonically built x64 helper through temporary `LogonType Interactive` task in Session 1; `ExitCode=0`, PASS marker present, no diagnostics; checks include `On`/`Off`, `Local speech model`, and download failure with recovery guidance; task removed afterwards |
| Window placement tests | PASS | 8/8 on 2026-08-23 |
| CPU model smoke test | PASS | 2026-10-04 on `windows-vm` (Windows 11 ARM64, x64 build): `Exact_parakeet_model_returns_non_empty_text` with the fixtures that `TrustedSpeechFixtures` verified from the pinned model archive |
| DirectML model smoke test | PENDING | No supported NVIDIA or Intel GPU on `windows-vm`; the GPU cases report inconclusive |
| `BUILD-INFO.json` matches source commit, archive hash, and freshly built runtime | PENDING | Must be refreshed by the next canonical `--rebuild-runtime` run |
| Authenticode for Setup, app, and installed uninstaller | PENDING | Not part of the historical run above. Since 2026-10-11, deployments and releases sign with WinWispr's own self-signed certificate (`eng/signing/`) and verify the signatures (`SIGNATURES=valid <thumbprint>`); a signature from a certificate the organization trusts is still missing, and `eng\Test-ReleaseSecurity.ps1` rejects unsigned artifacts |
| Defender/company EDR and IT approval | PENDING | Mandatory external gate before installation on the work laptop |

### Settings matrix

| Check | Status | Code/test evidence | Interactive evidence | Setup / fixture |
| --- | --- | --- | --- | --- |
| Open Settings from tray | PASS | Settings window and tray path exist in code | Tray menu and Settings opened on 2026-08-23 in Session 1 through VMware Fusion | Logged-in Windows Session 1, installed canonical build |
| Close Settings; process and tray remain active | PASS | Contract text and close/hide path exist in code | Settings hidden by closing window; PID 6432 remained `Responding=true` in Session 1; tray icon remained visible | Session 1 |
| Change mode, hotkey, and microphone; close and reopen; confirm immediate persistence | CODE-VERIFIED | `SaveSettings` path and Settings events; persistence/state tests exist | Not executed yet | Session 1; available microphone, valid hotkey |
| Capture a valid hotkey | CODE-VERIFIED | Hotkey formatting and capture contract tests | Not executed yet | Session 1; valid unused shortcut |
| Cancel hotkey capture with Escape | CODE-VERIFIED | Cancellation text and contract tested | Not executed yet | Session 1 |
| Cancel hotkey capture with inline Cancel | CODE-VERIFIED | Cancel control, automation name, and event path tested | Not executed yet | Session 1 |
| Hotkey capture times out after 15 seconds | CODE-VERIFIED | Timeout text and previous-hotkey contract tested | Not executed yet | Session 1; no input during capture window |
| Reject conflicting/invalid hotkey | CODE-VERIFIED | Unavailable text and safe retention of previous hotkey tested | Not executed yet | Session 1; shortcut already registered globally |
| Microphone: success with available device | CODE-VERIFIED | Selection/Ready state logic tested | Not executed yet | Session 1; at least one enumerable microphone |
| Microphone: no device | CODE-VERIFIED | `NoDevices` state, Windows default fallback, and status text tested | Not executed yet | Session 1; fixture/setup without enumerable microphone |
| Microphone: enumeration error | CODE-VERIFIED | `EnumerationFailed` state, fallback, and status text tested | Not executed yet | Session 1; reproducible device API failure |
| Microphone: saved device missing | CODE-VERIFIED | `SavedDeviceUnavailable` state and fallback tested | Not executed yet | Session 1; saved device ID deliberately absent |
| Model: download size and progress | CODE-VERIFIED | Manifest size and 0/50/100% UI states tested; isolated Settings UI helper exists | Not executed yet | Session 1; verified model manifest |
| Model: purpose and local processing | PASS | Settings UI helper checks `Local speech model`; markup contract checks technical model description | Model name, technical description, and factual local-processing note visible on 2026-08-23 in Session 1 | Session 1; installed canonical build |
| Model: dynamic size for screen readers | PASS | `TextBlockAutomationPeer` expects current visible text instead of a static name | Installed app reported `Download: 487 MB · Installed: about 671 MB` for `ModelSizeText` through UI Automation in Session 1 | Session 1; Settings open |
| Model: cancel download | CODE-VERIFIED | Cancel control and `Cancelled` state tested | Not executed yet | Session 1; preserve `%LOCALAPPDATA%\WinWispr` before/after |
| Model: retry after download failure | CODE-VERIFIED | `DownloadFailed` state and `Try again` action tested | Not executed yet | Session 1; reproducible download failure |
| Model: integrity failure | CODE-VERIFIED | `IntegrityFailed` state and retry action tested | Not executed yet | Session 1; deliberately damaged/hash-mismatched fixture |
| Model: installation failure | CODE-VERIFIED | `InstallationFailed` state and retry action tested | Not executed yet | Session 1; reproducible installation failure |
| Model: Ready | CODE-VERIFIED | Ready state mapping tested | Not executed yet | Session 1; verified model and runtime |
| Startup: English states | PASS | Settings UI helper checks `OnContent="On"` and `OffContent="Off"` | Visible `On` state confirmed on 2026-08-23 in Session 1 | Session 1; installed canonical build |
| DPI 100% | PENDING | DIP/placement logic tested; no visual 100% evidence | Not executed yet | Session 1; monitor at 100% |
| DPI 150% | CODE-VERIFIED | DIP conversion and 150% placement test exist | Not executed yet | Session 1; monitor at 150% |
| DPI 200% | PENDING | General DIP logic exists; no 200% test evidence | Not executed yet | Session 1; monitor at 200% |
| Restore saved placement between monitors with different scaling | CODE-VERIFIED | Target monitor DPI determined during restore; placement/DPI tests exist | Not executed yet | Session 1; two monitors, e.g. 100% and 150%; move window between them before closing |
| Large text scaling | PENDING | No interactive text scaling evidence | Not executed yet | Session 1; increase Windows text scaling |
| Settings window minimum resize width of 560 DIPs | CODE-VERIFIED | `SettingsMinimumWidthDip = 560` and constrain/DIP tests exist | Not executed yet | Session 1; resize window below minimum width |
| Keyboard-only operation | PENDING | Controls have keyboard/automation contracts | Partial evidence in Session 1: visible native focus in logical order `Recording mode` → `Microphone` → `Refresh`; complete mouse-free run still pending | Session 1; do not use mouse |
| Narrator | PENDING | Automation names and polite live regions tested in markup; Narrator not run | Not executed yet | Session 1; Windows Narrator enabled |
| Light theme | PENDING | No manual theme evidence | Not executed yet | Session 1; Windows app mode light |
| Dark theme | PENDING | No manual theme evidence | Not executed yet | Session 1; Windows app mode dark |
| High contrast | PENDING | No manual high-contrast evidence | Not executed yet | Session 1; Windows High Contrast enabled |

## Procedure for a new VM verification

The following commands describe only a possible verification phase. They
do not demonstrate a current run; new results, artifact hashes, and signature
verification must be recorded separately for each release.

All build and test commands run in the repository mirror
`C:\windev\WinWispr\src`, which the controller updates with rsync over SSH
before every run. SSH Session 0 registers a temporary interactive task whose
trigger starts the worker and acceptance in the logged-in session (Session 1
in the historical run; discover the session ID for new runs); the mirror's
`.git` must be present. The portable build runs in that logged-in interactive Windows
session. Only that session provides evidence for WinUI/app, microphone, hotkeys,
DPI, themes, keyboard, and Narrator; SSH Session 0 is not valid acceptance evidence.

```powershell
git rev-parse HEAD
dotnet restore WinWispr.sln --locked-mode
powershell.exe -ExecutionPolicy Bypass -File .\eng\Build-SherpaRuntime.ps1
dotnet test .\tests\WinWispr.Tests\WinWispr.Tests.csproj -c Release --no-restore --filter 'TestCategory!=WindowsAcceptance&TestCategory!=TaskBoardE2E'
powershell.exe -ExecutionPolicy Bypass -File .\eng\Build-Portable.ps1
```

Routine VM checks and installations skip the slower `TaskBoardE2E` category.
Release-mode verification includes it automatically; the release path still
excludes `WindowsAcceptance` unless its dedicated fixtures are supplied.

For native speech acceptance, the tests verify their own fixtures. They take
them from the pinned model archive
`artifacts\model\sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2`, which
`./eng/deploy-windows-vm.sh --check` copies to the VM. The trust chain is:

1. The archive must match the size and SHA-256
   (`5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf`) that
   `ModelManifest` pins. The app's own `ModelInstaller` checks both, extracts
   the archive, and checks the four model files against the sizes above.
2. The speech clip is the archive's own `test_wavs/de.wav` (German, 22.05 kHz,
   mono, 16-bit, 121388 bytes), published by the model's authors with the
   model. It is trusted because the archive is. Its SHA-256
   (`36d3c4845b9808a1656a2a2e92d884590e2db94389e6fe559643291ae0cd3710`) is
   pinned as well, so a different archive cannot swap it unnoticed.

`TrustedSpeechFixtures` in the test project implements this and caches the
verified result under `%TEMP%\WinWispr-speech-fixtures`. Without the archive
the speech tests report themselves inconclusive, which is not evidence.
`WINWISPR_MODEL_ARCHIVE` names another copy of the same archive;
`WINWISPR_MODEL_DIR` and `WINWISPR_SMOKE_WAV` override the model directory and
clip for a manual run, and such a run must record its own fixture hashes.

```bash
./eng/deploy-windows-vm.sh --check --test-filter 'FullyQualifiedName~ParakeetRuntimeAcceptanceTests|FullyQualifiedName~DictationEndToEndTests'
```

The GPU case is inconclusive without a supported NVIDIA or Intel adapter; that
is not GPU acceptance evidence.

The translation model is not installed at run time: the WinWispr Models
installer puts it in `TranslationData`. Setup and Portable contain no models.
A deployment installs Setup, checks that the folder holds no models, installs
the Models installer into the same folder, and then checks the speech archive
hash, all ten translation files against their pinned SHA-256
(`TRANSLATION_MODEL=installed`), the voice manifests (`MODELS=installed`), and
the signatures of both installers, `WinWispr.exe`, `WinWispr.dll` and both
uninstallers (`SIGNATURES=valid <thumbprint>`). The default runs of the suite cover the app's completeness check
with tiny files (`BundledTranslationModelTests`).

### Overlay rendering

The overlay is a transparent window: a capsule with a blurred desktop backdrop
(acrylic), a soft shadow in a transparent margin, and a tinted surface without
blur when Windows turns transparency effects off, energy saver is on, or the
session is remote. `OverlayGeometryTests` cover the placement per monitor DPI,
the capsule hit test and the fallback rule. In the `WindowsAcceptance`
category, `Native_overlay_startup_renders_content_without_taking_focus` also
checks that the shadow margin passes clicks to the window underneath while the
capsule keeps them, and `Native_overlay_gallery_captures_every_state` writes
BMP captures of every overlay state, including the fallback, over light, dark
and colourful backgrounds to `artifacts\dev-check\overlay-gallery` on the VM
for visual review:

```bash
./eng/deploy-windows-vm.sh --check --test-filter 'FullyQualifiedName~Native_overlay_gallery'
```

The captures use the VM's current display scale; repeat the run after changing
the scale to compare 100%, 150% and 200%. Mixed-DPI monitor setups need a real
multi-monitor desktop.

### Selection translation (deterministic tests and the interactive check)

The default suite covers selection translation without a clipboard, window or model:

- `SelectionTranslationTests` drives the coordinator through its effects record with fakes: every notice, Copy,
  Cancel, Replace (target, fallback, notices), swap direction (explicit direction on the original text, failure, double
  swap, busy state), Esc while the selection is read or translated, failure and timeout, the start guards (a dictation session in
  any state, a running translation, an open preview), the order read, prepare, translate, and the absence of the
  personal dictionary.
- `WindowActivatorTests` covers reactivating the original window with fake window functions: gone, already in front,
  minimized, never in front.
- `SelectionReaderTests` drives the selection reader against a fake clipboard with a change counter: the wait for the
  hotkey's modifier keys, a selection equal to the current clipboard text, nothing selected, several clipboard writes
  for one copy, and the restore of the previous clipboard on success, nothing selected, a copy without text, cancel and
  failure.
- `GlobalInputTests`, `AppSettingsStoreTests`, `SettingsWindowTests`, `DictationNoticePresentationTests`,
  `TranslationPreviewTests` and `TranslatorLoadStatusTests` cover the hotkey registration and conflicts, Esc during a
  translation, the default and fallback of the stored hotkey, the Settings markup, the notice mapping, the preview
  markup and the early model load.

The real clipboard, the Ctrl+C injection, focus handling and the preview window need the interactive desktop session of
the installed app (the release workflow), with the translation model installed:

1. Select German text in Notepad and press `Ctrl + Alt + E`: the preview shows "DE → EN" at the overlay position and
   takes focus. `Ctrl+C` or **Copy** puts the English text on the clipboard; `Esc`, **Cancel** or a click elsewhere
   closes it. The selection in Notepad is unchanged.
2. Put text A on the clipboard, select text B and translate: after the preview closes with Cancel the clipboard still
   holds A (also for an image or file list on the clipboard; application-private formats cannot be restored).
3. Select text equal to the clipboard text and translate: it still translates.
4. Press the hotkey with nothing selected: *No text selected*, clipboard unchanged. Select more than the limit: *Selection
   too long* with the limit.
5. Hold the hotkey's keys a moment after the press: the translation starts only after the release.
6. First translation after the start shows "Loading translation…", then "Translating…". `Esc` while it runs closes the
   overlay without a preview or notice, and Esc is not swallowed otherwise.
7. Start a dictation and press the translate hotkey: nothing happens. Press a dictation hotkey while the preview is
   open: the preview closes and the dictation starts.
8. Remove the translation model in Settings and press the hotkey: *Translation not installed*.
9. Select German text in Notepad, translate and press `Enter` (or **Replace**): the preview closes, Notepad is in front
   and the English text replaces the selection; `Ctrl+V` pastes it again and `Ctrl+Z` undoes the replacement. Hold the
   hotkey's keys a moment: the paste waits for the release. Close Notepad while the preview is open and press Replace:
   the English text is on the clipboard and *Copied to clipboard* shows.
10. Translate mixed text and press the swap button (⇄): the actions are disabled and "Translating…" shows, then the
    other direction's result appears with its label. Swap again: the first result returns. A selection that vanished
    before Replace is not detected: the text lands at the caret and `Ctrl+Z` removes it.
11. Copy German text in Notepad (`Ctrl+C`), then choose **Translate clipboard** in the tray menu: Notepad keeps the
    focus, the preview shows "DE → EN" with only Cancel and Copy (no Replace, no swap button) and `Enter` does nothing.
    Press Copy: the clipboard holds the English text. Clear the clipboard (or copy an image) and choose the entry again:
    *Clipboard has no text* and no preview. With the model removed: *Translation not installed*. While a preview is
    open or a dictation runs, choosing the entry does nothing.
12. Dictation takes priority. For each trigger (primary hotkey, task hotkey, English dictation hotkey, the task
    board's dictate button, tray Start/Stop) first start a selection translation and press the trigger while
    "Translating…" shows, then again while the preview is open. The translation stops or the preview closes, the
    dictation starts from that same press with its own overlay statuses (never "Translating…"), and the clipboard is
    unchanged. While an English dictation runs, the other triggers stay ignored.

### Translation end-to-end (the real application, real hotkeys)

`TranslationEndToEndTests` (`WindowsAcceptance` category, `Views/TranslationEndToEndTests.cs`) starts the real
`WinWispr.exe` in an isolated E2E profile, injects the real global hotkeys and keys, and observes the overlay and the
translation preview through their window titles, the preview's text through UI Automation, and a real text window
of the test process (the selection and paste target) and the real clipboard. It needs the interactive session, like
`DictationEndToEndTests`, and uses nothing from the installed app. The profile gets the translation model the way it gets
the speech model: the verified model folder (`TranslationModelFixtures`: every pinned file checked by size and
SHA-256, taken from `WINWISPR_TRANSLATION_MODEL_DIR` or else from the agreed
`%USERPROFILE%\WinWispr\test-data\translation\models`) is hard-linked into the profile's `TranslationData`
folder, where the E2E profile reads the model instead of next to the executable; the speech model comes from the
pinned archive as before. German
dictation is driven by the replay capture device with the archive's German clip ("Alles hat ein Ende, nur die Wurst
hat zwei."). Nothing is downloaded. The scenarios:

- Selection: the first translation after the start shows *Loading translation…* and *Translating…* and then the
  preview titled "DE → EN" with the expected English text (the facts of a qualification sentence, checked like the
  Translator acceptance tests); the selection is unchanged and the previous clipboard is restored while the preview is
  open. **Copy** (`Ctrl+C`) puts the translation on the clipboard, **Cancel** (`Esc`) changes nothing and keeps the
  previous clipboard, **Replace** (`Enter`) puts the translation in place of the selection and on the clipboard and
  `Ctrl+Z` in the target undoes it, and the swap button shows the "EN → DE" result and swapping back returns the
  first result.
- Notices: *No text selected* (clipboard untouched) and *Selection too long* (one character over the limit).
- Timing at the limit: a selection of 2,994 characters (the largest German text of whole qualification sentences
  within 3,000), one cold and five warm runs, from the end of the hotkey press to the preview, and from the first
  translation status to the preview.
- English dictation: the German clip is dictated with the English dictation hotkey in Hold and in Toggle, twice each
  (the second run is warm), and the pasted English text is asserted ("Everything has an end", "sausage"). The release of
  the hotkey to the pasted text and the translation phase (first *Translating…* status to the pasted text) are recorded.
- Not installed: with no translation model, the translate hotkey and the English dictation hotkey each report
  *Translation not installed*, the selection is not read (clipboard untouched) and the dictation press never shows
  *Recording* or *Processing*.
- Interplay: the translate hotkey is ignored while a dictation records (no status, no preview, no read), and a
  dictation press closes an open preview, starts recording from the same press, and the dictated text goes to the window
  the selection came from.

Run it with the deployment script (no environment variable is needed on windows-vm), and read the measurements from
the evidence file, because the runner's console output does not come back through the deployment worker:

```bash
./eng/deploy-windows-vm.sh --check --test-filter 'FullyQualifiedName~TranslationEndToEndTests'
ssh windows-vm 'Get-Content $env:TEMP\winwispr-translation-e2e-evidence.txt'
```

The desktop must stay undisturbed while it runs (injected keys reach only the foreground window). The harness hides
the task board so that the text window keeps the focus.

Keys held down from earlier input break every hotkey: an injected key down without its key up stays held for the whole
desktop session, and a held Shift turns each injected `Ctrl+Alt` chord into `Ctrl+Alt+Shift`, which no registered
hotkey matches. On 2026-10-07 a run that failed between holding and releasing the English dictation hotkey
(`Shift+Alt+E`) left Shift and E held on windows-vm. After that, every E2E run that pressed a hotkey failed with "did not
appear" or "board is hidden" timeouts, the task board E2E included. The harness now sends a key up for every key Windows
reports as held, before it starts the app and again after each scenario. When it finds held keys at the start, it lists
them in the runner output.

The scenarios use the default translate (`Ctrl+Alt+E`) and English dictation (`Shift+Alt+E`) hotkeys. A running
installed WinWispr holds the same chords (a deployment or release run starts it), and the E2E app then cannot register
them. Each scenario therefore first checks that both chords are free, and fails at once with the running WinWispr
processes otherwise. Close the installed app from the tray before the run and start it again afterwards.

**Result of the run on windows-vm, 2026-10-05** (Windows 11 ARM64 with x64 emulated, working tree on top of commit
043e3af, translation model from the agreed test-data folder, no download): 6 of 6 scenarios passed
(`TranslationEndToEndTests`, 6 run, 0 failed). Earlier runs the same day failed five times, all accounted for: a
placeholder fragment in the first dictation run, a timing assertion that was too strict (see the limit change below), and
three times the dictation fell back to *Automatic paste unavailable* with the transcript on the clipboard (once in
the Toggle scenario, twice in the interplay scenario), see "Intermittent paste fallback" below.

After the limit change to 4,000 characters, the full `TranslationEndToEndTests` run was repeated on 2026-10-05 and
passed (6 of 6), and the at-limit scenario alone was run in four more sessions (cold and five warm runs each). The
table gives the measurements from the 3,927-character runs; the first rows are from the earlier runs of the day at the
previous limit.

| Measurement | Value |
| --- | --- |
| First translation after the start, short sentence, hotkey to preview (includes the model load) | 2.5 to 3.4 s over four sessions |
| Selection of 3,927 characters (limit 4,000), cold (model load included), hotkey to preview | 3.67 to 3.79 s in four sessions (over 3 s: the model loads first) |
| Selection of 3,927 characters, warm, hotkey to preview | 1.79 to 2.13 s; 25 warm runs in five sessions, all within 3 s (the four isolated sessions: 20 runs, 1.79 to 2.08 s) |
| The same, from the first translation status to the preview | 1.69 to 2.02 s |
| English dictation, release of the hotkey to the pasted text (clip of one sentence, recognition and translation) | 0.44 to 0.86 s |
| English dictation, translation phase only | 0.09 to 0.20 s |

**Selection length limit: 4,000 lowered to 3,000 characters (2026-10-05, later the same day).** The latency gate
`A_selection_of_exactly_the_limit_translates_within_three_seconds_on_a_warm_model` failed at 4,000 characters with
3.06 to 3.62 s (English to German, unpunctuated), and the requirement is that no translation takes longer than three
seconds, so the maintainer lowered the limit to 3,000 characters. Measured at 3,000 on windows-vm (idle host, CPU load
3 to 8 percent before each run): the gate passed in three consecutive runs, and the slowest single translation was
2.42 s (English to German, unpunctuated, first of three runs; the other runs of that group took 1.40 to 2.04 s, the
sentence groups 1.27 to 1.59 s except one German to English run of 1.75 s). The end-to-end at-limit scenario with a
selection of 2,994 characters, three sessions (one inside the full `TranslationEndToEndTests` run, 6 of 6 passed, and
two alone): warm hotkey to preview 1.43 to 1.66 s in all 15 warm runs (translation phase 1.33 to 1.58 s), none over 3 s.
The cold first translation took 5.13 s (inside the full class run), 3.65 s and 3.84 s, so it is over three seconds
because the model loads first; only the first translation after the start pays it.

**Selection length limit: 5,000 lowered to 4,000 characters.** The first acceptance runs used a limit of 5,000
characters and a selection of 4,986 characters. Warm runs there stayed within three seconds in 12 of 16 runs but not
in all: 3.55 s, 3.18 s, 3.15 s and 3.02 s from the hotkey to the preview, against a typical 2.3 to 2.8 s (cold runs
4.7 to 8.9 s). The requirement is that no translation takes longer than three seconds, so the maintainer lowered the
limit to 4,000 characters (the code constant is `TranslationLimits.DefaultSelectionMaxCharacters`). The repeat at
3,927 characters is in the table above: 25 warm runs, none over 2.13 s. The cold first translation at the limit still
takes 3.67 to 3.79 s because the model load comes first; the load starts when the need is known (after the selection
is read), so only the first translation after the start pays it, and the three-second target is therefore a warm
figure. The values come from an emulated x64 build on Apple silicon, so they say nothing certain about other machines;
the end-to-end test fails only past the 5 s safety deadline and records the rest. The dictation case is far inside the
budget because a spoken sentence is short.

**Intermittent paste fallback.** Three times across the runs of the day (twice in the interplay scenario, once in the Toggle scenario) the paste did not happen and the overlay showed
*Automatic paste unavailable* with the transcript on the clipboard: `DeliveryResult` was `CopiedToClipboard` after 8 ms,
which means the foreground window was not the target at the moment of delivery (a later check found the target in front
again). Nothing is lost, and four consecutive repetitions of the interplay scenario passed afterwards, but the cause is not established. The interplay
scenario accepts the fallback only for its first dictation (no preview involved) and records which outcome occurred;
its second dictation, after the preview was closed, asserts the paste into the selection's window, as do the Hold and Toggle scenarios.
A fallback there fails the run and is reported, not hidden.

#### Manual checks in real applications: Not run

These need real applications and a person at the desktop of windows-vm, and the tray menu, which UI Automation cannot
drive in this harness. They were **not run** in this verification and no result is claimed. Run them in a normally
deployed development build (`./eng/deploy-windows-vm.sh`) with the translation model installed, and record the outcome
of every line below in this table (PASS or FAIL with a short note, application and version).

| Application | Check | Outcome |
| --- | --- | --- |
| Notepad (plain text editor) | Select German text, press `Ctrl + Alt + E`: the preview shows "DE → EN"; **Replace** puts the English text in place of the selection; `Ctrl+Z` undoes it; the clipboard held the English text after Replace | Not run |
| Notepad | Clipboard restore: copy text A, select text B, translate, press `Esc`: `Ctrl+V` pastes A | Not run |
| Word processor or mail client (Word, Outlook, Thunderbird) | Select German text in the body, translate, Replace: the text is replaced as plain text; `Ctrl+Z` undoes it; select a bold word and confirm the formatting is not kept | Not run |
| Browser text field (a `textarea` on a web page) | Select German text, translate, Replace: the field now holds the English text | Not run |
| Browser page text or PDF (not editable) | Select German text, translate, Replace: nothing changes visibly in the page, and `Ctrl+V` in an editable place pastes the translation (it stays on the clipboard) | Not run |
| Any application | Select more than 3,000 characters: *Selection too long* names the limit; select nothing: *No text selected* | Not run |
| Tray menu | **Translate clipboard** with German text on the clipboard: preview with only **Copy** and **Cancel**; Copy replaces the clipboard; with an empty or image clipboard *Clipboard has no text* | Not run |
| Tray menu | **Translate clipboard** while a preview is open or a dictation runs does nothing | Not run |
| Notepad | English dictation with `Shift + Alt + E` (Hold and Toggle, with the real microphone, speaking German): English text is pasted; `Esc` within 5 s cancels | Not run |
| Any application | Windows clipboard history (`Win + V`): the translation does not appear (the copied selection of the source application may) | Not run |

### Paste after Alt hotkeys (key release order)

Windows consumes a hotkey's key but passes its modifiers to the focused application. When Alt is released before the
key, the application sees a lone Alt press: Notepad++ enters its menu bar, Windows 11 Notepad moves the focus to its
title bar (also after `Ctrl+Alt` chords when Alt goes up before Ctrl), and the `Ctrl+V` that follows pastes nothing
while the delivery reports `PasteSent`. WinWispr's keyboard hook therefore taps the unassigned key `0xE8` while Alt or a
Windows key of one of its hotkeys is held, and the delivery refuses to paste into a window in menu mode.

Two `WindowsAcceptance` tests cover it. Both press the keys with scan codes, like a keyboard, Shift or Ctrl before Alt:

- `HotkeyReleasePasteTests` registers `Shift+Alt+Q` and `Ctrl+Alt+Q` through `GlobalInput` (WinWispr's hook and
  registration) over Windows 11 Notepad and Notepad++, releases the keys in every order, also after holding the key
  with auto-repeat (Hold mode) and as one `SendInput` burst (as macro tools and remote sessions deliver keys), and
  sends WinWispr's `Ctrl+V`. Every paste must arrive, bursts excepted, and each chord must reach WinWispr exactly
  once. A control run registers the same chords with `RegisterHotKey` alone and only records the outcome; the test is
  inconclusive if the control run loses no paste, because then the editors no longer react to a lone Alt press.
- `PasteDeliveryEndToEndTests.Real_application_pastes_into_notepad_after_a_shift_alt_toggle_released_alt_first` starts
  the real application with `Shift+Alt+Q` in Toggle mode, releases Alt first at both presses, dictates the German clip
  into Windows 11 Notepad four times, and asserts that every dictation was pasted.

```bash
./eng/deploy-windows-vm.sh --check --test-filter 'FullyQualifiedName~HotkeyReleasePasteTests|FullyQualifiedName~PasteDeliveryEndToEndTests'
ssh windows-vm 'Get-Content $env:TEMP\winwispr-hotkey-release-evidence.txt'
ssh windows-vm 'Get-Content $env:TEMP\winwispr-paste-e2e-evidence.txt'
```

Evidence on windows-vm, 2026-10-20, without bursts (20 pastes per editor): with the mask, every paste arrived in both
editors. In the control run, Windows 11 Notepad lost 12 of 20 (focus on the title bar's Settings button): every
`Shift+Alt` order that released Alt first, Hold included, and the `Ctrl+Alt` orders that released Alt before Ctrl.
Notepad++ lost 6 of 20 (menu mode `GUI_INMENUMODE`): the `Shift+Alt` orders that released Alt first, Hold included;
there the second Alt release of a Toggle session (`AKS,AKS`) closes the menu again. The masked Hold run first showed
two hotkey presses per chord: after the mask, Windows takes the key's auto-repeat for a new press despite
`MOD_NOREPEAT`, so the hook now swallows that repeat. The real application with the mask disabled pasted 0 of 4
dictations, each logged as `PasteSent`; with the mask it pasted 4 of 4 in each of four runs.

Bursts are mitigated, not solved. When the whole chord arrives at once, Alt's release can reach the hook before the
mask, which the hook thread then sends behind the burst; the hook therefore sends a mask itself at Alt's release until
one has passed it. With that, the `Shift+Alt` burst pasted 2 of 2 in Notepad++ and 1 of 2 in Windows 11 Notepad
(unmasked: 0 of 2 each); the `Ctrl+Alt` burst pasted even unmasked. A complete fix would hold back Alt's release and
send it again after the mask, which risks a stuck Alt key, so the test only records bursts.

### Translation acceptance (the Translator on the real model)

`TranslatorTests` is deterministic: a fake engine stands behind the Translator, so the default suite checks result
kinds, lazy loading, the pending-load answer, preload, retry and serialization without a model.
`TranslatorAcceptanceTests` runs the same module against the real Opus-MT int8 model on the shipped ONNX Runtime and
carries the `WindowsAcceptance` category, which the default suites skip:

- the tokenizer reproduces the Python reference ids and detokenized text (44 inputs per direction);
- 20 single sentences from the qualification fixtures keep their numbers, dates, times, names and negation in both
  directions;
- every warm translation of those sentences takes at most 3 seconds (the user's hard requirement; the test fails
  above it and prints every timing);
- a direction loads lazily, a translation during a preload waits for that load, and nothing is resolved at
  construction;
- a sentence over the token limit is cut into chunks of the model's size instead of being refused (a chunk the model
  cannot finish, such as a text of one sentence repeated 60 times, is a failed result, never a runaway or an exception);
- speech recognition still loads and recognizes after translation bound the managed runtime package to the
  shipped `onnxruntime.dll` (this one also needs the speech model archive, see above).

`TranslatorWholeTextTests` and `LanguageDetectionTests` are deterministic too and cover whole texts: the optional
direction, translating into English, language detection measured on the qualification fixtures (the 40 frozen sources,
the 75 distinct model outputs and the 160 challenge inputs in `Translation/Fixtures/language-detection.json`), the
selection length limit (exactly the limit passes, one more character is refused before any load; dictation has no
limit), sentence splitting and layout, chunking, deadlines, cancellation and serialization.
`TranslatorWholeTextAcceptanceTests` runs the same behavior on the real model (`WindowsAcceptance`): detection and
override, translating into English, line breaks, blank lines and lists, a whole text equal to its sentences translated
one by one, long text without punctuation, a refused selection that loads nothing, a text of exactly the limit within
three seconds (sentences and unpunctuated, both directions, three runs each), the deadline, cancellation and
concurrent requests.

`TranslationRuntimeAcceptanceTests` needs the shipped `Runtime\win-x64` folder but no model: it checks that the
managed package and the shipped `onnxruntime.dll` are both 1.24.4 and runs a tiny graph with the translation session
options.

**Where the model lives on windows-vm.** The tests read the folder named by `WINWISPR_TRANSLATION_MODEL_DIR`, which
must hold `opus-mt-de-en` and `opus-mt-en-de` in the layout of the package's `TranslationData` folder (the ten files of
`ModelManifest.Translation`). The agreed place is
`%USERPROFILE%\WinWispr\test-data\translation\models`, next to the other preserved test data. Before it runs, the
test project checks every file against the pinned size and SHA-256, so a result always describes the exact qualified
model. When the variable is not set, the tests use the agreed folder if it exists (the interactive session of the end-to-end run
does not see environment variables). Without either, the model tests are inconclusive and are not acceptance evidence; with a folder that is
not the pinned model they fail and name the files. The files are the same as the packaging input
`artifacts/translation-model/` (copy that folder, or the `TranslationData` folder of an installation); nothing in the
test run downloads. x64 binaries run emulated on the ARM VM, so measured times are pessimistic.

**Running them.** `./eng/run-vm-acceptance.sh <test-filter> [NAME=VALUE ...]` runs the test runner that the last
`./eng/deploy-windows-vm.sh --check` built on windows-vm, with the given environment variables, under the VM lock. It
builds nothing (it warns when the working tree is newer than the runner), prints the runner's output and one line per
test (outcome, duration) from the trx report, and exits non-zero on a failed test or runner failure. A test reported
`NotExecuted` is inconclusive and is not evidence. The deployment worker runs in a scheduled interactive task and does
not forward environment variables, which is why this script exists; it runs in the SSH session, so tests that need the
logged-in desktop stay with the deployment check.

```bash
./eng/deploy-windows-vm.sh --check --test-filter 'FullyQualifiedName~TranslatorTests'   # syncs, restores, builds, runs the deterministic tests
./eng/run-vm-acceptance.sh \
    'FullyQualifiedName~TranslatorAcceptanceTests|FullyQualifiedName~TranslatorWholeTextAcceptanceTests|FullyQualifiedName~TranslationRuntimeAcceptanceTests' \
    WINWISPR_TRANSLATION_MODEL_DIR='C:\Users\<vm-user>\WinWispr\test-data\translation\models'
```

Without the variable the same filter through `./eng/deploy-windows-vm.sh --check --test-filter` still runs
`TranslationRuntimeAcceptanceTests` and reports the model tests as inconclusive. The speech test of this group needs
the pinned speech archive in `artifacts\model` (the deployment script copies it).

**Latency gate.** Run this before any change of the selection length limit (`TranslationLimits.DefaultSelectionMaxCharacters`)
or of the engine tuning, and on the build that carries the change (the first command builds it):

```bash
./eng/deploy-windows-vm.sh --check --test-filter 'FullyQualifiedName~TranslatorWholeTextAcceptanceTests'
./eng/run-vm-acceptance.sh \
    'FullyQualifiedName~A_selection_of_exactly_the_limit_translates_within_three_seconds_on_a_warm_model|FullyQualifiedName~A_warm_translation_never_takes_longer_than_three_seconds' \
    WINWISPR_TRANSLATION_MODEL_DIR='C:\Users\<vm-user>\WinWispr\test-data\translation\models'
```

`A_selection_of_exactly_the_limit_translates_within_three_seconds_on_a_warm_model` translates a text of exactly the
limit, built from the fixture sentences (sentences and unpunctuated, both directions, three runs each), and fails above
3 seconds; `A_warm_translation_never_takes_longer_than_three_seconds` covers the single sentences. Both must pass
(`ACCEPTANCE=PASS`); a limit or tuning that fails them is not shipped. The VM must be quiet: Defender scans and indexing
on the host move these timings by a second, so repeat a failure on an idle host before drawing a conclusion.

**Throughput measurement and the selection length limit.** `Throughput_measurement_prints_the_time_per_text_length_for_every_setting`
prints, per direction, engine setting (`<segments per decoder pass>x<parallel segments>`, for example `1x6`) and text
length, the best, median and worst of three warm runs, whether the output equals the first setting's, a hash of the
output and the private memory of the process. It loads the Parakeet speech model first when the archive is available
(the table says "Parakeet resident"). Environment variables select the sweep: `WINWISPR_TRANSLATION_TUNINGS`
(default `1x1,1x6`), `WINWISPR_TRANSLATION_LENGTHS` (default `450,900,1350,1800`),
`WINWISPR_TRANSLATION_UNPUNCTUATED=1` (a text without full stops, which is cut into model-sized chunks) and
`WINWISPR_TRANSLATION_DIRECTION=de-en|en-de` (one direction in a fresh process, for clean memory numbers). The text is
built from the qualification fixture sentences. The selection length limit is the largest text that translates within
three seconds with some margin, so it must be re-derived whenever the engine, the model or the build host changes.

Measured on windows-vm on 2026-10-05 (6 cores, Apple silicon with x64 emulated, Parakeet resident and idle, warm
model, median of three runs, outputs identical across all settings, `1x6` is the shipped setting):

| Characters | German to English `1x1` | `1x6` | English to German `1x1` | `1x6` | Unpunctuated, `1x6` (de-en / en-de) |
| --- | --- | --- | --- | --- | --- |
| 1,800 | 2.1 s | 0.8 s | 2.1 s | 0.9 s | 0.7 / 0.8 s |
| 3,600 | 4.0 s | 1.5 s | 4.1 s | 1.7 s | 1.7 / 1.8 s |
| 3,000 | 3.4 s (interpolated) | 1.3 s (interpolated) | 3.5 s (interpolated) | 1.4 s (interpolated) | 1.4 / 1.5 s (interpolated, see the gate result below) |
| 4,000 | 4.4 s (interpolated) | 1.7 s (interpolated) | 4.5 s (interpolated) | 1.9 s (interpolated) | 1.8 / 1.9 s (interpolated) |
| 5,000 | 5.6 s (interpolated) | 2.0 s | 5.7 s (interpolated) | 2.5 s | 2.1 / 2.4 s |
| 5,400 | 6.1 s | 2.3 s | 6.1 s | 2.7 s | 2.1 / 2.5 s |
| 7,200 | 7.6 s | 3.3 s | 8.0 s | 3.4 s | 3.4 / 3.6 s |
| 9,000 | 9.6 s | 3.8 s | 9.9 s | 4.5 s | 3.6 / 4.8 s |

The limit is **3,000 characters** (lowered from 4,000 because the latency gate failed there with 3.06 to 3.62 s; the
3,000 row above is interpolated, the gate and end-to-end measurements at 3,000 are in the Translation acceptance results).
The earlier reasoning for 4,000 follows as history. The engine alone would allow more (the slowest direction takes 2.5 s at 5,000
characters and reaches 3 s at about 6,300), but the end-to-end path adds the preview and the hotkey handling, and
end-to-end runs at 5,000 characters took 3.02 to 3.55 s in 4 of 16 warm runs (see the Translation acceptance results);
at 4,000 characters the end-to-end warm runs took 1.79 to 2.13 s. The starting value of 450 characters came from the qualification's
35 tokens per second on windev-pve. Against the qualification report (section 4, 5,000 characters German to English,
greedy, two threads, one segment at a time: 23.5 s tuned, 26.8 s default), windows-vm takes 5.6 s with the same
sequential method after the QDQ switch below (4.2 to 4.8 times faster) and 2.0 s with the shipped parallel method (12 to 13
times faster). This host is therefore not slower than the qualification host, in spite of the emulation. A user
machine with fewer or slower cores gets the same limit and relies on the safety deadline (5 s) for the overrun.

Three findings of the measurement shape the engine. Parallel segments (one per core, up to six) give 2.2 to 2.7 times
the speed of one segment at a time and keep every output identical. Padded batches (`4x1`, `8x1`, `4x3`) are faster
still but change dynamically quantized outputs in most of the measured rows, so they are not used. The decoder dequantizes
its int8 output matrix to 119 MB of floats in every step of every run, which made each concurrent run cost 120 MB and
most of a step's time; the session option `session.disable_quant_qdq=1` folds it once at load and gives the same
outputs (equal hashes) with 1.6 times the speed and no memory per concurrent run. The added private memory of one
direction after use is 0.39 GB for sentences and 0.46 to 0.51 GB for unpunctuated text at 5,000 characters (limit
0.6 GB); loading both directions in one process keeps the arena of the first, so a second direction adds less.

For portable verification, extract the ZIP in the interactive session, start
`WinWispr.exe`, check the process path and single-instance behavior, and exit the
app cleanly. Before and after cancellation/retry cases, check:

- `%LOCALAPPDATA%\WinWispr` remains intact; deployment cleanup does not replace
  or delete models, settings, or logs.
- The ZIP listing contains no models, audio, transcripts, logs, or private data.
- The archive scan also rejects `.DS_Store`, `._*`, `__MACOSX`, and `.AppleDouble`.

VM acceptance runs as a controller step through a temporary interactive-session
task directly on the shared drive:

```bash
./eng/deploy-windows-vm.sh
```

The controller still creates a provenance archive with
`COPYFILE_DISABLE=1 git archive`. The interactive task compares the local commit
with `git rev-parse HEAD` in the mirror and runs the worker with
`-RepositoryPath C:/windev/WinWispr/src`. There is no source archive upload,
extraction into `source`, or artifact archive; the portable ZIP, installer, and
logs remain under the mirror's `artifacts` directory on the VM. Release control
files exist only temporarily under the mirror's `artifacts\.vm-control`, and
only the release evidence is copied back to the Mac.

Final artifact/privacy/metadata evidence comes exclusively from worker/controller
output:

`SOURCE_COMMIT`, `SOURCE_ARCHIVE_SHA256`, `ARTIFACT_SHA256`, `VERIFY=PASS`, and
`STATUS=PASS` must be present. Releases additionally provide
`RELEASE_REQUEST_SHA256` and evidence validated after its copy back to
`artifacts/.vm-control/release` on the Mac. No private models, audio, transcripts, logs,
or temporary source copies belong in the distribution archive.
