# Task 3 Report: Central Client Identity and Inert Preview Consumers

## Status

Task 3 is implemented and locally verified in the required detached critical worktree. The two Important findings from the first independent review were reproduced with safe RED tests and corrected. This report is the updated re-review handoff; independent re-review is still pending. The root-owned `progress.md` was not edited.

The worktree remained detached at `17d9be955dfa6c07bc5da252a4496a08dd335201` throughout (`git branch --show-current` remained empty). No branch, commit, merge, push, PR, publish, release, installer, deployment, App launch, restore, endpoint call, or remote action was performed.

The first final-verification evidence (51 focused tests and 72 full-suite tests) is explicitly superseded: independent review found that its telemetry payload test reached MAUI's static `Preferences.Get`. The corrected telemetry test injects `FakePreferencesStore`, records exact reads, and is covered by the authoritative fresh 55/55 focused and 76/76 full-suite runs below.

## Task 3 files

Created:

- `RazorReaper/Services/IClientIdentityService.cs` — `ClientIdentity` and the functional identity boundary.
- `RazorReaper/Services/IPreferencesStore.cs` — typed preference seam for identity, telemetry, and later privacy work.
- `RazorReaper/Services/Implementations/ClientIdentityService.cs` — lazy, synchronized, single-record identity owner.
- `RazorReaper/Services/Implementations/MauiPreferencesStore.cs` — side-effect-free-constructor MAUI adapter.
- `RazorReaper/Services/Implementations/WindowsRawHardwareIdentitySource.cs` — the only WMI/MachineGuid hardware source, plus its internal raw-source interface.
- `tests/RazorReaper.UnitTests/Identity/ClientIdentityTests.cs`.
- `tests/RazorReaper.UnitTests/Identity/IdentityConsumerTests.cs`.
- `tests/RazorReaper.UnitTests/Identity/LocalPreviewIdentityCompositionTests.cs`.
- This local report.

Modified:

- `RazorReaper/MauiProgram.cs` — preference/raw-source registration and final composition boundary.
- `RazorReaper/Services/Implementations/LocalPreviewComposition.cs` — inert/normal identity, telemetry, and usage mappings and implementations.
- `RazorReaper/Services/Implementations/HwidService.cs` — pure compatibility adapter over `IClientIdentityService`.
- `RazorReaper/Services/Implementations/AccessGateService.cs` — one identity record per status payload.
- `RazorReaper/Services/Implementations/FeedbackService.cs` — one best-effort identity record per feedback payload; no direct identity preference read.
- `RazorReaper/Services/Implementations/Telemetry/TelemetryService.cs` — injected identity and preference abstractions; one identity record per telemetry payload; unchanged v2 RPC fields.
- `RazorReaper/Services/IDiscordPresenceService.cs` — comments now describe telemetry reading the shared keys through the preference abstraction.
- `tests/RazorReaper.UnitTests/Infrastructure/FakePreferencesStore.cs` — the existing fake implements the typed seam and records exact get keys; no second preference fake was introduced.
- `tests/RazorReaper.UnitTests/LocalPreviewStartupTests.cs` — composition coverage expanded from nine to twelve sensitive mappings.

Deleted:

- `RazorReaper/Services/Implementations/Telemetry/TelemetryService.Identity.cs` — duplicate install-ID/WMI/Registry implementation and stale install-ID fallback comment.

Task 1 and Task 2 files/changes already present in the shared worktree were preserved. Task 3 did not modify consent, location, telemetry-v3 minimization, license/storage policy, quota semantics, updater, FFmpeg, authorization, Discord production behavior, UI pages, Shop, Admin, Bot, installer, or release behavior.

## Strict TDD evidence

Every test/build command used `--no-restore`. All hardware tests use injected delegates or an in-memory raw source. All preference tests use `FakePreferencesStore`. All HTTP tests use an in-memory `HttpMessageHandler` with `example.invalid` configuration; no socket or endpoint is contacted.

