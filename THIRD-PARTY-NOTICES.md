# Third-party notices

WinWispr uses these third-party components at pinned versions or fixed release
artifacts:

This file is the human-readable direct-component, model, and native-runtime
notice set. The complete locked application/test package inventory is in the
two `packages.lock.json` files and the generated CycloneDX SBOM; this notice
file does not by itself prove that every transitive package has a reconciled
license.

- Microsoft.WindowsAppSDK 2.4.0 - Microsoft.
  License: MIT
  Source: https://github.com/microsoft/WindowsAppSDK
  License URL: https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE
- org.k2fsa.sherpa.onnx 1.13.5 - k2-fsa.
  License: Apache-2.0
  Source: https://github.com/k2-fsa/sherpa-onnx
  Package: https://www.nuget.org/packages/org.k2fsa.sherpa.onnx/1.13.5
  License URL: https://www.apache.org/licenses/LICENSE-2.0
- org.k2fsa.sherpa.onnx.runtime.win-x64 1.13.5 - k2-fsa NuGet runtime package.
  License: Apache-2.0
  Source: https://github.com/k2-fsa/sherpa-onnx
  Package: https://www.nuget.org/packages/org.k2fsa.sherpa.onnx.runtime.win-x64/1.13.5
  License URL: https://www.apache.org/licenses/LICENSE-2.0
  Note: the app build excludes these package assets and uses the locally built
  native runtime below.
- Parakeet TDT 0.6B V3 INT8 - NVIDIA model release.
  License: CC-BY-4.0
  Attribution/source: NVIDIA Parakeet TDT 0.6B V3,
  https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3
  License URL: https://creativecommons.org/licenses/by/4.0/
  Build source used by WinWispr:
  https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2
  The verified archive is bundled in the WinWispr Models installer as
  `ModelData/model.tar.bz2`; the source archive stays outside the Git source
  archive and is validated by size and SHA-256 during the build.
- Microsoft.ML.OnnxRuntime.Managed 1.24.4 - Microsoft.
  License: MIT
  Source: https://github.com/microsoft/onnxruntime
  Package: https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Managed/1.24.4
  License URL: https://github.com/microsoft/onnxruntime/blob/v1.24.4/LICENSE
  Note: translation uses this managed binding only. It carries no native
  binary and binds at run time to the `onnxruntime.dll` of the shipped native
  runtime (`Runtime\win-x64`, version 1.24.4), the library that speech
  recognition already uses. No new native binary is added.
- Microsoft.ML.Tokenizers 2.0.0 - Microsoft.
  License: MIT
  Source: https://github.com/dotnet/machinelearning
  Package: https://www.nuget.org/packages/Microsoft.ML.Tokenizers/2.0.0
  License URL: https://github.com/dotnet/machinelearning/blob/main/LICENSE
  Note: reads the translation model's own SentencePiece files.
- Google.Protobuf 3.30.2 - Google (transitive dependency of Microsoft.ML.Tokenizers).
  License: BSD-3-Clause
  Source: https://github.com/protocolbuffers/protobuf
  Package: https://www.nuget.org/packages/Google.Protobuf/3.30.2
  License URL: https://github.com/protocolbuffers/protobuf/blob/main/LICENSE
