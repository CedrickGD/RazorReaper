# Server-Authoritative License and Quota Actions Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with strict RED/GREEN/refactor checkpoints. Work only in the exact existing detached worktree `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`. Do not create a branch, commit, merge, push, release, deploy, call a live backend, or perform any remote action. Review through local tests and diffs only.

**Goal:** Move ban, live license and monthly-quota decisions into one server-authoritative call made after local non-mutating validation and before the first protected side effect across all seven quota features and every UI/hotkey entry point.

**Architecture:** `IActionAuthorizationService` sends an idempotent action intent to a typed API client. The backend atomically resolves access, live entitlement and quota; the desktop treats only an explicit matching `allowed` decision as authority. Each action-owning service performs local preflight, authorizes, then mutates. Cached license and usage status remain display-only; stop/restore/revert paths are unconditional.

**Tech Stack:** .NET 10 typed HTTP client, JSON contracts, `TimeProvider`, SecureStorage adapter, xUnit recording API and side-effect fakes.

**Spec:** Approved server-authoritative action architecture; depends on the privacy identity/test foundation and should precede central revocation.

## Global Constraints

- Exact detached worktree only; no branch, commit, merge, push, release, deployment or remote action.
- Unit tests use fake transports and fake side-effect adapters; do not consume live quotas or licenses.
- No locally cached `IsPremium`, chip status, UI overlay or preference may authorize an action.
- Local validation occurs before authorization; authorization occurs before the first mutation.
- Stop, restore, deactivate, revert, panic-stop, default-font escape and cleanup never require authorization.
- Production Discord behavior remains unchanged.
- Use `apply_patch`, `--no-restore`, and diff-based checkpoints.

---

## Live Backend Prerequisites

Do not switch production features to fail-closed authorization until the backend has deployed:

- `POST /api/actions/authorize` accepting `rr.action.v1`;
- atomic access + live license + quota evaluation;
- idempotency keyed by `action_id`;
- stable reason codes: `allowed`, `suspended`, `banned`, `license_invalid`, `license_expired`, `quota_exhausted`, `client_unsupported`, `request_invalid`;
- authoritative unlimited/limit/remaining/charged fields;
- the existing keys `sky_changer`, `loading_screen`, `fonts`, `desync`, `stretched_res`, `fed_suit`, `input_scripts`;
- opaque device-bound license-token exchange;
- backward-compatible legacy `/api/usage/*` and `/api/license/*` for old clients;
- staging identities for free, premium, expired, exhausted, suspended and banned cases.

This plan does not authorize implementing, deploying or calling that backend.

## File Structure

**Create**

- `RazorReaper/Models/ActionAuthorizationModels.cs`
- `RazorReaper/Models/LicenseSnapshot.cs`
- `RazorReaper/Services/IActionAuthorizationService.cs`
- `RazorReaper/Services/IAuthorizationApiClient.cs`
- `RazorReaper/Services/ISecureValueStore.cs`
- `RazorReaper/Services/IFontPresetSelectionService.cs`
- `RazorReaper/Services/Implementations/ActionAuthorizationService.cs`
- `RazorReaper/Services/Implementations/AuthorizationApiClient.cs`
- `RazorReaper/Services/Implementations/MauiSecureValueStore.cs`
- `RazorReaper/Services/Implementations/FontPresetSelectionService.cs`
- optional `RazorReaper/Services/Automation/IAsyncHotkeyCommandDispatcher.cs` and implementation if needed to observe async start commands.
- authorization/licensing/feature tests and JSON fixtures under `tests/RazorReaper.UnitTests/{Authorization,Licensing,Automation}/`.

**Modify**

- `RazorReaper/Configuration/AppConfiguration.cs`, `appsettings.json`, `MauiProgram.cs`
- `RazorReaper/Services/ILicenseService.cs`, `IUsageGateService.cs`
- `RazorReaper/Services/Implementations/LicenseService.cs`, `UsageGateService.cs`
- `RazorReaper/Components/Pages/Home.razor`, `SharedNavbar.razor`, `PremiumLock.razor`, `UsageChip.razor`
- action owners/callers listed by vertical tasks below.

## Task 1: Define the Action Decision Contract