### Identity core: initial RED/GREEN

Initial RED command:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ClientIdentity"
```

The first run exited 1 for the intended missing production contracts. Compiler errors were `CS0246` for `IPreferencesStore`, `ClientIdentity`, `IClientIdentityService`, and `IRawHardwareIdentitySource`; no identity production implementation existed.

The first GREEN attempt exposed a compile-only adapter mistake: unconstrained generic calls cannot bind to MAUI's concrete static `Preferences.Get/Set` overloads (`CS1503`). The adapter was corrected to use `Preferences.Default.Get<T>/Set<T>` without changing policy or adding a package/restore.

Initial core GREEN after the minimum seams/implementation:

```text
19 passed, 0 failed, 0 skipped
```

Those tests established:

- canonical stored legacy ID with zero writes;
- alternate valid GUID canonicalized only in memory, preserving stored bytes;
- valid `Guid.Empty`;
- blank, whitespace, and malformed replacement with one lowercase `D`-GUID write;
- identical cached record and one hardware acquisition under sequential access;
- coordinated concurrent first access with one record, one preference write, and one hardware acquisition;
- constructor laziness and identity creation without telemetry;
- retry after a transient first acquisition exception rather than caching the exception;
- all three original binding HWID golden vectors, 32-character truncation, and uppercase output;
- exact CPU/disk/board ordering, first non-empty trimming, individual-query `UNKNOWN`, and MachineGuid-only-when-all-`UNKNOWN` behavior;
- lazy `IHwidService` delegation to exactly the central `HardwareId`.

### Independent-review identity finding: RED/GREEN

The first implementation incorrectly trimmed a non-empty MachineGuid and replaced empty/whitespace values with `UNKNOWN_GUID`. Tests were changed first to freeze the effective legacy bytes. The safe focused RED run exited 1 with 20 passed and 3 failed:

- empty string expected `E3B0C44298FC1C149AFBF4C8996FB924`, but the implementation produced the `UNKNOWN_GUID` hash `2AF3AF4BC75E5D22CD66407BF9AB1C88`;
- three spaces expected `0AAD7DA77D2ED59C396C99A74E49F3A4`, but the implementation produced `2AF3AF4BC75E5D22CD66407BF9AB1C88`;
- ` MACHINE-GUID ` expected `8F0AD21DD5C9ECE3259C10357C4601EE`, but the implementation trimmed it and produced the `MACHINE-GUID` hash `41CC1660BD817A5B8F2453926C38DFAB`.

The implementation was then reduced to the binding legacy rule: every non-null MachineGuid string is passed to SHA-256 verbatim; only a null return or thrown read becomes `UNKNOWN_GUID`. Focused GREEN:

```text
23 passed, 0 failed, 0 skipped
```

The exact MachineGuid vectors now covered are:

- ` MACHINE-GUID ` -> `8F0AD21DD5C9ECE3259C10357C4601EE`;
- empty string -> `E3B0C44298FC1C149AFBF4C8996FB924`;
- three spaces -> `0AAD7DA77D2ED59C396C99A74E49F3A4`;
- null -> `2AF3AF4BC75E5D22CD66407BF9AB1C88`;
- thrown Registry delegate -> `2AF3AF4BC75E5D22CD66407BF9AB1C88`.

No test constructs the default Windows raw source; every path is exercised through safe injected WMI/MachineGuid delegates. Therefore these RED/GREEN runs do not access real WMI or Registry.

### Consumers: initial RED/GREEN

The first consumer-test compile exposed that the test project intentionally has no direct `Microsoft.Extensions.*` compile references. The tests were corrected to construct the already-shipped runtime interfaces with reflection/dispatch proxies; no package, project dependency, or restore was added.

Corrected initial RED command:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~IdentityConsumer"
```

It compiled and ran four tests, all four failing for the intended old production shape:

