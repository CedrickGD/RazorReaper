# Trusted Updater Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with strict RED/GREEN/refactor checkpoints. Work only in the exact existing detached worktree `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`. Do not create a branch, commit, merge, push, publish a release, deploy, call a remote API, or perform any remote action. Review through local tests and diffs only.

**Goal:** Ensure RazorReaper downloads, stages and executes an installer only after a signed immutable manifest, anti-rollback policy, content hash, size and pinned Authenticode publisher all validate.

**Architecture:** `UpdateService` orchestrates narrow transport, JWS verification and policy collaborators. `AutoUpdateManager` receives only a trusted artifact descriptor, streams to a unique partial file, verifies it, atomically stages it and requests handoff. `InstallerLauncher` re-verifies immediately before using a fixed local argument list; network-controlled installer arguments and unsigned fallback are removed.

**Tech Stack:** .NET 10, `ECDsa` ES256/JWS, SHA-256 streaming, Windows Authenticode/WinVerifyTrust adapter, xUnit, fake HTTP and filesystem/process seams.

**Spec:** Approved trusted-updater architecture; depends on the test harness from `2026-08-16-app-launch-privacy-plan.md`.

## Global Constraints

- Exact detached worktree only; no branch, commit, merge, push, release or remote action.
- Use `apply_patch` for edits and `--no-restore` for builds/tests.
- Do not fetch a manifest or installer while implementing tests; use local fixtures and in-memory streams.
- Valid trusted updates remain mandatory/unattended as today. Invalid or unverifiable data can never force an exit.
- Preserve unrelated changes. Use diff-based checkpoints instead of commits.

---

## Prerequisites That Must Be Supplied Outside This Plan

Do not wire production trust values until the release owner supplies:

- offline-held ES256 private signing key, never stored in this repository;
- pinned P-256 public key(s) and stable `kid` values safe to embed;
- signed `rr.update.v2` manifest fixtures;
- monotonically increasing `release_sequence` rules;
- exact production installer length and SHA-256;
- code-signing certificate and expected Authenticode publisher public-key hash;
- allowlisted versioned manifest/artifact HTTPS origins;
- a bridge-release strategy for already-released unsigned-XML clients.

This plan may implement and test the trust machinery with local test keys. It must not invent production keys/hashes or contact the backend.

## File Structure

**Create**

- `RazorReaper/Models/TrustedUpdateManifest.cs` — signed payload and artifact descriptor.
- `RazorReaper/Services/Updates/IUpdateManifestTransport.cs`
- `RazorReaper/Services/Updates/IUpdateManifestVerifier.cs`
- `RazorReaper/Services/Updates/IUpdateArtifactDownloader.cs`
- `RazorReaper/Services/Updates/IFileHashService.cs`
- `RazorReaper/Services/Updates/IAuthenticodeVerifier.cs`
- `RazorReaper/Services/Updates/IInstallerLauncher.cs`
- `RazorReaper/Services/Implementations/Updates/HttpUpdateManifestTransport.cs`
- `RazorReaper/Services/Implementations/Updates/JwsUpdateManifestVerifier.cs`
- `RazorReaper/Services/Implementations/Updates/UpdateArtifactDownloader.cs`
- `RazorReaper/Services/Implementations/Updates/Sha256FileHashService.cs`
- `RazorReaper/Services/Implementations/Updates/WindowsAuthenticodeVerifier.cs`
- `RazorReaper/Services/Implementations/Updates/InstallerLauncher.cs`
- update tests and local fixtures under `tests/RazorReaper.UnitTests/Updates/` and `Fixtures/Updates/`.

**Modify**

- `RazorReaper/Models/UpdateCheckResult.cs`
- `RazorReaper/Services/IUpdateService.cs`
- `RazorReaper/Services/IAutoUpdateManager.cs`
- `RazorReaper/Services/Implementations/UpdateService.cs`
- `RazorReaper/Services/Implementations/AutoUpdateManager.cs`
- `RazorReaper/App.xaml.cs`
- `RazorReaper/MauiProgram.cs`
- `update.xml` only for documented legacy bridge compatibility; new clients must not consume it.
- `installer/RazorReaper.iss` only if fixed close/restart flags need alignment.

## Task 1: Define the Signed Manifest Contract

**Produces:** A parsed, trusted-only `TrustedUpdateManifest` containing schema, sequence, version, timestamps, mandatory/minimum version, artifact URL/size/SHA-256/publisher pin, changelog and notes.

- [ ] **Step 1: Add local fixture generator/test keys**

Store only a test public/private key pair under `tests/RazorReaper.UnitTests/Fixtures/Updates/`. Label it test-only. Generate fixture signatures offline in test helper code; never reuse them as production keys.

- [ ] **Step 2: Write RED verifier tests**

Test acceptance of one known-good ES256 JWS and rejection of unsigned XML/JSON, payload modification, wrong key, unknown `kid`, `alg` other than exact ES256, response-provided `jku`/`x5u`, unknown schema, invalid version/sequence/time/size/hash, and non-HTTPS/non-allowlisted URLs.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~JwsUpdateManifestVerifier"
```

Expected RED: contract and verifier are absent.

- [ ] **Step 3: Implement minimum JWS verification**

Verify exact compact-JWS signing bytes before JSON deserialization. Pin algorithm and `kid` in local configuration/code. Ignore no errors: return a typed trust failure instead of a partially parsed manifest.

- [ ] **Step 4: Add anti-rollback policy**

Persist the highest trusted `release_sequence` via `IPreferencesStore`. Reject a lower sequence; treat same sequence with different artifact metadata as invalid. Do not use semantic version alone as the rollback anchor.

- [ ] **Step 5: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~JwsUpdateManifestVerifier"
git diff --check
```

