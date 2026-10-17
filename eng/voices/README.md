# Bundled TTS release inputs

The two high-quality Piper voices were downloaded on 2026-09-22 from the official
sherpa-onnx `tts-models` release. `sources.json` pins the published archive sizes
and SHA-256 digests. Each sibling voice JSON pins every expanded payload file.
The small upstream model cards, repository license metadata and provenance notices
are retained here; large archives and models are ignored release inputs like STT.

Prepared inputs live in `artifacts/voices/<id>/`. The WinWispr Models installer
build (`eng/Build-ModelPackage.ps1`) copies them to `VoiceData/<id>/`, validating
the pinned manifests and every file hash. Setup and Portable omit VoiceData.
Missing or changed voices fail the model package build. A changed manifest also
needs a new model package version in `eng/model-package.json`.
When moving this work into another checkout, transfer the ignored voice input
folders as well as the source changes, just as for `artifacts/model/`.

To restore an input, download its URL from `sources.json`, verify size and SHA-256,
and safely extract the archive into `artifacts/voices`. Copy this directory's
voice-specific MODEL_CARD, PIPER-README.md and LICENSE-NOTICE.txt into the extracted
voice directory; copy `docs/licenses/espeak-ng.txt` as `ESPEAK-LICENSE.txt`, and
copy the matching pinned JSON as `voice.json`. Do not regenerate manifests to
silently accept changed upstream files. This preparation happens on the build
machine; the shipped application never downloads a voice.