- AccessGate expected `IClientIdentityService`, but its constructor still accepted `IHwidService`.
- Feedback expected `IClientIdentityService`, but its constructor still accepted `IHwidService`.
- Both Telemetry tests expected an injected identity constructor, but Telemetry still owned identity privately.

Initial consumer GREEN after the minimum identity migration:

```text
4 passed, 0 failed, 0 skipped
```

Safe handler recordings prove AccessGate and Feedback emit `install_id` and `hwid` from one supplied record and call `GetIdentity()` once. Telemetry calls the injected identity service once per payload and emits the supplied install ID; the test intentionally does not freeze the telemetry `hwid` field because Task 5 removes it.

### Independent-review telemetry finding: RED/GREEN

Independent review found that the initial telemetry payload test still reached the production static `Preferences.Get` calls used for the v2 `rpc_enabled` and `discord_user` fields. The initial 51/72 final evidence was therefore unsafe and is not used as completion evidence.

Tests were changed first to inject `FakePreferencesStore` into the desired constructor and observe its exact interactions. The safe focused RED run exited 1 with 2 passed and 2 failed: both Telemetry tests expected a six-parameter constructor with `IPreferencesStore` at parameter index 4, while production still exposed five parameters.

Production then received only the preference injection needed by those tests. The two v2 fields and their defaults remain unchanged:

- `rpc_enabled` reads `IDiscordPresenceService.EnabledPreferenceKey` with default `true`;
- `discord_user` reads `IDiscordPresenceService.ConnectedUserPreferenceKey` with default `string.Empty`, and is emitted only when non-blank.

Corrected focused GREEN:

```text
4 passed, 0 failed, 0 skipped
```

The constructor test proves zero identity, location, HTTP-client-factory, request, timer-start, or preference work, including zero fake gets/sets. One telemetry payload proves:

- exactly one `GetIdentity()` call;
- exactly two fake preference reads, in order: `rr.discord.rpc.enabled`, then `rr.discord.user`;
- zero preference writes;
- preserved `rpc_enabled=false` and `discord_user=preview-review-user` JSON fields;
- one in-memory HTTP-client-factory use, one best-effort location call, and one intercepted request.

The corrected Telemetry source has zero `Preferences.Get`, `Preferences.Default.Get`, or `Microsoft.Maui.Storage` matches. Its only two preference reads are `preferencesStore.Get(...)`. The Task 3 telemetry tests therefore cannot reach real MAUI Preferences.

### Preview composition: RED/GREEN

