---
status: accepted
---

# Ship the models in a separate package and sign with a self-signed certificate

The speech model, the translation model and the offline voices leave Setup and Portable and ship in their own installer, **WinWispr Models** (`WinWispr-Models-vN.exe`), with its own GitHub release `models-vN`. All release binaries are signed with WinWispr's own self-signed code-signing certificate. This replaces the packaging part of [ADR 0001](0001-offline-machine-translation-model.md): the translation model is no longer part of Setup and Portable, and the model-free Update is retired.

## Separate model package

The models make up nearly all of a release: Setup and Portable were each about 1 GB, while the application itself is about 80 MB, and the models change far less often than the code. Every app release uploaded about 2 GB, often over a metered mobile hotspot, and a separate model-free Update existed only to keep user downloads small.

- **Own version.** `eng/model-package.json` gives the package an integer version and pins its content (the speech archive, `eng/translation-model-pins.json` and `eng/voices/*.json`) with a digest. A pin change without a new version fails `tests/model-package-contract.sh`, so a `models-vN` release is published only when a model changes, and an app release uploads about 80 MB per binary.
- **Installs into the app folder.** The Models installer finds the WinWispr folder through the app's uninstall entry, or asks for it (portable copies), refuses a folder without `WinWispr.exe`, and puts `ModelData`, `TranslationData` and `VoiceData` next to `WinWispr.exe`, where the app already reads them. The app's model handling did not change. Its own uninstaller removes exactly these files, and uninstalling WinWispr runs it first.
- **Setup is the update.** Setup contains no models and leaves installed ones in place, so the model-free Update is no longer needed.

## Self-signed code signing

The certificate (`CN=WinWispr`, RSA 3072, valid until 2036-10-11) has a non-exportable private key that exists only in the build VM's user certificate store; the repository holds the public certificate and its pin (`eng/signing/`). Deployments and releases sign and then verify every signature against the pin, with an RFC 3161 timestamp.

- **Why not SignPath or a purchased certificate.** The repository is private and has no product license yet, which free open-source signing services such as SignPath require; WinWispr is a personal-use tool; and a purchased OV or EV certificate means a yearly fee, identity validation and hardware-token handling for a single user.
- **What it gives.** On machines that trust the certificate (`eng/Trust-CodeSigningCertificate.ps1`), Windows shows *WinWispr* as a verified publisher and detects a changed file. The build verifies that what it installed is exactly what it signed.
- **What it does not give.** No SmartScreen reputation, no trust on machines that have not imported the certificate, and no enterprise approval; `enterpriseReady` stays false. Trusting a root certificate also lets its key holder sign other code that the machine then trusts.

## Consequences

- A new user installs two things: Setup (or Portable) and WinWispr Models. Without the Models installer, translation reports *Translation not installed*, and Settings offers only the existing speech-model download.
- Releases have two tracks: app releases `vX.Y.Z` with four assets, and model releases `models-vN`, built with `./eng/deploy-windows-vm.sh --model-package` and published with `./eng/publish-models.sh`.
- Deployments, releases and model package builds fail without the certificate on the build VM. Losing the private key means a new certificate, a new pin, and re-trusting it on every machine.
- If the project becomes public and licensed, or needs SmartScreen reputation, revisit SignPath or a purchased certificate; the signing step is isolated in `eng/CodeSigning.ps1`.
