# Deployment

Read this before changing the `eng/` deployment scripts or the release flow. The commands people run are in `AGENTS.md`; this file holds the invariants behind them and the host facts that only deploy work needs.

## Scripts and contracts

Read the affected scripts and their contract tests before editing:

- [deploy-windows-vm.sh](../../eng/deploy-windows-vm.sh) orchestrates; [Deploy-WindowsVm.ps1](../../eng/Deploy-WindowsVm.ps1) is the interactive worker; [vm-lock.sh](../../eng/vm-lock.sh) is the VM lock it shares with [run-vm-acceptance.sh](../../eng/run-vm-acceptance.sh).
- The WinWispr Models installer: [Build-ModelPackage.ps1](../../eng/Build-ModelPackage.ps1), [ModelPackage.ps1](../../eng/ModelPackage.ps1), [WinWisprModels.iss](../../installer/WinWisprModels.iss), [model-package.json](../../eng/model-package.json), [publish-models.sh](../../eng/publish-models.sh). Signing: [CodeSigning.ps1](../../eng/CodeSigning.ps1) and the pin [code-signing.json](../../eng/signing/code-signing.json).
- Contracts: [deployment-contract.sh](../../tests/deployment-contract.sh), [model-package-contract.sh](../../tests/model-package-contract.sh), [deploy-windows-vm-contract.sh](../../tests/deploy-windows-vm-contract.sh), [run-vm-acceptance-contract.sh](../../tests/run-vm-acceptance-contract.sh), [setup-worktree-contract.sh](../../tests/setup-worktree-contract.sh).

## Host facts

- Verify the interactive Windows session on the chosen host before a build. Discover the current session ID; a session ID from a past run is stale.
- `eng/deploy-windows-vm.sh` always uses `windows-vm`.
- Other projects' agents (for example `C:\windev\SpecAssist`) share the interactive desktop without the WinWispr VM lock. Before the tests, the worker's `Assert-UndisturbedDesktop` reports hung, elevated, and other projects' windows; it refuses release runs and filtered checks while they are open, because desktop E2E tests then lose the foreground.
- The code-signing certificate (`CN=WinWispr`, thumbprint in [code-signing.json](../../eng/signing/code-signing.json)) and its non-exportable private key exist only in `Cert:\CurrentUser\My` of the build user on `windows-vm`; [New-CodeSigningCertificate.ps1](../../eng/New-CodeSigningCertificate.ps1) created it once. The VM also trusts it through [Trust-CodeSigningCertificate.ps1](../../eng/Trust-CodeSigningCertificate.ps1), run elevated, because every deployment verifies the signatures. Deployments, releases and `--model-package` fail without it; `--check` does not sign. A new certificate changes the pin and the trust on every machine; ask the user first.
- Test data lives in `%USERPROFILE%\WinWispr\test-data`; the translation model for acceptance tests is `%USERPROFILE%\WinWispr\test-data\translation\models`. These paths and host names are stated here and in `AGENTS.md` only; tickets and prompts link to them.

## Worktrees

A fresh worktree lacks the Git-ignored bundled speech model, offline voices and translation model, which live in the main checkout's `artifacts/`. Run `./eng/setup-worktree.sh` once per worktree: it links `artifacts/model`, `artifacts/voices` and `artifacts/translation-model` to the main checkout (idempotent; the main checkout is refused) and adds them to the shared `info/exclude`, because the `.gitignore` entries match directories and not symlinks.

## Invariants