Command:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~LocalPreview"
```

The RED run exited 1 with `CS0246` only for the three planned missing types: `LocalPreviewClientIdentityService`, `LocalPreviewTelemetryService`, and `LocalPreviewUsageGateService`.

Preview GREEN after the minimum composition change:

```text
28 passed, 0 failed, 0 skipped
```

The tests invoke the real private `MauiProgram.ConfigureServices` boundary through reflection, build the actual local service provider, verify the final resolved sensitive types before invoking them, and then exercise every inert method. This prevents a failed type assertion from invoking a production identity/telemetry/usage service. The proof covers:

- deterministic all-zero preview identity with no dependency constructor;
- telemetry start/event/stop no-ops with no identity, timer, preference, hardware, or HTTP dependency;
- usage consumption denied (`Allowed=false`) and status returning no production data;
- exact production mappings retained in normal composition;
- the composition call being last, so later `AddSingleton` descriptors cannot override preview under Microsoft DI's last-registration-wins rule.

## Compatibility and ownership evidence

### Install ID

- The exact key remains `rr.telemetry.install_id`.
- A production-source scan finds that key exactly once, in `ClientIdentityService`.
- `Guid.TryParse` defines validity; `Guid.Empty` remains valid.
- A valid alternate representation is returned as lowercase canonical `D` form without rewriting the stored value.
- Invalid state writes exactly one new lowercase canonical `D` GUID under sequential or coordinated concurrent first access.
- `ClientIdentityService` owns one cached complete `ClientIdentity`; its constructor performs no acquisition.
- Explicit double-checked locking prevents duplicate first work. A failed acquisition leaves the cache empty so a later call retries.

### HWID

- WMI queries are exactly `Win32_Processor.ProcessorId`, `Win32_DiskDrive.SerialNumber`, and `Win32_BaseBoard.SerialNumber` in that order.
- Each WMI query returns the first non-empty trimmed value; individual failure/absence becomes `UNKNOWN`.
- MachineGuid is queried only when all three components are exactly `UNKNOWN`.
- A non-null MachineGuid is preserved byte-for-byte, including empty or whitespace-only strings; only null/failure becomes `UNKNOWN_GUID`.
- SHA-256 hashes the raw UTF-8 material and exposes only the first 32 uppercase hex characters.
- Raw WMI/Registry material is confined to the internal source-to-hasher boundary; it is not persisted, logged, serialized, or returned by any public identity service.
- Original golden results remain:
  - `CPU-DISK-BOARD` -> `5734B40BB3DF5517866D578B18438B61`;
  - `MACHINE-GUID` -> `41CC1660BD817A5B8F2453926C38DFAB`;
  - `UNKNOWN_GUID` -> `2AF3AF4BC75E5D22CD66407BF9AB1C88`.

### Consumers and DI

- AccessGate, Feedback, and Telemetry each call `GetIdentity()` once per payload and reuse that record.
- Feedback retains its historical best-effort identity failure behavior.
- Telemetry retains its v2 `rpc_enabled` and conditional `discord_user` fields and defaults, now through the injected preference abstraction.
- `HwidService` is a constructor-inert compatibility adapter; LicenseService and normal UsageGateService remain on unchanged `IHwidService` contracts.
- Normal composition maps `IClientIdentityService` -> `ClientIdentityService`, `ITelemetryService` -> `TelemetryService`, and `IUsageGateService` -> `UsageGateService`.
- Preview composition maps the same interfaces to parameterless inert replacements.
- `ClientIdentityService` is registered only through `IClientIdentityService`; there is no second concrete singleton registration.
- No constructor or DI registration factory calls `GetIdentity()`.
- Existing endpoint constants, JSON names (`install_id`, `hwid`), license/quota behavior, telemetry v2 schema, startup order, and Discord production semantics were preserved.

## Authoritative final verification checkpoint

These commands were run sequentially after the last production-code change. They supersede the unsafe pre-review 51/72 evidence.

| Command | Exit | Result |
| --- | ---: | --- |
| Focused Debug (`ClientIdentity|IdentityConsumer|LocalPreview`) | 0 | 55 passed, 0 failed, 0 skipped |
| Focused Release (`ClientIdentity|IdentityConsumer|LocalPreview`) | 0 | 55 passed, 0 failed, 0 skipped |
| Full Debug unit suite | 0 | 76 passed, 0 failed, 0 skipped |
| Debug App build | 0 | 0 warnings, 0 errors |
| Release App build | 0 | 0 warnings, 0 errors |
| `git diff --check` | 0 | no whitespace errors |

Exact commands:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ClientIdentity|FullyQualifiedName~IdentityConsumer|FullyQualifiedName~LocalPreview"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~ClientIdentity|FullyQualifiedName~IdentityConsumer|FullyQualifiedName~LocalPreview"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Release --no-restore
git diff --check
```

`NETSDK1057` is the installed preview SDK's informational support-policy message. Both App builds explicitly reported zero warnings and zero errors. `git diff --check` printed only pre-existing working-copy CRLF normalization notices for shared files and returned exit 0 with no whitespace error.

An earlier pre-review attempt ran the full Debug test and Debug build concurrently; the build passed, while the test build hit a shared WinUI `obj/.../input.json` file lock before tests executed. No code was changed for that tooling collision. Every authoritative command above was run sequentially.

## Static source gates

Fresh scans after the authoritative builds:

