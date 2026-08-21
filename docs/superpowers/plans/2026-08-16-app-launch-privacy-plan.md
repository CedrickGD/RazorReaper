# App Launch, Privacy Consent, and Hybrid Geo Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with strict RED/GREEN/refactor checkpoints. This plan applies only to the exact existing detached worktree `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`. Do not create a branch, commit, merge, push, publish a release, contact a remote service, or perform any other remote action. Review progress through tests and diffs only.

**Goal:** Make local UI launches safe for screenshot work, establish the C# unit-test harness, require explicit telemetry consent, require a separate precise-location opt-in, and emit honest source/accuracy/age metadata with a Cloudflare edge-only coarse fallback.

**Architecture:** A debug-only `--local-preview` run mode suppresses every startup integration that can write externally or contact a service. Production telemetry remains callable from existing features but becomes dormant until `IPrivacyConsentService` reports explicit consent. Device coordinates are acquired only through an injected OS provider after a second opt-in; coarse country/region is added only by the project-controlled ingest edge.

**Tech Stack:** .NET 10 MAUI Blazor Hybrid, xUnit, `TimeProvider`, `HttpMessageHandler` fakes, MAUI Preferences adapter, Windows/MAUI geolocation adapter.

**Spec:** Approved app trust/privacy architecture from the parent task; this plan is the executable privacy/launch slice.

## Global Constraints

- Work only in `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82` at detached `HEAD`; do not create or switch branches.
- No commit, merge, push, release, deployment, API call, browser action, or other remote action.
- Modify only files listed by the active task; preserve unrelated user changes.
- Use `apply_patch` for hand edits. Do not launch the app before the final launch-safety checkpoint.
- Use `--no-restore` for test/build commands. If packages are absent locally, stop and request restore authority rather than downloading silently.
- Discord production behavior remains unchanged. Local preview may skip initialization because it is an explicit debug-only non-production mode.
- Every checkpoint is diff-based: tests, `git diff --check`, `git diff -- <scoped files>`, and `git status --short` replace commits.

---

## File Structure

**Create**

- `tests/RazorReaper.UnitTests/RazorReaper.UnitTests.csproj` — Windows xUnit host referencing the app.
- `tests/RazorReaper.UnitTests/GlobalUsings.cs` — xUnit global imports.
- `tests/RazorReaper.UnitTests/Infrastructure/FakePreferencesStore.cs` — deterministic local preference store.
- `tests/RazorReaper.UnitTests/Infrastructure/FakeOsLocationProvider.cs` — records permission/location calls.
- `tests/RazorReaper.UnitTests/Infrastructure/RecordingHttpMessageHandler.cs` — records outbound telemetry requests without network.
- `tests/RazorReaper.UnitTests/Infrastructure/ManualTimeProvider.cs` — controllable UTC time.
- `tests/RazorReaper.UnitTests/SmokeTests.cs` — proves the harness loads the app assembly.
- `RazorReaper/Configuration/IAppRunMode.cs`, `AppRunMode.cs` — debug-only local-preview policy.
- `RazorReaper/Services/IPreferencesStore.cs`, `IClientIdentityService.cs`, `IPrivacyConsentService.cs`, `IOsLocationProvider.cs` — test seams.
- `RazorReaper/Services/Implementations/MauiPreferencesStore.cs`, `ClientIdentityService.cs`, `PrivacyConsentService.cs`, `MauiOsLocationProvider.cs` — platform adapters.
- `RazorReaper/Models/PrivacyConsentSnapshot.cs`, `TelemetryLocation.cs` — policy/data contracts.
- `RazorReaper/Services/Implementations/Telemetry/TelemetryLocationPolicy.cs` — pure freshness/precision policy.
- `RazorReaper/Components/Shared/PrivacyConsentPrompt.razor` — explicit opt-in UI.
- Unit tests under `tests/RazorReaper.UnitTests/{Configuration,Startup,Identity,Privacy,Location,Telemetry}/` matching tasks below.

**Modify**