- Build from the rsync mirror `C:\windev\WinWispr\src` of the calling checkout (a worktree works too), including uncommitted changes and `.git`. The mirror holds exactly the files Git does not ignore. Build output stays in the mirror's `artifacts\`, which the sync never sends from the Mac and never deletes on the VM; only the speech model archive, the offline voices and the translation model are copied there, for the WinWispr Models build. The sync also keeps the `bin/` and `obj/` directories that builds create in the source tree, so builds stay incremental. The mirror keeps the Mac's time stamps and is shared by every checkout, so time stamps alone cannot tell MSBuild that another checkout's tree was synced: the runner names the synced tree (file list, sizes, time stamps), and [SourceTreeBuild.ps1](../../eng/SourceTreeBuild.ps1) keeps a build incremental only when its output was built from that same tree, and otherwise builds with `--no-incremental` (`BUILD_MODE=` in the output).
- Disk space stays bounded: every worker run first prunes old output with [Prune-VmArtifacts.ps1](../../eng/Prune-VmArtifacts.ps1) and stops before the build when `C:` or the Mac is short; `release-local.sh` prunes the Mac with [prune-artifacts.sh](../../eng/prune-artifacts.sh). Preview either with `--dry-run` (`./eng/deploy-windows-vm.sh --prune --dry-run` for the VM). Put diagnostic scratch under `artifacts/dev-check/`, where it ages out.
- SSH Session 0 only mirrors the checkout, schedules the interactive worker, and polls it. UI, microphone, and hotkey checks run in the logged-in desktop session.
- Keep the local `COPYFILE_DISABLE=1 git archive` for provenance and SHA-256 evidence, and reject macOS metadata. Source archives are never uploaded or extracted and artifacts never travel as archives; the only return transfers are the release evidence below `artifacts/.vm-control/release` and, for `--model-package`, the WinWispr Models installer and its sidecar into `artifacts/models/`, hash-checked against the worker's `MODEL_PACKAGE_SHA256`.
- VM runs from every checkout are serialized by `artifacts/.vm-lock` in the main checkout. rsync 3 is required on both sides: Homebrew's `rsync` on the Mac and the portable MSYS2 rsync at `C:\windev\tools\msys64\usr\bin\rsync.exe` on the VM.
- Remove the mirror's `artifacts\.vm-control` files after the run. Preserve `%USERPROFILE%\WinWispr\test-data`, the active installation at `%LOCALAPPDATA%\Programs\WinWispr`, and app data/models/logs at `%LOCALAPPDATA%\WinWispr`; deployment cleanup leaves app data as it is.
- The worker builds the WinWispr Models installer once per model package version and caches it in the mirror's `artifacts\models\`; it reuses the cache only while its sidecar hash and signature verify (`MODEL_PACKAGE_BUILD=cache|rebuilt`). Pruning keeps only the newest model package, on the VM and on the Mac. A model change needs a new `version` and `contentSha256` in `eng/model-package.json`; the contract test fails otherwise.
- `--cleanup-legacy` removes only the old `source`, `incoming`, and `artifacts` directories under `%USERPROFILE%\WinWispr`.

## Data volume

On a mobile hotspot the steps cost very different amounts. `git push` of a release commit and tag sends a few MB. `publish-release.sh` uploads Setup and Portable without models, roughly 80 MB each plus their sidecars (estimate: the v1.9.0 model-free Update, the closest earlier equivalent, was 77 MB `.exe` and 114 MB `.zip`); before the models moved out it was about 2 GB. `publish-models.sh` uploads the WinWispr Models installer, about 870 MB (models-v1: 866 MB), but only when the model package version changes. Both are the metered steps and ask before uploading on a metered network. When the user limits data ("kein push", "save data volume"), finish the local release, then offer to push now and publish later, stating both sizes.

## Installing

- Install Setup first, check that the installation folder holds no `ModelData`, `TranslationData` or `VoiceData`, then install the WinWispr Models installer silently into the same folder. Verify the installed speech archive hash, all ten translation files and the voice manifests (`MODELS=installed`), and the signatures of Setup, the Models installer, the installed `WinWispr.exe`, `WinWispr.dll`, `unins000.exe` and `ModelsUninstall\unins000.exe` (`SIGNATURES=valid <thumbprint>`) before launching the app.
- Copy each verified installer to a unique local Windows temp directory, verify its SHA-256, run `Unblock-File` on that copy, and install silently in the interactive session. Launching an installer from the build output or a network path triggers Windows' Open File security warning, which blocks unattended deployment. Check the installer exit code and app launch, then remove the temporary copy. Windows security stays enabled and the user is never asked to click through the warning.
- If AppX is deliberately reintroduced, installation and verification run in the interactive session; check the expected version and `Status Ok` with `Get-AppxPackage -AllUsers` and remove the temporary task.