**Produces:** `ActionAuthorizationRequest`, `ActionAuthorizationDecision`, access/entitlement/quota subrecords and typed reason codes.

- [ ] **Step 1: Add local JSON fixtures**

Create allowed-free, allowed-premium, quota-denied, license-expired, suspended, banned, mismatched-action and malformed fixtures. Use pseudonymous fake IDs.

- [ ] **Step 2: Write RED API-client tests**

Cover exact schema/feature/operation/identity/token/version/action ID serialization and strict matching response parsing. Reject non-success HTTP, timeout, malformed/unknown schema and mismatched `action_id`.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AuthorizationApiClient"
```

- [ ] **Step 3: Implement typed client**

Use a dedicated short-timeout client and `IClientIdentityService`. Never log raw token/request body. Map every transport/protocol failure to typed `authorization_unavailable`, not allowed.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AuthorizationApiClient"
```

## Task 2: Implement Authorization Policy and Idempotency

**Produces:** `AuthorizeAsync(feature, operation, actionId, cancellationToken)`; callers create one UUID per user intent and reuse it on retry.

- [ ] **Step 1: Write RED policy tests**

Cover premium still calls server, free allowed/denied mapping, all network/protocol failures deny, local known suspension short-circuits to deny but local allowed state never grants, duplicate intent reuses ID, and returned remaining counts raise status refresh only after a valid decision.

- [ ] **Step 2: Implement minimum fail-closed policy**

Only `allowed=true` with matching action ID permits mutation. Do not consult `ILicenseService.IsPremium` or `IUsageGateService` status for authority.

- [ ] **Step 3: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ActionAuthorizationService"
git diff --check
```

## Task 3: Make License State Display-Only and Migrate Token Storage

- [ ] **Step 1: Write RED license tests**

Cover legacy raw key exchange into SecureStorage, raw preference removal after successful exchange, cached snapshot freshness/tier/expiry for display, explicit rejection clearing cached display state, network failure retaining only a stale display snapshot, and serialized `PeriodicTimer` validation.

- [ ] **Step 2: Implement `LicenseSnapshot` and secure adapter**

Keep compatibility properties/events for current UI, but document and enforce that none authorize actions. Replace async `Timer` callback with one cancellable periodic loop.

- [ ] **Step 3: Update display consumers**

`Home`, `SharedNavbar`, `PremiumLock` and celebration UI read display snapshot. Do not alter visual behavior beyond honest unknown/stale state. `PremiumLock` remains cosmetic.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~License"
```

## Task 4: Reference Vertical Slice — Sky Changer

**Files:** `ISkyInjectorService.cs`, `Implementations/CustomLab/SkyInjectorService.cs`, `Components/Pages/CustomLab/SkyInjector.razor`, `SkyInjectorAuthorizationTests.cs`.

- [ ] **Step 1: Write RED order tests**

Assert invalid options fail before authorization; denied authorization opens/writes zero target files; allowed flow records `validate -> authorize -> first write`; duplicate click uses one intent; restore performs no authorization.

- [ ] **Step 2: Move authorization into `SkyInjectorService.InjectAsync`**

Pass/create the action intent at the service boundary. Remove `TryConsumeAsync` and quota copy from the Razor component. Return typed denial details so the page retains the current quota message.

- [ ] **Step 3: Verify and review this pattern before continuing**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SkyInjectorAuthorization"
git diff -- .\RazorReaper\Services\ISkyInjectorService.cs .\RazorReaper\Services\Implementations\CustomLab\SkyInjectorService.cs .\RazorReaper\Components\Pages\CustomLab\SkyInjector.razor .\tests\RazorReaper.UnitTests\Authorization\SkyInjectorAuthorizationTests.cs
```

Do not migrate the other six flows until this diff is reviewed.

## Task 5: Loading-Screen Authorization

**Files:** `LoadingScreenService.cs`, `LoadingScreen.razor`, `FileConverter.razor`, `LoadingScreenAuthorizationTests.cs`.

- [ ] Write RED tests for both replace entry points, converter/input preflight, denial before ARK file writes, and unconditional restore.
- [ ] Authorize `loading_screen/replace` inside `ReplaceAsync`/`ConvertAndReplaceAsync`; avoid double authorization when one calls the other by using one private authorized core.
- [ ] Remove direct page gate calls; preserve File Converter conversion as free and gate only replace-into-game.
- [ ] Run:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~LoadingScreenAuthorization"
```

