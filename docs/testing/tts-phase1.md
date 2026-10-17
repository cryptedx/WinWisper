# Local TTS phase 1

## Scope and plan

Use the existing pinned sherpa-onnx 1.13.5 native build, preserving the DirectML
recognizer patches. Enable TTS and protect TTS construction/generation at the C ABI.
Use CPU synthesis and a single explicitly loaded voice. Keep TTS voices separate
from the Parakeet installation. No cloud, runtime download, server, Python, OCR,
selection capture, summary generation, or product UI is part of this prototype.

1. Validate native feature provenance and reuse the shared resolver in either startup order.
2. Validate bundled voice manifests, files, hashes, and licenses locally.
3. Bound text sections and playback buffering; stop discards stale synthesis, unload
   waits for native calls, and dictation pauses output and further scheduling.
4. Run deterministic Windows checks, then supplied-model synthesis and listening checks.
5. Measure cold/warm load, process memory/CPU, real-time factor at candidate speeds,
   and memory after unload. Never treat a test pass as naturalness acceptance.

## Offline delivery constraint

The user authorized build-time downloads on 2026-09-22. The pinned high-quality
Thorsten German and LJ Speech US English sherpa bundles are local release inputs
in `artifacts/voices/`. Archive URLs, sizes and published SHA-256 digests are in
`eng/voices/sources.json`; the complete file manifests are tracked alongside it.
The WinWispr Models installer build requires these exact manifests and verifies
every file before copying it into `VoiceData`. Setup and Portable omit VoiceData.
There is no runtime voice download. Large binaries remain outside Git like STT.

## Evidence

Implemented in isolated `.worktrees/tts-phase1` from commit
`cbf5a7962e95b1db7af6fe7fe192e1dfc33ede76`, without commit, deployment, or release.

- Windows host `windev-pve`, active desktop session discovered as 2. The worker
  restored the existing `Z:` share and built `Z:/.worktrees/tts-phase1` directly.
- Final default `--check`: **240/240 passed**, no skipped tests; C# build had zero
  warnings/errors. Log: `artifacts/dev-check/logs/20260922T152818719-1872`.
- Native TTS rebuild with boundary version 2 passed. The checked DLL retains
  `WinWisprGetLastRecognizerStatus`, adds `WinWisprGetLastTtsStatus`, exports TTS
  creation/generation, and no longer contains the disabled-TTS stub diagnostic.
  All four runtime DLL hashes match `runtime.sha256`. ORT DirectML 1.24.4 and
  DirectML 1.15.4 remain pinned. A later check used the validated runtime cache.
- Installer, deployment, deployment-runner, and static SBOM contracts passed.
  The deployment-runner contract initially lacked the ignored STT archive in this
  worktree; reusing the existing local archive resolved that fixture prerequisite.
- Windows packaging tests verify local voice hashes, both required languages,
  corruption rejection, PowerShell syntax, and native-source SBOM components.
  Their tiny synthetic files are not voice-quality or real-model evidence.

Downloaded-model acceptance on 2026-09-22:

- Official archive SHA-256 digests match both downloaded bundles. Expanded payload:
  German 131,881,256 bytes; English 132,233,410 bytes, excluding manifests.
- TTS-first then STT, both voices at speeds 0.8/1.0/1.25, and real release payload
  copy/hash verification: **2/2 passed**, no skips. Log:
  `artifacts/dev-check/logs/20260922T155810032-8920`.
- STT-first then TTS in a separate process: **1/1 passed**, no skips, zero build
  warnings/errors. Log: `artifacts/dev-check/logs/20260922T160515622-6592`.
- Final default regression check: **240/240 passed**, no skips, zero build warnings
  or errors. Log: `artifacts/dev-check/logs/20260922T160315547-10508`.
- The initial shared-path acceptance run was intentionally stopped, not counted as
  passing. Process counters showed 172 read operations/s at 705,526 bytes/s with
  0% CPU while validating files. The harness now stages voices into unique local
  Windows temporary directories, verifies them there, and cleans them up.
