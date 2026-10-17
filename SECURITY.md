# Security policy

## Scope and support boundary

This repository contains a local Windows dictation application and its local
release tooling. It has no remote release service of its own,
cloud transcription or translation service, or telemetry endpoint. The
WinWispr Models installer, released separately from Setup and Portable,
contains the verified speech and translation models; with it installed,
installation, speech recognition and translation require no application network access.

The application has one network feature on by default: the update check. It
anonymously reads the latest public GitHub release of WinWispr and, only after
the user clicks the offer, downloads Setup and installs it if the file's
SHA-256 and its signature by the pinned self-signed WinWispr certificate
verify. It sends no telemetry. Users turn it off in Settings; administrators
disable it with the machine policy `DisableUpdateCheck`. Its hosts, controls
and limits are described in `docs/ENTERPRISE-SECURITY.md` and
`docs/adr/0003-in-app-update-check.md`.

The explicit HTTP speech-model download remains only as a
compatibility path for non-bundled development builds and for installations
without WinWispr Models. It, its controls, and
the network destinations to allow or block are described in
`docs/ENTERPRISE-SECURITY.md`.

The repository does not contain a security-reporting email address, web form,
private key, or support SLA. It contains only the public half of WinWispr's
self-signed code-signing certificate (`eng/signing/`). Before an enterprise distribution,
the responsible organization must configure an approved private reporting
channel and name the maintainer who receives reports. Until then, this is not
an enterprise support commitment.

## Reporting a vulnerability

Use the organization-approved private channel for the project maintainer; do
not put exploit details, audio, transcripts, logs, model files, certificates,
or credentials in an issue, commit, release archive, or public chat.

Include the affected WinWispr version or local tag, artifact SHA-256, Windows
version, reproduction steps, impact, and only the minimum redacted evidence.
The maintainer must acknowledge, assess, remediate, test, and document the
outcome in the release evidence before distribution. This repository makes no
response-time promise.

## Enterprise release gate

`eng/Release.ps1 is not a supported release entry point`; it fails closed and
points to `./eng/release-local.sh` from the Mac Git workspace. The supported
local commands are:

```bash
./eng/install-git-hooks.sh
./eng/release-local.sh --dry-run
./eng/release-local.sh --self-signed
```

`hooks validate and remind; they never build, tag, push, publish, or start a
release automatically`. `the developer consciously starts the release`;
`the complete commit range is revalidated fail-closed`; `VM acceptance passes
before the local annotated tag is created last`. `SSH host-key trust stays at
the configured known_hosts entry for the VM host (windows-vm)`.

The release transcript must expose `SOURCE_ARCHIVE_SHA256` and
`RELEASE_REQUEST_SHA256`. The build runs from an rsync mirror of the
repository on the VM (`C:\windev\WinWispr\src`); temporary request and evidence
control files live only below the mirror's ignored `artifacts/.vm-control/`
directory and are removed after the run, once the release evidence has been
copied back to the Mac. The
current evidence pack is signed only with WinWispr's own self-signed
certificate (`CN=WinWispr`, pinned in `eng/signing/code-signing.json`; its
non-exportable private key exists only on the build VM) and deliberately sets
`enterpriseReady=false` and `vmVerified=true`. A self-signed signature shows a
verified publisher only on machines that trust the certificate, earns no
SmartScreen reputation, and is not a public certificate authority signature.
`--self-signed is not a distribution approval`.

Before enterprise distribution, a responsible release owner must:

1. Review the local tag, source commit/provenance hash, `SHA256SUMS.txt`,
   `BUILD-INFO.json`, `SBOM.cdx.json`, `THIRD-PARTY-NOTICES.md`, and current
   changelog.
2. Perform and record a current dependency-vulnerability assessment and an
   independent vulnerability and malware scan; the local release script does
   not currently implement those scans or policy gates. Any known High- or
   Critical-severity finding without an approved remediation/acceptance
   decision blocks enterprise distribution.
3. Complete SBOM and license review including NOASSERTION and native runtime
   status, and resolve the missing product-license decision. These are release
   blockers.
4. Sign the installer, the WinWispr Models installer, portable application
   binaries, and uninstallers with a certificate the organization trusts,
   using Authenticode SHA-256, a protected Authenticode private key, and an
   RFC 3161 timestamp; keep private signing material outside the repository.
5. Verify the signatures, signer identity, certificate chain, timestamp, and
   artifact hashes on the intended Windows endpoint, then add that evidence to
   the approved release record.
6. Run a fresh interactive Windows acceptance on the exact signed artifacts,
   including microphone, hotkey, model, native runtime, and installation
   behavior.
7. Complete endpoint EDR, WDAC, SmartScreen, and IT pilot approval for the
   intended deployment environment.

## Evidence status vocabulary

- **Code-verified** means source and automated tests cover a behavior. It is
  not proof that a new binary worked on a real endpoint.
- **VM-verified** means an identified interactive Windows run recorded the
  behavior. It applies only to that documented artifact.
- **Signature-verified** means the exact distributed file was checked after
  signing with a certificate the organization trusts. There is no such current
  evidence in this repository. Deployments and releases verify the self-signed
  WinWispr signature (valid, pinned signer, timestamped); that proves the files
  match what the build VM signed, not an approved publisher.
- **Not run** means the evidence was not produced for the release and must not
  be inferred from older results.

The historical unsigned VM result in `docs/testing/windows-11-acceptance.md`
is not signature evidence and does not approve a future version.

## Disclosure and records

Keep vulnerability reports and any sensitive evidence in the configured
private channel. Commit only sanitized, decision-relevant outcomes: affected
versions, remediation, verification method, and release status. Update
`CHANGELOG.md` for user- or IT-relevant changes and retain the local release
evidence pack with its local Git tag. Before enterprise distribution, copy the
pack to access-controlled, immutable organizational evidence storage.
