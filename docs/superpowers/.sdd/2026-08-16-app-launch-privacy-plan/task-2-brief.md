# Task 2: Add a Strict Debug-Only Local Preview

## Objective

Add one explicit `--local-preview` run mode that is effective only in Debug builds and is safe enough to become the later prerequisite for real marketing screenshots. In preview, the visible MAUI/Blazor UI may render, but startup must not resolve or invoke the production font installer, scope-mode startup, updater, telemetry, access poller, Discord IPC, ARK Link/autostart watcher, license validator/poller, HUD/session watcher, or pending-installer handoff. In normal mode, preserve current registrations, startup queue order, behavior, and Discord semantics exactly.

This task creates the safety boundary only. Do not launch the App in this task; actual preview launch remains forbidden until the full privacy plan and its final independent review are clean.

## Owned files

- Create `RazorReaper/Services/IAppRunMode.cs`.
- Create `RazorReaper/Services/Implementations/AppRunMode.cs`.
- Create at most one narrowly scoped preview composition file under `RazorReaper/Services/Implementations/` for inert Debug-preview implementations/registration helpers.
- Modify `RazorReaper/MauiProgram.cs`.
- Modify `RazorReaper/App.xaml.cs`.
- Modify `RazorReaper/Components/Layout/MainLayout.razor`.
- Modify `RazorReaper/MainPage.xaml.cs` only for preview start-path/cache isolation.
- Modify `RazorReaper/Platforms/Windows/App.xaml.cs`.
- Create `tests/RazorReaper.UnitTests/AppRunModeTests.cs`.
- Create `tests/RazorReaper.UnitTests/LocalPreviewStartupTests.cs`.
- Add only narrowly necessary test fakes under `tests/RazorReaper.UnitTests/Infrastructure/`.
- Write ignored/local report `docs/superpowers/.sdd/2026-08-16-app-launch-privacy-plan/task-2-report.md`.

Do not modify production feature-service internals, pages other than `MainLayout`, settings, privacy/telemetry schemas, updater verification, license policy, authorization, FFmpeg, installer, Shop, Admin, or Bot files in this task.

## Environment and binding safety rules

- Work only in `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82` at detached `HEAD`.
- Do not create/switch a branch, commit, merge, push, publish, deploy, launch the App, access a remote service, run an installer, or spawn subagents.
- Use `apply_patch` for hand edits.
- Every `dotnet test` and `dotnet build` command must use `--no-restore`; Task 1 already generated the required assets from a strictly local cache. Do not run any restore.
- Do not start `RazorReaper.exe`, `dotnet run`, `msbuild /t:Run`, a packaged executable, WebView preview, or browser pointed at the App.
- No test may construct the MAUI `App`, call `MauiProgram.CreateMauiApp`, invoke production service constructors, use `Preferences`, touch the registry/Run key, contact Discord, or open a socket. Exercise pure seams and inert fakes only.

## Root cause and required architecture

The guard must precede production service resolution, not merely their public startup methods:

- `App` currently receives seven effectful services in its constructor, so an `if` inside the constructor body would already be too late for side-effectful constructors.
- `LicenseService` can schedule background server validation from its constructor when a cached key exists.
- `ArkLinkService` performs preference migration during construction.
- `MainLayout.ReflectPage` calls Discord and can initialize IPC even if `App` skipped Discord startup.
- `MainLayout` eagerly resolves HUD/session services whose graph loads overlay settings and registers hotkey-aware script instances.
- `Platforms/Windows/App.xaml.cs` independently resolves Discord, ARK Link, and crosshair/tray services.
- App shutdown independently calls `LaunchPendingInstaller` and telemetry/Discord shutdown paths.

Required shape:

1. `IAppRunMode` exposes only `bool IsLocalPreview { get; }`.
2. `AppRunMode` accepts an argument sequence for deterministic tests. In `#if DEBUG`, an exact case-insensitive `--local-preview` argument enables preview. Outside `#if DEBUG`, it always returns false even if the flag is supplied. Do not infer preview from environment variables, debugger attachment, build configuration names at runtime, or persisted preferences.
3. Create exactly one `AppRunMode` instance in the composition root and register that same instance as `IAppRunMode`.
4. Put the preview decision before resolving effectful production services. `App` should receive the run mode plus a lazy service-resolution boundary (for example `IServiceProvider`) rather than eagerly receiving the seven named services. A small internal pure startup coordinator/actions record is encouraged so tests prove that the preview path does not even invoke the production-integration factory and that normal queue order remains:
   `font-install`, `scope-mode`, `update-check`, `telemetry-start`, `access-gate`, `discord-rpc`, `ark-link`.
