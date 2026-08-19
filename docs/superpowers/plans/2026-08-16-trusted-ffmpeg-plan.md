# Trusted Pinned FFmpeg Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with strict RED/GREEN/refactor checkpoints. Work only in the exact existing detached worktree `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`. Do not create a branch, commit, merge, push, release, download a binary, call a remote service, or perform any remote action. Review with tests and diffs only.

**Goal:** Ensure every FFmpeg process launched by RazorReaper comes from one immutable, licensed, size- and SHA-256-pinned artifact verified at build/install time and again immediately before execution.

**Architecture:** An embedded lock document describes one exact archive and executable. `FfmpegProvider` downloads/extracts only against that lock and returns a `VerifiedExecutableLease`, not a freely trusted string path. `MediaConverter`, `VideoConverter` and `MediaProbe` must acquire a lease at every process boundary; existence alone never means trusted.

**Tech Stack:** .NET 10, SHA-256 streaming, `ZipArchive`, file-share lease, xUnit with tiny synthetic archives and fake downloader/process seams.

**Spec:** Approved trusted pinned FFmpeg architecture; depends on the test harness from the app-launch/privacy plan.

## Global Constraints

- Exact detached worktree only; no branch, commit, merge, push, release or remote action.
- Do not fetch the real FFmpeg archive or add a large binary while implementing this plan.
- Use local synthetic fixtures; use `--no-restore`; use `apply_patch` for edits.
- If production artifact metadata is unavailable, leave media setup fail-closed instead of using `latest` or unverified fallback.
- Preserve unrelated changes and use diff-based checkpoints.

---

## Production Artifact Prerequisite

Before production lock values are enabled, the release owner must supply, from an independently verified source:

- exact FFmpeg upstream build/version identifier;
- fixed versioned HTTPS URL and any byte-identical mirrors;
- archive length and SHA-256;
- exact archive entry path for `ffmpeg.exe`;
- extracted executable length and SHA-256;
- license classification and required redistribution notices.

Do not invent these values. Do not keep `ffmpeg-release-essentials.zip`, `master-latest`, `/latest/`, or another moving URL.

## File Structure

**Create**

- `RazorReaper/Resources/Tools/ffmpeg.lock.json` — immutable production metadata.
- `RazorReaper/Resources/Tools/FFMPEG-NOTICE.txt` — exact redistribution notice.
- `RazorReaper/Services/Media/FfmpegArtifactLock.cs` — strict lock parser/validator.
- `RazorReaper/Services/Media/IFfmpegArtifactVerifier.cs`
- `RazorReaper/Services/Media/FfmpegArtifactVerifier.cs`
- `RazorReaper/Services/Media/VerifiedExecutableLease.cs` — verified path plus held file handle.
- tests under `tests/RazorReaper.UnitTests/Media/` and tiny fixtures under `Fixtures/Ffmpeg/`.

**Modify**

- `RazorReaper/RazorReaper.csproj`
- `RazorReaper/Services/Media/FfmpegProvider.cs`
- `RazorReaper/Services/Media/MediaConverter.cs`
- `RazorReaper/Services/Media/VideoConverter.cs`
- `RazorReaper/Services/Media/MediaProbe.cs`
- `RazorReaper/Services/LoadingScreenService.cs`
- `RazorReaper/Components/Pages/FileConverter.razor`
- `RazorReaper/Components/Pages/LoadingScreen.razor`
- `RazorReaper/MauiProgram.cs`

## Task 1: Define and Validate the Lock Contract

**Produces:** A strict `rr.ffmpeg.lock.v1` parser containing version, fixed URLs, archive size/hash, exact entry, executable size/hash and notice path.

- [ ] **Step 1: Create tiny local archive fixtures**

Use a few-byte fake `ffmpeg.exe` payload inside ZIPs for tests: valid exact path, wrong path, duplicate filename, traversal name, wrong content and high-compression-ratio cases. Fixtures are test data, not executable production tools.

- [ ] **Step 2: Write RED lock tests**

Reject unknown schema, absent version, moving/latest URL, HTTP URL, invalid hash length/characters, non-positive sizes, duplicate URL, missing exact entry and missing notice path.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FfmpegArtifactLock"
```

Expected RED: lock model/parser absent.

- [ ] **Step 3: Implement strict parser**

Parse the embedded resource with explicit field validation. Do not normalize a moving URL into acceptance. Return one typed error; do not fall back to hardcoded URLs.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FfmpegArtifactLock"
```

## Task 2: Verify Archive and Extracted Executable

**Produces:** `IFfmpegArtifactVerifier` that validates archive before opening and validates the exact extracted bytes afterward.

