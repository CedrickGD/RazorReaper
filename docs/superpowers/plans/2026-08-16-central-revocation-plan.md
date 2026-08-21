# Central Access Revocation Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with strict RED/GREEN/refactor checkpoints. Work only in the exact existing detached worktree `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`. Do not create a branch, commit, merge, push, release, deploy, call a remote backend, or perform any remote action. Review with local tests and diffs only.

**Goal:** Replace the current UI-only suspension overlay with a centralized, signed, monotonic access state that denies new effectful work and safely cancels already-running operations while leaving all stop/restore/revert and recovery paths available.

**Architecture:** `AccessGateService` maintains `Unknown|Allowed|Suspended|Banned|Indeterminate` from signed server snapshots and rejects stale revisions. `IActionAuthorizationService` remains the authoritative new-action gate. `SuspensionCoordinator` subscribes to verified transitions and invokes only safety-oriented shutdown adapters for automation, firewall, pending display changes and overlays. UI projects the state but is not enforcement.

**Tech Stack:** .NET 10, ES256/JWS verification shared with updater trust primitives where suitable, `TimeProvider`, Preferences adapter, xUnit fake access transport and revocable services.

**Spec:** Approved centralized ban/suspension architecture; depends on the app-launch/privacy identity foundation and server-authoritative action plan.

## Global Constraints

- Exact detached worktree only; no branch, commit, merge, push, release, deployment or remote action.
- Use local signed fixtures and fake services; never suspend a live identity during testing.
- Unknown is not allowed. Only a fresh verified allowed snapshot or an explicit action decision may permit governed work.
- Safety operations are never blocked: stop, deactivate, restore, revert, panic-stop, exit, update, privacy, support and access re-check.
- Do not change production Discord Rich Presence behavior or stop Discord on suspension.
- Use `apply_patch`, `--no-restore`, and diff-based checkpoints.

---

## Live Backend Prerequisites

Before production signed persistence is enabled, the access backend must supply:

- signed `rr.access.v2` snapshots with stable `kid`/ES256 trust anchor;
- monotonic `revision` per subject;
- `allowed`, `suspended`, `banned` states;
- `issued_at`, `valid_until`, reason and optional `banned_until`;
- newer signed lift responses;
- access state/revision in `/api/actions/authorize` decisions;
- backward-compatible `/api/access/status` behavior for old clients;
- staging fixtures/identities for allowed, timed suspension, permanent ban and lift.

This plan does not authorize backend work or calls.

## File Structure

**Create**

- `RazorReaper/Models/AccessSnapshot.cs`
- `RazorReaper/Services/IAccessSnapshotVerifier.cs`
- `RazorReaper/Services/IAccessRevocationCoordinator.cs`
- `RazorReaper/Services/Implementations/AccessSnapshotVerifier.cs`
- `RazorReaper/Services/Implementations/SuspensionCoordinator.cs`
- tests under `tests/RazorReaper.UnitTests/Access/` and signed fixtures under `Fixtures/Access/`.

**Modify**

- `RazorReaper/Services/IAccessGateService.cs`
- `RazorReaper/Services/Implementations/AccessGateService.cs`
- `RazorReaper/Services/Implementations/ActionAuthorizationService.cs`
- `RazorReaper/App.xaml.cs`, `RazorReaper/MauiProgram.cs`
- `RazorReaper/Components/Layout/MainLayout.razor`
- `RazorReaper/Components/Shared/AccessBlocked.razor`
- revocable service files named in Task 4.

## Task 1: Define Access State and Signed Snapshot Contract

**Produces:** `AccessSnapshot(State, Revision, Reason, BannedUntil, IssuedAtUtc, ValidUntilUtc, IsServerVerified)` and strict `rr.access.v2` verification.

- [ ] **Step 1: Create local signed fixtures**

Use a test-only key and fixtures for allowed, timed suspension, permanent ban, lift, altered payload, expired snapshot and lower revision. Do not store production private keys.

- [ ] **Step 2: Write RED verifier tests**

Reject unsigned/altered/wrong-key/wrong-algorithm/unknown-schema payloads, invalid times, unknown state and malformed revision. Accept exact valid fixtures.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AccessSnapshotVerifier"
```

- [ ] **Step 3: Implement strict verifier**

Reuse only generic, already-tested JWS primitives from updater work; keep access schema policy separate. Verification precedes deserialization/application.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AccessSnapshotVerifier"
```

## Task 2: Expand AccessGateService State and Persistence

**Produces:** `Unknown`, `Allowed`, `Suspended`, `Banned`, `Indeterminate`; compatibility projections for `IsSuspended`, `Mode`, `Reason`, `BannedUntil`.

- [ ] **Step 1: Write RED service tests**

Cover startup Unknown, valid cached ban applied before network, tampered cache rejected, higher revision wins, lower revision cannot overwrite, overlapping timer/manual checks cannot reorder state, permanent ban persists offline, timed suspension blocks until expiry then becomes Indeterminate, signed lift clears, and event fires for reason/revision/state changes.

- [ ] **Step 2: Implement verified cache**

Persist the signed compact snapshot, not editable parsed fields as authority. On load, verify signature/times/revision before applying. Preserve the last verified suspension/ban on network failure.

- [ ] **Step 3: Replace async `Timer` with serialized loop**