```text
INSTALL_ID_MATCH_COUNT=1
  RazorReaper/Services/Implementations/ClientIdentityService.cs

OS_HARDWARE_SOURCE_FILE_COUNT=1
  RazorReaper/Services/Implementations/WindowsRawHardwareIdentitySource.cs

OLD_IDENTITY_ALGORITHM_MATCH_COUNT=0
ACCESS_FEEDBACK_DIRECT_MAUI_PREFERENCE_MATCH_COUNT=0
TELEMETRY_DIRECT_MAUI_PREFERENCE_MATCH_COUNT=0
TELEMETRY_ABSTRACTED_PREFERENCE_READ_COUNT=2
TELEMETRY_V2_RPC_FIELD_MATCH_COUNT=2
TEST_DEFAULT_WINDOWS_RAW_SOURCE_CONSTRUCTION_COUNT=0
TASK3_TEST_DIRECT_MAUI_PREFERENCE_MATCH_COUNT=0
TASK3_TEST_REAL_OS_ACCESS_MATCH_COUNT=0
DIFF_CHECK_EXIT=0
HEAD=17d9be955dfa6c07bc5da252a4496a08dd335201
BRANCH=(detached / empty)
```

The OS-hardware scan searches production C# for `System.Management`, the three binding `Win32_*` classes, or `MachineGuid`; only `WindowsRawHardwareIdentitySource.cs` matches. This deliberately distinguishes unrelated application Registry use, such as ArkLink's current-user startup setting, from hardware identity access.

The old-algorithm scan searches production C# for `GetOrCreateHardwareId` or `GetOrCreateInstallId`. The direct-preference scans search AccessGate, Feedback, and Telemetry for static `Preferences.Get/Set`, `Preferences.Default.Get/Set`, or `Microsoft.Maui.Storage` use. Telemetry contains exactly the two expected abstracted reads and the two corresponding v2 field assignments.

The Task 3 test scans search the identity, preview-startup, and shared-fake test sources for direct MAUI Preferences, `ManagementObjectSearcher`, Registry roots, or parameterless construction of the real Windows raw source. They return zero matches.

The production `GetIdentity()` scan contains only the interface/implementations and method-path calls in ClientIdentity, AccessGate, Feedback, Hwid, preview identity, and Telemetry. It contains no constructor or DI-factory call. The sensitive-composition scan contains one `LocalPreviewComposition.Register` invocation at the end of `MauiProgram.ConfigureServices` and the six expected preview/normal identity, telemetry, and usage mappings; there is no later sensitive registration.

## Safety and prohibited-action evidence

- No App executable, `dotnet run`, MAUI window, WebView preview, installer, FFmpeg executable, downloaded artifact, or packaged executable was launched.
- No restore command or implicit restore was used; every test/build command included `--no-restore`.
- No Task 3 test accesses real MAUI Preferences, WMI, Registry, a production timer, a production endpoint, or an OS mutation surface.
- The corrected telemetry tests inject `FakePreferencesStore`; the constructor observes zero preference calls and the payload observes only the two expected fake reads.
- No production endpoint URL was invoked. HTTP behavior tests use an in-memory handler; `example.invalid` is configuration data only.
- No branch creation/switch, commit, merge, push, PR, publish, release, deployment, upload, browser action, API call, or other remote action occurred.
- The detached HEAD remained unchanged, and unrelated/shared Task 1/Task 2/user changes were preserved.

## Re-review handoff

The independent re-reviewer should inspect the working-tree diff rather than a commit range because this task is deliberately uncommitted at detached HEAD. Review against:

- `docs/superpowers/specs/2026-08-16-app-trust-privacy-design.md`, identity/privacy seams and verification requirements;
- `docs/superpowers/plans/2026-08-16-app-launch-privacy-plan.md`, Task 3;
- `docs/superpowers/.sdd/2026-08-16-app-launch-privacy-plan/task-3-brief.md`, all binding vectors, concurrency, Feedback, and preview-composition additions;
- this report's corrected RED/GREEN chronology, file list, source gates, and authoritative verification evidence.
