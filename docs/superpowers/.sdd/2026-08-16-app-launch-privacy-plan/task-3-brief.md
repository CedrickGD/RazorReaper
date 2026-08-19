# Task 3: Centralize client identity and close preview identity/network paths

## Objective

Create one lazy, synchronized source of App identity without starting telemetry, preserve every effective legacy install-ID/HWID rule, migrate all direct consumers, and make local-preview navigation unable to touch Preferences, WMI, Registry, telemetry transport, or the usage endpoint.

Work only in the exact detached critical worktree named by `progress.md`. No App launch, restore, branch, commit, merge, push, PR, release, installer, upload, endpoint call, or remote action is allowed.

## Authorized architecture

Create:

- `ClientIdentity(string InstallId, string HardwareId)`.
- `IClientIdentityService.GetIdentity()`.
- `IPreferencesStore` plus `MauiPreferencesStore` with typed `Get`, `Set`, `ContainsKey`, and `Remove` operations needed by later privacy tasks.
- An internal injectable raw-hardware source plus the Windows WMI/Registry implementation.
- `ClientIdentityService` with a side-effect-free constructor, an explicit synchronization boundary, and one cached complete identity record.
- Deterministic inert preview implementations for identity, telemetry, and usage.
- Focused identity/consumer/preview tests and only the test fakes needed for them.

Modify:

- `HwidService` into a pure compatibility adapter over `IClientIdentityService`; its constructor must not acquire identity.
- `TelemetryService`, `AccessGateService`, and `FeedbackService` to inject the identity service and obtain one record per payload operation.
- DI/composition so normal mode has one singleton identity owner and preview mode receives only inert identity/telemetry/usage services; no later registration may override preview via last-registration-wins behavior.
- The existing `FakePreferencesStore` rather than creating a second disconnected fake.

Delete the duplicate telemetry identity partial after its behavior has moved. Keep `LicenseService` and normal `UsageGateService` on the existing `IHwidService` interface for compatibility in this task.

Do not modify consent, location, telemetry-v3 minimization, license/storage logic, quota semantics, updater, FFmpeg, authorization, Discord production behavior, UI pages, Shop, Admin, or Bot behavior.

## Binding install-ID compatibility

- Keep the exact key `rr.telemetry.install_id` and exactly one production owner of it.
- Blank/whitespace/malformed values are invalid and replaced with a new canonical lowercase `D` GUID.
- `Guid.TryParse` defines validity; `Guid.Empty` remains valid.
- A valid stored alternate representation is exposed as canonical `D` form but the stored bytes are not rewritten.
- A valid canonical stored ID causes zero writes.
- Invalid state causes exactly one write under sequential or coordinated concurrent first access.
- The constructor performs no preference/hardware/network/timer work.

## Binding HWID compatibility

The Windows source returns the first non-empty trimmed value for `Win32_Processor.ProcessorId`, `Win32_DiskDrive.SerialNumber`, and `Win32_BaseBoard.SerialNumber`; an absent/failed individual query becomes `UNKNOWN`. Concatenate exactly `cpu-disk-board` in that order. Query MachineGuid only when all three are exactly `UNKNOWN`; if unavailable use `UNKNOWN_GUID`. SHA-256 the raw UTF-8 bytes and return the first 32 uppercase hex characters.

Golden vectors:

- `CPU-DISK-BOARD` -> `5734B40BB3DF5517866D578B18438B61`
- `MACHINE-GUID` -> `41CC1660BD817A5B8F2453926C38DFAB`
- `UNKNOWN_GUID` -> `2AF3AF4BC75E5D22CD66407BF9AB1C88`

Do not implement the stale telemetry comment claiming install-ID fallback. Preserve effective runtime behavior. Never persist, log, serialize, or return raw WMI/Registry material.

## Strict RED tests before production code

Write and observe safe missing-interface/behavior RED tests for all of the following; no RED may invoke real MAUI Preferences, WMI, Registry, timer, or endpoint code:

1. Canonical valid legacy ID is returned without a write.
2. Alternate valid GUID form is accepted/canonicalized in memory without rewrite.
3. `Guid.Empty` remains accepted.
4. Blank, whitespace, and malformed values generate a valid `D` GUID and write exactly once.
5. Sequential calls return the identical record and acquire hardware once.
6. Coordinated concurrent first calls return one identical record, write once, and acquire hardware once.
7. Constructing ClientIdentity, Hwid, and Telemetry services performs zero preference, hardware, HTTP, or timer work.
8. Identity is obtainable without constructing or starting telemetry.
9. Golden HWID vectors, ordering, uppercase, truncation, and MachineGuid-only-on-all-UNKNOWN behavior.
10. `IHwidService` returns exactly the centralized HardwareId.
11. AccessGate and Feedback request bodies contain both values from one supplied record; Feedback no longer directly reads the legacy key.
12. Telemetry obtains identity from the injected service rather than owning/generating it; do not freeze the raw telemetry HWID field because Task 5 removes it.
13. Preview composition resolves inert identity, telemetry, and usage; normal composition resolves the production types.
14. Invoking all inert preview methods performs no identity call, preference/hardware read, timer start, or HTTP request; usage consumption fails closed and status returns no production data.

## Implementation invariants

- Use one singleton `ClientIdentityService`; do not register the concrete and interface separately in a way that constructs two instances.
- Use explicit locking/caching so a transient first-call exception is not permanently cached as `Lazy<T>` failure and a later call can retry.
- Each consumer calls `GetIdentity()` once per payload construction and uses that record.
- Existing endpoints, JSON field names (`install_id`, `hwid`), request timing, license/quota behavior, telemetry schema, startup order, and Discord production semantics do not change.
- Preview replacements live at the composition boundary, not as `IAppRunMode` branches inside production services.

## Verification

Run with `--no-restore` only:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ClientIdentity|FullyQualifiedName~IdentityConsumer|FullyQualifiedName~LocalPreview"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~ClientIdentity|FullyQualifiedName~IdentityConsumer|FullyQualifiedName~LocalPreview"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Release --no-restore
git diff --check
```

Static source gates:

- `rr.telemetry.install_id`: exactly one production owner, ClientIdentityService.
- WMI/Registry hardware access: exactly one production OS-hardware source.
- no old `GetOrCreateHardwareId` or `GetOrCreateInstallId` telemetry algorithms.
- AccessGate and Feedback have no direct Preferences identity read.
- no `GetIdentity()` in a constructor or DI registration factory.
- HwidService is a pure adapter.
- preview mappings cannot be overridden by later registrations.

Write exact RED/GREEN evidence, touched files, source-scan results, and proof of no launch/restore/remote action to `task-3-report.md`. Stop and report any blocker rather than broadening scope.