- [ ] **Step 1: Write RED verifier tests**

Cover archive length/hash mismatch, multiple expected entries, filename-only match at the wrong path, traversal, encrypted/unsupported entry, excessive compression ratio, executable size/hash mismatch and successful exact fixture.

- [ ] **Step 2: Implement archive verification first**

Hash and length-check the archive before `ZipArchive.OpenRead`. Locate exactly the locked full entry name. Extract into an app-owned unique directory. Bound uncompressed bytes before/during extraction.

- [ ] **Step 3: Implement executable verification**

Hash and length-check the extracted file. Only a successful exact match may be atomically renamed into the tool cache.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FfmpegArtifactVerifier"
git diff --check
```

## Task 3: Replace Existence with a Verified Lease

**Interfaces:** Replace `FfmpegPath` authority with `AcquireVerifiedAsync(progress, cancellationToken)` returning `VerifiedExecutableLease?`. `IsInstalled` means verified; UI may use a cached verified state but process launch must reacquire.

- [ ] **Step 1: Write RED provider tests**

Cover wrong-hash bundled binary, wrong-hash cached binary, matching bundled preference, matching cached fallback, one shared concurrent fetch, failed source trying only byte-identical mirrors, atomic install, cancellation cleanup and re-verification after file metadata/content change.

- [ ] **Step 2: Write RED lease tests**

Lease holds an open read handle preventing ordinary overwrite/delete between final hash and `Process.Start`; disposing releases it. It carries the locked version/hash for diagnostics, never a claim based solely on path.

- [ ] **Step 3: Implement provider with injected download seam**

Remove `DownloadUrls`. Read URLs only from the lock. Enforce expected/max response bytes while streaming, archive verification, exact extraction and executable verification. On a mismatch, never launch or silently preserve the file. Return typed failure for UI/logging.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FfmpegProvider|FullyQualifiedName~VerifiedExecutableLease"
```

## Task 4: Migrate Every Process Boundary

**Consumes:** `IFfmpegProvider.AcquireVerifiedAsync`. **Produces:** No direct trusted path use.

- [ ] **Step 1: Write RED converter/probe tests**

For `MediaConverter`, `VideoConverter`, and each `MediaProbe` process path, assert no `Process.Start` when lease acquisition fails, lease remains held through process start, arguments remain unchanged, and lease disposes after successful start/failure.

- [ ] **Step 2: Migrate `MediaConverter`**

Acquire a verified lease immediately before each conversion/probe process. Use `ArgumentList` while in scope if the existing code still constructs an argument string; preserve conversion semantics.

- [ ] **Step 3: Migrate `VideoConverter`**

Acquire immediately before process creation. Preserve progress parsing, cancellation and cleanup.

- [ ] **Step 4: Migrate `MediaProbe`**

Both thumbnail and banner/inspection FFmpeg paths must acquire independently. A previous successful probe cannot authorize a later process.

- [ ] **Step 5: Update service/page readiness UX**

`LoadingScreenService`, `FileConverter.razor` and `LoadingScreen.razor` should show `The video converter failed its integrity check and was not run.` on trust failure. Other app features continue.

- [ ] **Step 6: Verify process-boundary GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~MediaConverterTrust|FullyQualifiedName~VideoConverterTrust|FullyQualifiedName~MediaProbeTrust"
```

## Task 5: Add Build-Time Bundled Binary Validation

- [ ] **Step 1: Write an isolated RED MSBuild test**

Use a tiny fake file and test lock in a temporary test-project directory. Assert a mismatched present `Tools/ffmpeg.exe` fails validation and a matching fixture passes. Do not use/download the real binary.

- [ ] **Step 2: Harden `RazorReaper.csproj`**

Embed the lock and notice. If production `Tools/ffmpeg.exe` is present, calculate its SHA-256 with the build task and fail on mismatch before packaging. If absent, the fresh clone still builds and runtime setup remains fail-closed until it can obtain the exact pinned artifact.

- [ ] **Step 3: Verify full plan**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Ffmpeg|FullyQualifiedName~MediaConverterTrust|FullyQualifiedName~VideoConverterTrust|FullyQualifiedName~MediaProbeTrust"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
rg -n "release-essentials|master-latest|/latest/|public string FfmpegPath|FileName = ffmpegPath" .\RazorReaper -g "*.cs"
git diff --check
git status --short
```

Expected search result: no moving URL and no direct unverified FFmpeg process path.

## Diff-Based Review Checkpoint

Review only the media/provider/csproj/test files listed above. Confirm no production binary was added or downloaded, every process path reacquires trust, notice metadata matches the eventual artifact, and a missing/mismatched tool disables only media functionality rather than weakening validation.