Use a cancellable `PeriodicTimer` and one semaphore/state-version path. Manual Re-check may trigger the same serialized method; stale responses cannot apply after a newer revision.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AccessGateService"
git diff --check
```

## Task 3: Integrate Access State with New-Action Authorization

- [ ] **Step 1: Write RED integration tests**

Known suspended/banned state denies locally without mutation; Unknown/Indeterminate calls the authoritative endpoint but any unavailable result denies; local Allowed never bypasses the endpoint; a newer access revision embedded in an action response updates the monitor; older action revision does not regress it.

- [ ] **Step 2: Implement one state-update funnel**

Expose an internal verified/server-decision application method on the access state service. `ActionAuthorizationService` publishes only validated response state. Do not duplicate access booleans.

- [ ] **Step 3: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AccessAuthorizationIntegration"
```

## Task 4: Implement SuspensionCoordinator

**Consumes:** verified transition to Suspended/Banned. **Produces:** best-effort safety stop operations; idempotent and concurrency-safe.

- [ ] **Step 1: Write RED coordinator tests with fakes**

Assert one transition:

- stops every active `AutomationScriptBase`;
- calls `IMacroEngine.StopAll`;
- stops `IFedSuitMacro`;
- stops `IAutoAntidoteService`;
- calls `IDesyncService.DeactivateAsync` to remove the firewall rule;
- calls `IStretchedResService.RevertNow` only for pending confirmation;
- stops/hides crosshair and relevant effectful overlays;
- cancels linked in-flight operation tokens;
- tolerates one revocable throwing and continues the others;
- coalesces repeated identical revision;
- does not call Discord shutdown, updater stop or access-loop stop.

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SuspensionCoordinator"
```

- [ ] **Step 2: Add a narrow revocation interface/adapter**

Prefer a coordinator depending on existing service interfaces and `IEnumerable<AutomationScriptBase>` over adding access logic inside every script. If a generic `IAccessRevocable` is introduced, implement focused adapters and register them explicitly; do not let feature stop paths call authorization.

- [ ] **Step 3: Implement idempotent best-effort coordination**

Subscribe after DI construction/startup. Track handled access revision. Invoke all safety actions even if one fails; log stable local diagnostics without reason/token payload leakage.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SuspensionCoordinator"
```

## Task 5: Wire Revocable Services Without Gating Safety

**Files:**

- `RazorReaper/Services/Automation/AutomationScriptBase.cs`
- `RazorReaper/Services/Automation/MacroEngine.cs`
- `RazorReaper/Services/Automation/FedSuitMacro.cs`
- `RazorReaper/Services/Automation/AutoAntidoteService.cs`
- `RazorReaper/Services/Desync/DesyncService.cs`
- `RazorReaper/Services/StretchedResService.cs`
- `RazorReaper/Services/Implementations/Crosshair/CrosshairService.cs`
- active-operation owners introduced by the authorization plan.

- [ ] **Step 1: Add RED safety tests per service**

For each service, prove Stop/Deactivate/Revert does not call `IActionAuthorizationService`, works while access is suspended and releases held keys/rules/display timers.

- [ ] **Step 2: Expose only missing safety methods**

Reuse existing Stop/Deactivate/Revert APIs. Add cancellation registration for in-flight effectful actions only where absent. Do not refactor feature internals unrelated to safe shutdown.

- [ ] **Step 3: Register coordinator in `MauiProgram` and start in `App`**

Ensure cached ban state can be applied before hotkey-startable work. Normal production startup remains; local preview still suppresses access integration as defined in the launch/privacy plan.

- [ ] **Step 4: Verify safety tests**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AccessSafety|FullyQualifiedName~SuspensionCoordinator"
```

## Task 6: Update Access UI as Projection Only

- [ ] **Step 1: Write RED UI/copy contract tests**

Verify Suspended/Banned show current reason/expiry/re-check; Unknown/Indeterminate use `Couldn’t verify access right now. Nothing was changed.` for action denial; update/privacy/support/re-check/exit remain reachable; UI text never claims the overlay itself enforces access.

- [ ] **Step 2: Update `MainLayout` and `AccessBlocked`**

Preserve current full-screen design for verified suspension/ban. Add minimal initializing/indeterminate state only where necessary. Do not place Discord controls under revocation behavior.

- [ ] **Step 3: Verify UI contract**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AccessUiContract"
```

## Task 7: Full Entry-Point Audit and Verification

- [ ] **Step 1: Run all tests/build**

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Access|FullyQualifiedName~Suspension"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
git diff --check
git status --short
```

- [ ] **Step 2: Audit effectful entry points**

```powershell
rg -n "RegisterHotkey|Process\.Start|ApplyResolution|advfirewall|InjectAsync|ConvertAndReplaceAsync|EnsureFontInstalledAsync|Start\(" .\RazorReaper -g "*.cs" -g "*.razor"
```

For each result, record in review notes either `preflight -> authorize -> mutate` or an explicit safe/uncontrolled classification such as Stop/Restore/Revert/Update/Privacy/Exit.

- [ ] **Step 3: Audit Discord unchanged**

```powershell
git diff -- .\RazorReaper\Services\Implementations\DiscordPresenceService.cs .\RazorReaper\Services\IDiscordPresenceService.cs
```

Expected: no production behavior change from this plan.

## Diff-Based Review Checkpoint

Review only access/coordinator/UI/test and necessary safety-interface changes. Confirm UI is projection, new actions remain server-authorized, cached state is signed and monotonic, all already-running effectful work is stopped best-effort, safety paths remain unconditional, and no live backend action occurred.