- `RazorReaper.sln`, `RazorReaper/RazorReaper.csproj` — test project and internal visibility.
- `RazorReaper/MauiProgram.cs` — DI registrations.
- `RazorReaper/App.xaml.cs`, `RazorReaper/Components/Layout/MainLayout.razor` — preview/startup and consent prompt.
- `RazorReaper/Configuration/AppConfiguration.cs`, `RazorReaper/appsettings.json` — deployment flags, never user consent.
- `RazorReaper/Services/IHwidService.cs`, `HwidService.cs`, `TelemetryService.Identity.cs`, `AccessGateService.cs` — shared identity.
- `RazorReaper/Services/ITelemetryService.cs`, `IDeviceLocationService.cs`, `DeviceLocationService.cs`, telemetry implementation files — consent and v3 geo.
- `RazorReaper/Components/Pages/Settings.razor`, `RazorReaper/wwwroot/css/pages/settings-styles.css` — privacy controls.
- `PRIVACY.md`, `installer/innosetuplicense.txt`, `installer/RazorReaper.iss` — accurate disclosure.

## Task 1: Create the Unit-Test Harness

**Interfaces:** Produces a Windows xUnit project and reusable fakes. No production behavior changes.

- [ ] **Step 1: Scaffold the test project without restore**

```powershell
dotnet new xunit --name RazorReaper.UnitTests --output .\tests\RazorReaper.UnitTests --framework net10.0 --no-restore
dotnet sln .\RazorReaper.sln add .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj
dotnet add .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj reference .\RazorReaper\RazorReaper.csproj
```

Change the test TFM to `net10.0-windows10.0.19041.0`; set `IsTestProject=true`, `IsPackable=false`, nullable and implicit usings enabled. Delete template `UnitTest1.cs`.

- [ ] **Step 2: Write the smoke test and verify RED**

`SmokeTests.AppAssemblyLoads` should assert `typeof(MauiProgram).Assembly.GetName().Name == "RazorReaper"`.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SmokeTests"
```

Expected RED: project-reference/internal configuration failure until the solution/project wiring is complete.

- [ ] **Step 3: Make the harness GREEN**

Add only the required project reference and `InternalsVisibleTo("RazorReaper.UnitTests")`. Add the listed fakes; do not add mocking packages.

- [ ] **Step 4: Verify harness**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SmokeTests"
git diff --check
git diff -- .\RazorReaper.sln .\RazorReaper\RazorReaper.csproj .\tests\RazorReaper.UnitTests
```

Expected GREEN: one passing smoke test.

## Task 2: Add Debug-Only Local Preview

**Interfaces:** `IAppRunMode.IsLocalPreview`; `AppRunMode` recognizes `--local-preview` only under `#if DEBUG`.

- [ ] **Step 1: Write RED tests**

Create `AppRunModeTests` and `LocalPreviewStartupTests` covering:

- flag enables preview in Debug;
- absent flag selects normal mode;
- preview starts none of font install, scope mode, updater, telemetry, access, Discord, or ARK Link;
- preview layout does not validate the license or show an unknown-access block;
- normal mode preserves current startup calls.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AppRunMode|FullyQualifiedName~LocalPreview"
```

Expected RED: missing run-mode abstraction and unconditional startup behavior.

- [ ] **Step 2: Implement minimum GREEN path**

Register `IAppRunMode` in `MauiProgram`. Inject it into `App` and `MainLayout`. Put one early preview guard around existing startup integrations and the existing `MainLayout.ValidateLicenseAsync` call. Do not add preview checks inside feature services.

- [ ] **Step 3: Refactor and verify production invariants**

Keep production startup order and Discord code unchanged. Ensure Release compilation cannot activate preview from an argument.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AppRunMode|FullyQualifiedName~LocalPreview"
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
git diff --check
```

## Task 3: Centralize Client Identity

**Interfaces:** `ClientIdentity(string InstallId, string HardwareId)` and `IClientIdentityService.GetIdentity()`.

- [ ] **Step 1: Write RED identity tests**

Cover preservation of a valid legacy `rr.telemetry.install_id`, replacement of invalid values, identity creation without telemetry startup, and identical IDs across telemetry/access/license consumers.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ClientIdentity"
```

- [ ] **Step 2: Implement identity and preference adapters**

Move install-ID and HWID creation to `ClientIdentityService`. Keep `IHwidService` as a compatibility adapter. Replace direct install-ID preference access in `AccessGateService`. Delete the duplicate identity algorithm from `TelemetryService.Identity.cs` or reduce the partial to delegation.

- [ ] **Step 3: Verify a single identity implementation**

```powershell
rg -n "rr\.telemetry\.install_id|GetWmiProperty|GetMachineGuid|GetOrCreateHardwareId" .\RazorReaper -g "*.cs"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ClientIdentity"
```

Expected: one HWID implementation and one install-ID owner.

## Task 4: Implement Explicit Telemetry and Precise-Location Consent

**Interfaces:** `PrivacyConsentSnapshot(ConsentChoice Telemetry, bool PreciseLocation, int PolicyVersion, DateTimeOffset? DecidedAtUtc)`; change event plus grant/deny/set-precise methods.

- [ ] **Step 1: Write RED privacy tests**

Cover unknown default, no implicit migration to granted, precise default false, precise cannot enable without telemetry, telemetry revocation clears precise, and policy-version persistence.

- [ ] **Step 2: Write RED telemetry lifecycle tests**

Cover no send for unknown/denied, one session start after grant, consent recheck immediately before transport, heartbeat cancellation, and no `session_end` on revocation.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Privacy|FullyQualifiedName~TelemetryConsent"
```