## Task 6: Font Selection Authorization

**Files:** new `IFontPresetSelectionService`/implementation, `FontSettingsCard.razor`, `MauiProgram.cs`, `FontPresetAuthorizationTests.cs`.

- [ ] Write RED tests: current preset no-op, default system font unconditional, non-default denial changes no preference/UI stack, allowed order is validate-authorize-persist/install.
- [ ] Move selection policy out of the Razor component. Keep background startup font availability separate from quota-charged user selection.
- [ ] Run:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FontPresetAuthorization"
```

## Task 7: Desync Authorization

**Files:** `Services/Desync/DesyncService.cs`, `DesyncAuthorizationTests.cs`.

- [ ] Write RED tests for admin/path/startup-cleanup preflight, authorize-before-`netsh add`, no rule on denial/unavailable, and unconditional deactivate/delete.
- [ ] Move authorization ahead of the existing firewall mutation; remove rollback-as-denial enforcement because mutation never occurs on denial.
- [ ] Run:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~DesyncAuthorization"
```

## Task 8: Stretched-Resolution Authorization

**Files:** `Services/StretchedResService.cs`, `Components/Pages/StretchedRes.razor`, `StretchedResolutionAuthorizationTests.cs`.

- [ ] Write RED tests for validation-before-network, authorize-before display API, denial/no mode change, one in-flight intent, and unconditional revert/restore/confirm.
- [ ] Change apply to an async service API and update callers. Decide explicitly in the test whether writing ARK INI shares the already-authorized apply intent; never charge twice by accident.
- [ ] Run:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~StretchedResolutionAuthorization"
```

## Task 9: Fed Suit Authorization

**Files:** `Automation/FedSuitMacro.cs`, `Pages/FedSuit.razor`, relevant `HotkeyRegistry.cs`, `FedSuitAuthorizationTests.cs`.

- [ ] Write RED tests: authorization completes before `_running` and runner start; denial starts nothing; concurrent UI/hotkey starts coalesce; stop hotkey always works.
- [ ] Introduce `StartAsync` and a guarded observed hotkey bridge. Remove start-then-check quota task.
- [ ] Run:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~FedSuitAuthorization"
```

## Task 10: Shared Input-Script Authorization

**Files:** `AutomationScriptBase.cs`, `AutomationHotkeys.cs`, `HotkeyRegistry.cs`, script-page compile callers, optional async dispatcher, tests.

- [ ] Write RED tests: state remains Off during authorization; denial/unavailable creates no CTS/task/input; allowed sets Running only after decision; one shared `input_scripts` feature with script key as operation metadata; stop is immediate; hotkey async exceptions are observed.
- [ ] Inject authorization through the base constructor or one explicit service, removing `IPlatformApplication.Current` service-location and `EnforceInputQuotaAsync`.
- [ ] Update all 16 derived constructors and compile callers mechanically; do not change individual script behavior.
- [ ] Run:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AutomationScriptAuthorization|FullyQualifiedName~AsyncHotkey"
```

## Task 11: Retain Status Chips Without Authority

- [ ] Write RED status tests proving stale/missing status only hides the chip and never affects authorization.
- [ ] Keep `GetStatusAsync` and `OnUsageChanged`; remove/obsolete `TryConsumeAsync` only after searches prove no caller.
- [ ] Run final checks:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Authorization|FullyQualifiedName~License"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
rg -n "TryConsumeAsync" .\RazorReaper -g "*.cs" -g "*.razor"
rg -n "_licenseService\.IsPremium|LicenseService\.IsPremium" .\RazorReaper\Services -g "*.cs"
git diff --check
git status --short
```

Expected: no protected action uses legacy consume or local premium state.

## Diff-Based Review Checkpoints

Review after Task 4 and after each remaining vertical feature, not only at the end. For every action, the diff and tests must demonstrate `non-mutating preflight -> authorize -> mutate`, while the paired stop/restore/revert path contains no authorization. No live endpoint is called during implementation.
