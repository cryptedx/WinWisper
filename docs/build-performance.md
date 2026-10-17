# VM build and packaging verification

Measured on 2026-09-12 using `ssh windev-pve`, the active interactive Windows
session, and the direct `Z:\` repository mapping. The VM has 4 vCPUs and 8 GB RAM.
The source baseline is `42ffbea6c20a923f1d2210cc41bff33392bcaf8b`; the workflow
changes were tested uncommitted. No release tag or publication was created.

> **Note:** This is a dated record. Since 2026-10-11, Setup and Portable no
> longer contain the speech model (or any other model); the models ship in the
> separate WinWispr Models installer. The sizes and model-copy timings below
> describe the packaging of 2026-09-12.

## Development checks

These are Windows worker durations, excluding SSH/task scheduling overhead.
“Cold” means new build outputs; installed SDKs, NuGet packages, and the native
runtime were already cached. Each row is one measured run, not a median.

| Run | Build | Worker total | Result |
| --- | ---: | ---: | --- |
| New development output | 92.449 s | 146.706 s | 165/165 deterministic tests |
| Unchanged warm output | 37.965 s | 85.568 s | 165/165 deterministic tests |
| Added source file | 40.032 s | 90.080 s | Newly added test compiled and passed |
| Native Settings UI | 40.429 s | 127.512 s | 1/1 native acceptance test |

The added source file was a temporary test probe and was removed after the
check. The final probe and UI runs both completed with `WORKER_EXIT_CODE=0`.
The runtime files and test assembly retained their hashes and modification
times during the unchanged warm run. The running Teams preview was not stopped.

Raw logs: `artifacts/dev-check/logs` and `artifacts/dev-check/validation`.

## Contract checks

- Bash syntax, deployment, installer, transport, and local-release contract
  checks passed. Transport cases include dirty checks without a model archive,
  invalid option combinations, apostrophes in test filters, concurrent owners,
  failed tests, and a failed worker that already printed a success marker.
- PowerShell parsing and the ArtifactHash, ReleaseVersion, ReleaseRequest, and
  ReleaseEvidence tests passed in the interactive Windows VM (7.456 seconds
  on the final run), including a payload larger than the 1 MB buffer.
- Evidence tests check both unchanged payload hashes and lack of recompression.
  Windows PowerShell 5.1 uses .NET Framework, where `NoCompression` can add
  uncompressed Deflate block framing; exact compressed/uncompressed size
  equality is therefore not required.
- The separate versioned build also passed all 165 deterministic tests with
  zero failures or skips (21.637 seconds test time, 42.923 seconds including
  runner startup). Its log is `artifacts/dev-check/validation/versioned-tests.ps1.out`.

## Packaging measurements

| Phase | v1.2.2 historical interval | Optimized measured duration |
| --- | ---: | ---: |
| Portable | 553.882 s | 82.369 s |
| Installer | 574.296 s | 138.091 s |
| Evidence | 1061.267 s | 170.774 s |

The portable timing includes publish without rebuilding, model copying, ZIP
creation, hashing, and cleanup. The installer timing includes validated ZIP
extraction, compilation, hashing, and staging cleanup. Evidence includes all
existing copy/hash/manifest/archive checks.

Historical durations are inferred from consecutive log timestamps, not
isolated profiler measurements. Portable/installer comparisons are not
same-source benchmarks: the current source also includes the later Teams/WPF
changes. Evidence was measured using exactly the old v1.2.2 ZIP and Setup bytes,
with their original acceptance record. The direct VM/share workflow is unchanged.
Each duration is one successful run, not a median. The three packaging phases
sum to 391.234 seconds versus the historical 2189.445 seconds; this is not a
new end-to-end release measurement.

| Artifact | v1.2.2 bytes | Current test bytes |
| --- | ---: | ---: |
| Portable | 578,897,580 | 606,388,041 |
| Setup | 549,786,106 | 568,139,164 |
| Rebuilt evidence envelope using old assets | Not retained | 1,128,873,544 |

The new application payload includes the Teams/WPF changes, so total size
differences cannot be attributed solely to compression. Exact hashes and the
independent manifest verification are in
`artifacts/dev-check/validation/verified-artifacts.json`.

Skipping recompression alone was insufficient: an unbuffered prototype wrote
only about 136 MB after several minutes while using approximately 18 seconds
of CPU time. That measurement was cancelled. ZIP output now uses a 1 MB
FileStream buffer, and installer extraction buffers archive input likewise.
The smoke validator also uses the existing buffered checksum helper.

The model contains 487,170,055 bytes and occupies 487,244,405 bytes in the new
ZIP, including uncompressed Deflate framing. Its SHA-256 remains
`5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf`.

## Packaging validation boundaries

The packaging smoke test uses version `0.0.0` and untracked build provenance.
It is an unpublished test of the working tree, not a release. Portable and
Setup sidecars, the unchanged published DLL, the bundled model, all evidence
manifest members, and the original evidence payload bytes were independently
verified. The numeric Setup version is exactly `0.0.0.0`; Inno formats the
textual version as a padded `0.0.0`. A deliberately
mismatched `0.0.1` request was rejected before creating a ZIP. A complete new
release, including installation and tag-last validation, requires the explicit
commit approval mandated by `AGENTS.md`; it has not been claimed as complete.

Cold/versioned builds emitted WinAppSDK PRI263 for
`MSTestAdapter.PlatformServices.resources.dll` in the test project. Warm builds
had zero warnings and errors, and the native Settings UI acceptance passed.
The warning was not suppressed.

Final Windows inspection confirmed that the original Teams preview process
(PID 5220) was still running and no `WinWispr-publish-*` temporary directories
remained. No installation, commit, tag, push, or publication was performed.