- [ ] **Step 3: Implement consent as a hard boundary**

Inject `IPrivacyConsentService` into `TelemetryService`. Preserve callers such as `DiscordPresenceService`; make `TrackEventAsync` a no-op when consent is absent. Use a consent-linked CTS for heartbeat, location, payload construction, and transport. Treat `TelemetrySettings.Enabled` only as an operator kill switch.

- [ ] **Step 4: Verify GREEN and refactor duplicate checks**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Privacy|FullyQualifiedName~TelemetryConsent"
git diff --check
```

## Task 5: Extract OS Location and Enforce Honest Geo

**Interfaces:** `IOsLocationProvider`; `TelemetryLocation` with source, precision, nullable coordinates/accuracy, observation time, nullable age, signal source, country and region.

- [ ] **Step 1: Write RED device-location tests**

Cover no OS call without precise consent, immediate cache clear on revoke, no cached return while disabled, finite coordinates, non-future timestamps, a 60-minute hard maximum age, and injected time.

- [ ] **Step 2: Write RED payload-policy tests**

Cover measured accuracy preservation, no invented accuracy, coordinate rounding consistent with accuracy, honest `device_fused`/`device_last_known` signal source, and edge `country|region` without coordinates or radius.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Location|FullyQualifiedName~TelemetryPayload"
```

- [ ] **Step 3: Implement platform adapter and pure policy**

Move all MAUI/WinRT geolocation calls into `MauiOsLocationProvider`. Keep consent, caching, freshness, age, precision and serialization in pure services. Remove the current disabled-path `return cachedLocation` behavior.

- [ ] **Step 4: Minimize telemetry v3**

Remove `machine_name`, duplicate `user_label`, process ID, raw HWID and `discord_user` from v3 analytics. Preserve Discord Rich Presence behavior and its preferences. Emit device location only after precise opt-in.

- [ ] **Step 5: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Location|FullyQualifiedName~TelemetryPayload"
rg -n "return cachedLocation|machine_name|user_label|discord_user|client_latitude|client_longitude" .\RazorReaper -g "*.cs"
```

## Task 6: Add Privacy UX and Accurate Documents

- [ ] **Step 1: Write RED copy-contract tests**

Verify prompt/settings/docs do not combine telemetry with precise consent, contain no continued-use-as-consent language, and do not call an unknown-accuracy observation exact/GPS.

- [ ] **Step 2: Implement prompt and Settings section**

Add explicit `Share diagnostics` and `Not now`; add separate precise-location switch, disabled when telemetry is off. Invoke OS permission only on the precise toggle. Display edge region or device source/accuracy/age honestly.

- [ ] **Step 3: Rewrite privacy/legal copy**

Separate necessary update/access/license traffic from optional telemetry, edge-derived approximate location, and optional precise location. Remove telemetry wording from the publisher identity string.

- [ ] **Step 4: Verify full plan**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
git diff --check
git status --short
```

## Live Backend Prerequisites

Before enabling normal consented v3 telemetry, the project-controlled ingest must accept `rr.session.v3`, continue accepting v2 for old clients, add only Cloudflare country/region for the edge fallback, omit edge centroids/fake accuracy, tolerate removed identifying metrics, and treat the shipped app key as public. This plan does not authorize deploying or calling that backend.

## Final Review Checkpoint

Review the scoped diff. Confirm all tests pass, preview mode suppresses all startup integrations, production Discord code is unchanged, telemetry cannot send before consent, and precise location cannot be requested before its separate opt-in. Only after this checkpoint may screenshot work launch:

```powershell
dotnet run --project .\RazorReaper\RazorReaper.csproj -c Debug --no-restore -- --local-preview
```

Launching is not part of this plan execution checkpoint; it is a later explicitly requested action.