- System.Numerics.Tensors 9.0.0 - Microsoft (transitive dependency of
  Microsoft.ML.OnnxRuntime.Managed; it was already locked through
  Microsoft.WindowsAppSDK).
  License: MIT
  Source: https://github.com/dotnet/runtime
  Package: https://www.nuget.org/packages/System.Numerics.Tensors/9.0.0
  License URL: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Opus-MT opus-mt-de-en and opus-mt-en-de (int8 ONNX exports) -
  Language Technology Research Group at the University of Helsinki (Helsinki-NLP),
  OPUS-MT project; ONNX conversion and int8 quantization by Xenova.
  License: CC-BY-4.0 (the upstream project states this license for its released
  models; the Hugging Face page of opus-mt-de-en is tagged Apache-2.0, and
  WinWispr follows the upstream statement)
  Attribution/source: https://github.com/Helsinki-NLP/Opus-MT
  License URL: https://creativecommons.org/licenses/by/4.0/
  ONNX exports used by WinWispr, at pinned commits:
  https://huggingface.co/Xenova/opus-mt-de-en (commit
  `399dfd68706739fffd503f876093e455ae268a06`) and
  https://huggingface.co/Xenova/opus-mt-en-de (commit
  `1ca130c44c4c5441ef16d48aae521a424ab644f7`)
  Changes to the material: the weights were converted to ONNX and quantized to int8
  by those exports; WinWispr ships and uses the files unmodified.
  Distribution: included in the WinWispr Models installer as the ten files of
  both directions below `TranslationData/`, taken from the ONNX exports at the
  pinned commits above; Setup and Portable do not contain them. Each file is
  verified by its pinned size and SHA-256 when the package is built. The sizes
  and hashes are pinned in `src/WinWispr/Speech/ModelManifest.cs`, and the pinned
  upstream commits of the exports and of the original models are listed in
  `eng/translation-model-pins.json`.
  Disclaimer: the models are provided as is, without warranties of any kind, as
  stated in section 5 of the license.
- Native sherpa-onnx runtime DLLs - when present, built by
  `eng/Build-SherpaRuntime.ps1` from sherpa-onnx v1.13.5 commit
  `3dc7c569f31ca2cd4a20ed6f7db780327e6714c5`.
  License: NOASSERTION
  File hashes are recorded in `src/WinWispr/Runtime/win-x64/runtime.sha256`.
  Native runtime license reconciliation is a release blocker before enterprise
  distribution.

Exact NuGet package versions and content hashes are recorded in:

- `src/WinWispr/packages.lock.json`
- `tests/WinWispr.Tests/packages.lock.json`

`Microsoft.Windows.CsWin32` is not listed because this checkout no longer
references that package.

## Local TTS native components

The phase 1 TTS-enabled custom sherpa runtime statically links:

- eSpeak NG, source commit `ed530aa113046142eb5115cf2fc9157854d0ffe1`,
  GPL-3.0-or-later, source archive SHA-256
  `e4e262cbe34f7fe21f91f1ba3397f2728e1f30eafbae7853f2b753a9ed13f0dd`.
- Piper phonemize, source commit `f3ff95afc03640bc1399e113e83361192a2fafb4`,
  MIT, source archive SHA-256
  `d9cca4e2bdc7d6dd8dffb96a4668283dbd3f77a9c194a3e530c1e8eba9406a5d`.

Source and license texts: [eSpeak NG](https://github.com/csukuangfj/espeak-ng/tree/ed530aa113046142eb5115cf2fc9157854d0ffe1)
and [Piper phonemize](https://github.com/csukuangfj/piper-phonemize/tree/f3ff95afc03640bc1399e113e83361192a2fafb4).
Runtime provenance and SBOM record these additions. This prototype is not approved
for distribution until corresponding-source obligations and supplied voice licenses
are resolved. Each bundled voice includes its own hash-verified license file.

## Bundled TTS voices

The WinWispr Models installer includes `vits-piper-de_DE-thorsten-high` and
`vits-piper-en_US-ljspeech-high`. Source archive hashes and complete payload hashes
are pinned in `eng/voices/`. The Piper voices repository declares MIT in its
README metadata; bundled unmodified model cards identify the Thorsten dataset as
CC0 and LJ Speech dataset as public domain. Thorsten high was fine-tuned from
Lessac high; dataset declarations are not presented as independent weight licenses.
Each voice carries the upstream model card, repository metadata, provenance notice,
and eSpeak GPL license. Setup and Portable include neither voice.

`WinWispr.dll` embeds one short recording made with `vits-piper-de_DE-thorsten-high`:
the phrase "Also ich sage" (`src/WinWispr/Speech/GermanPrimer.wav`, 0.8 seconds,
16 kHz). Speech recognition places it in front of very short dictations to keep
them German; it is never played back.