5. In preview, use composition-root inert replacements wherever Blazor component injection would otherwise construct an effectful production graph before an orchestration guard. Required replacements include updater, Discord, license, access gate, ARK Link, `IPaletteCommandProvider`, `IHudOverlayService`, and `ISessionHudService`. The palette replacement must expose deterministic, truthful navigation entries without constructing `IEnumerable<AutomationScriptBase>`; the HUD replacement may expose deterministic in-memory settings/state for screenshot rendering but must never enumerate scripts, register hotkeys, read/write AppData, start a timer/window, query a process/server, or persist anything. These types must have no network, timer, `Preferences`, SecureStorage, filesystem, registry, process, Discord, global-hotkey, or environment mutation. Keep them internal and register them only when `IsLocalPreview` is true. Their state/copy must be honest and deterministic; do not invent a real-looking license key or claim a server-validated entitlement.
6. `MainLayout` must skip version-upgrade detection, explicit license validation, access-event subscription/block rendering, Discord page reflection, and eager HUD/session resolution in preview. Preserve local rendering, navigation, appearance, and title behavior. Resolve HUD/session integration lazily only in normal mode; do not add preview checks inside HUD or license implementations.
7. The Windows bootstrap must check the already registered run mode before resolving Discord, ARK Link, crosshair/tray, starting the show-signal listener, or wiring integration callbacks. Rendering the ordinary main window is allowed. Normal mode must keep the existing tray/show/Discord/ARK Link behavior unchanged.
8. All App shutdown and exception paths must tolerate preview integrations being absent. Preview must never call updater handoff/reset, Discord shutdown, telemetry tracking/flush, or other production integration methods. Normal mode retains the existing bounded telemetry flush and installer behavior.
9. Preview must not transiently render `/home`: that page probes username, Wi-Fi, WMI hardware, drives, ARK paths, activity/resources, and may expose private license/path values during its first render. `App`/`MainPage` must select a deterministic safe existing start path (the static `/credits` page is acceptable) before the Blazor router renders; tests must prove preview and normal start paths differ correctly. This does not authorize editing Home or adding fake personal data.
10. `MainPage` preview must not create/map the production hosted-media or Convert preview cache directories. Skip those mappings or use a clearly isolated preview-only root. Normal mapping stays unchanged.
11. The Windows preview instance must use Debug-preview-specific single-instance mutex and show-event names, so it neither exits because of nor signals a normal production-mode instance. Normal names and behavior stay unchanged. Tests may extract a pure naming policy; do not create/open real named synchronization objects in tests.
12. If WebView2 setup is touched, preview must not reset or reuse the normal production profile. A separate clearly named local preview profile is acceptable. Do not expand this task into profile cleanup or filesystem deletion.

## Strict TDD sequence

1. Add tests before any production implementation:
   - exact flag enables preview in Debug;
   - absent or near-match flag (`--local-preview=true`, substring, different token) does not;
   - Release compilation cannot enable preview with the exact flag;
   - preview startup does not invoke or resolve any integration factory/action and queues nothing;
   - normal startup invokes each existing action once in the exact current order;
   - preview layout policy invokes none of version detection, license validation, access subscription/block, Discord reflection, or HUD/session resolution;
   - normal layout policy invokes the existing calls once;
   - preview shutdown policy invokes none of updater handoff, Discord shutdown, or telemetry stop/track;
   - preview composition selects inert replacements for all required component-injected effectful services, while normal composition retains the exact production types.
   - preview selects a safe non-Home start path, skips production cache mappings, and uses distinct mutex/show-event names; normal mode retains `/`, production cache mappings, and production synchronization names.
2. Run the focused Debug test command and observe RED from missing `IAppRunMode`/startup policy or unconditional behavior. A compile failure from missing planned types is acceptable; a typo is not.
3. Implement the minimum production boundary and rerun the focused Debug tests to GREEN.
4. Run the same focused tests in Release and prove the exact flag remains inactive. Use conditional test expectations so Debug and Release both validate the same source contract; do not weaken the Release assertion.
5. Refactor only while both configurations remain green.

## Mandatory verification

Run from the critical worktree, all with `--no-restore`:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AppRunMode|FullyQualifiedName~LocalPreview"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~AppRunMode|FullyQualifiedName~LocalPreview"
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
dotnet build .\RazorReaper\RazorReaper.csproj -c Release --no-restore
git diff --check
```

Also provide static evidence that:

- only Debug compilation can recognize the flag;
- preview exits before production integration resolution/creation;
- no preview path calls `LaunchPendingInstaller`, update check, telemetry, license validation, access start/check, Discord, ARK Link, HUD/session, tray/crosshair, font install, or scope mode;
- normal startup queue names and order are unchanged;
- production service types remain the normal-mode registrations;
- the App was not launched and no network/registry/installer/remote command ran;
- detached `HEAD` and no-commit/no-remote boundaries remain unchanged.

## Stop conditions

Stop as BLOCKED instead of weakening the boundary if:

- proving Release flag rejection would require a restore or remote package;
- a component forces construction of a production integration that cannot be replaced/lazily resolved within the owned files;
- a test would need to launch MAUI or construct a production network/registry service;
- normal startup/Discord/tray behavior would need to be redesigned rather than preserved.

## Report

Write `docs/superpowers/.sdd/2026-08-16-app-launch-privacy-plan/task-2-report.md` with exact files, observed RED, Debug/Release GREEN outputs, startup-order and non-resolution evidence, registration-type evidence, build results, diff checkpoint, and boundary confirmation. Return only status, commits (`none` expected), one-line verification summary, concerns, and report path.