- High variants were chosen as initial listening-quality candidates, not as a
  resource-budget decision. No medium comparison was performed. Successful MSTest
  console output did not retain the phase metrics, so no numerical resource
  acceptance is claimed. Playback, listening, and final installer execution are
  still unverified. The packaging check verifies actual VoiceData copies through
  the same helper used by Portable/Setup; it does not claim a published release.

Native negative acceptance: **2/2 passed** using the existing filtered `--check`
flow, zero skipped tests and zero build warnings/errors. Final log:
`artifacts/dev-check/logs/20260922T153338231-5704`. An empty configuration returns
failure, and corrupt ONNX bytes are contained at the C ABI after file-level
validation. An earlier fixture was rejected too early for missing `phontab`; that
run was not counted as corrupt-ONNX evidence and the fixture was corrected.
This is not positive TTS synthesis, provider, token-parser, or playback acceptance.

## Remaining acceptance blocker

The German and English bundles have been downloaded and hash-verified. Their
phonemizer data is identical, so both support same-process voice switching.
Still required: listening and resource-budget acceptance, real playback pause/stop during dictation, long-text tests, provider/data
failure cases with a real model, cold/warm and unload metrics, and actual full
installer execution. No deployment, commit, push, merge, release,
or worktree removal has been performed.

## Developer prototype

Launch the development executable with `--tts-prototype` in the logged-in Windows
session. This explicitly opens a developer console; normal app launch has no new
UI or model load. Commands: `load <local directory>`, `speak de 1`, `speak en 1`,
`speak long 1`, `pause`, `resume`, `stop`, `unload`, `quit`. One voice is resident.
Start ordinary dictation to pause speech; ending dictation does not resume it.
There is no microphone/volume manipulation by the TTS player.

Each supplied voice directory requires `voice.json` with PascalCase fields:
`Id`, `Language` (`de-DE`, `en-US`, or `en-GB`), `License` (relative license file),
`Model`, `Tokens`, `DataDirectory`, and `Files` (array of `Path`, `Bytes`, `Sha256`).
Paths use `/` and must remain inside the voice directory. Every file except the
manifest must be listed and verified, including phonemizer data and license text.
Do not generate this manifest until the supplied model format and license are
reviewed. The prototype expects a sherpa-compatible VITS/Piper ONNX model, tokens,
and eSpeak data; a raw arbitrary ONNX file is insufficient.

Approved local inputs belong in ignored `artifacts/voices/<voice-id>/`. The
WinWispr Models installer build copies verified files to `VoiceData/<voice-id>/`;
missing German or English inputs fail that build. The Models installer installs
the directory next to `WinWispr.exe`; Setup and Portable omit it. No installed STT model or
AppData is replaced by the prototype. Manual loading can use installed VoiceData
or an explicitly supplied directory; no extraction or download fallback exists.

`TtsSessionTests` exercise cancellation/discard, unload ordering, dictation pause,
manual resume, single-voice replacement, and section limits without claiming voice
quality. `TtsVoiceTests` check integrity and path rejection. Native acceptance is
separate: optionally set `WINWISPR_TTS_VOICES` to a directory containing both supplied voices
and `WINWISPR_TTS_STT_MODEL` to the existing Parakeet directory, then use the
existing `--check --test-filter` flow for `TtsNativeAcceptanceTests`. By default the harness uses repository `artifacts/voices` and the existing active
STT installation. Missing inputs
are inconclusive, never successful synthesis evidence. Run fresh processes for
cold resolver ordering; a same-process sequence cannot establish cold startup.

Metrics from the prototype console are whole-process private bytes and CPU time,
sampled every 25 ms. They include concurrent STT/UI and can miss short peaks.
The acceptance harness additionally reports audio duration and synthesis real-time
factor at 0.8, 1.0, and 1.25 speed. Speech-with-playback wall time is not synthesis
time. Measure repeated load/unload and sustained passages before deciding budgets.

The pinned Piper frontend initializes eSpeak once per process. Voice switching
therefore requires identical verified phonemizer data. The prototype rejects a
change rather than silently retaining the first directory. ONNX voice unloading
does not promise zero retained process memory: eSpeak globals and runtime allocators
can remain resident. Measure this explicitly; switching phonemizer data requires
an application restart.