## Task 2: Harden Manifest Transport

**Produces:** Bounded HTTPS bytes only; transport itself grants no trust.

- [ ] **Step 1: Write RED transport tests**

Cover timeout/cancellation, maximum manifest bytes, non-success status, redirect outside allowlist and no unsigned fallback after primary failure.

- [ ] **Step 2: Implement bounded transport**

Use a dedicated named/typed `HttpClient`. Disable redirects or validate each hop. Read at most the configured manifest byte ceiling. Return raw bytes plus final URI; never return parsed XML.

- [ ] **Step 3: Replace `UpdateService.FetchManifestAsync`**

Make `UpdateService.CheckForUpdatesAsync` compose transport then verifier. Remove `FallbackManifestUrl` and all unsigned `XDocument` parsing from new clients. Keep notes in the signed JSON payload.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~UpdateManifestTransport|FullyQualifiedName~UpdateService"
```

## Task 3: Stream and Verify the Installer Artifact

**Produces:** An atomically staged file only after exact length, SHA-256 and Authenticode publisher verification.

- [ ] **Step 1: Write RED download tests**

Test unique `.partial` path with `FileMode.CreateNew`, exact/maximum length enforcement while streaming, short/long bodies, SHA mismatch, cancellation cleanup, redirect policy and no overwrite of an existing staged installer.

- [ ] **Step 2: Write RED Authenticode adapter tests**

Keep WinVerifyTrust behind `IAuthenticodeVerifier`. Unit-test caller policy with a fake: invalid chain, unsigned file and wrong publisher pin reject. Platform integration can use a checked-in tiny test-signed fixture only if already legally available locally; otherwise keep the native adapter test as a Windows-only manual/staging gate.

- [ ] **Step 3: Implement downloader and hash service**

Hash while writing. Compare expected length and SHA-256 before atomic rename. Then call Authenticode validation. On any failure, remove only the app-owned partial/staged candidate and return a typed trust result.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~UpdateArtifact"
git diff --check
```

## Task 4: Integrate Trusted Staging into AutoUpdateManager

- [ ] **Step 1: Write RED manager tests**

Cover: untrusted manifest does not download; trusted no-update reports current; artifact failure never sets `IsInstallerReady`; mandatory untrusted data never fires `InstallRequested`; verified artifact does set ready once; cancellation resumes future checks.

- [ ] **Step 2: Remove network-controlled execution fields**

Delete `InstallerArgs` from `UpdateCheckResult` and manifest-to-manager flow. Add explicit trust status/reason only as needed for UI. Keep `DownloadUrl` internal to the trusted descriptor rather than a free string accepted from arbitrary XML.

- [ ] **Step 3: Stage only verified descriptors**

`AutoUpdateManager` asks `IUpdateArtifactDownloader` for a verified staged artifact. Set preferences/readiness only after success. The status message for trust failure is exactly: `Update could not be verified. Your current version was not changed.`

- [ ] **Step 4: Verify manager GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AutoUpdateManagerTrust"
```

## Task 5: Re-Verify and Launch Without a Generated Shell Script

- [ ] **Step 1: Write RED launcher tests**

Cover missing staged file, changed hash after staging, wrong Authenticode publisher, fixed argument list, process-start failure, single-launch interlock and no app-exit request on failure.

- [ ] **Step 2: Implement `InstallerLauncher`**

Immediately before `Process.Start`, re-check size/hash/Authenticode against the trusted descriptor. Use a fixed local Inno argument list. Prefer direct verified installer launch and existing Inno close/restart behavior. Remove `rr_update.cmd`, dynamic shell content and manifest-provided args.

- [ ] **Step 3: Preserve safe application handoff**

`App.HandleInstallRequested` exits only after `InstallerLauncher` confirms a process was successfully started. Otherwise reset pending state and keep the current app open.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~InstallerLauncher|FullyQualifiedName~AutoUpdateManagerTrust"
```

## Task 6: Legacy Bridge and Full Review

- [ ] **Step 1: Document bootstrap limitation**

Legacy released clients cannot be retroactively protected. `update.xml` may point them to one bridge release. The bridge and every later client use only signed v2 and never fall back to XML after a signature/transport error.

- [ ] **Step 2: Run full verification**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Update|FullyQualifiedName~Installer"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
rg -n "FallbackManifestUrl|InstallerArgs|rr_update\.cmd|master/update\.xml|XDocument" .\RazorReaper -g "*.cs"
git diff --check
git status --short
```

Expected search result: no production unsigned fallback, XML manifest parser, generated update script or manifest-controlled installer argument.

## Diff-Based Review Checkpoint

Review `git diff --` for only the updater/test/DI files listed above. Confirm every trust failure is non-destructive, a valid mandatory update still follows current unattended semantics, production trust anchors contain public material only, and no remote operation occurred during implementation.
