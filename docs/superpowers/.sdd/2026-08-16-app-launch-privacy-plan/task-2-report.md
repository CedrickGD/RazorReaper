# Task 2 Report: Strict Debug-Only Local Preview

## Status

Implemented locally in the required detached worktree. No branch, commit, push, publish, restore, App launch, installer, registry command, or remote/network command was performed.

Detached baseline/HEAD remained `17d9be955dfa6c07bc5da252a4496a08dd335201` (`HEAD (no branch)`).

## Task 2 files

- Created `RazorReaper/Services/IAppRunMode.cs`.
- Created `RazorReaper/Services/Implementations/AppRunMode.cs`.
- Created `RazorReaper/Services/Implementations/LocalPreviewComposition.cs` (the single preview composition/policy file).
- Modified `RazorReaper/MauiProgram.cs`.
- Modified `RazorReaper/App.xaml.cs`.
- Modified `RazorReaper/Components/Layout/MainLayout.razor`.
- Modified `RazorReaper/MainPage.xaml.cs` only for start-path and production-cache isolation.
- Modified `RazorReaper/Platforms/Windows/App.xaml.cs`.
- Created `tests/RazorReaper.UnitTests/AppRunModeTests.cs`.
- Created `tests/RazorReaper.UnitTests/LocalPreviewStartupTests.cs`.
- Created this ignored/local report.

Task 1's existing solution/project/test-harness changes were preserved. No feature-service implementation, page other than `MainLayout`, setting/schema, updater verification, license policy, authorization, FFmpeg, installer, Shop, Admin, or Bot file was modified.

## TDD evidence

### RED

The first focused Debug compile failed with exit code 1. It exposed an unintended test-only `Microsoft.Extensions.DependencyInjection` compile dependency in addition to the planned missing production types. The test was corrected to exercise a pure composition plan and instantiate only inert preview replacements; no production implementation existed yet.

The corrected focused RED command was:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AppRunMode|FullyQualifiedName~LocalPreview"
```

It failed with exit code 1 solely because the planned contract did not exist: `AppStartupActions`, `LocalPreviewLayoutActions`, `AppShutdownActions`, `LocalPreviewServicePlan`, and `IAppRunMode` all produced expected `CS0246` missing-type errors.

Three later launch-safety findings each received their own strict RED before implementation:

- Global Search and `/hud-overlay` would have resolved production automation/HUD graphs. The focused compile failed only for the missing inert palette, HUD, and session-HUD types.
- Safe start path/cache/synchronization tests failed only for missing `LocalPreviewLaunchPolicy` and Windows synchronization-name policy members.
- Elevation return routing could have overridden `/credits`; the focused compile failed only for the missing preview elevation-return policy.

The independent review then identified four additional fail-open paths. Focused tests were added before their production implementations; the corrected Debug RED failed with exit code 1 only for the planned missing `LocalPreviewUpdateService`, the authoritative/nullable `WindowsBootstrapPolicy.ShouldWireIntegrations` contract, `LocalPreviewLoggingPolicy`, and `LocalPreviewWebViewProfilePolicy`. The review fixes prove:

- component injection resolves a parameterless, version-only `IUpdateService` in Preview while Normal retains exact `UpdateService` registration;
- Preview logging selects neither production diagnostics/preferences nor a file sink;
- an isolated WebView profile preparation failure propagates in Preview and aborts startup, while Normal retains its historical fallback;
- the pre-mutex Preview flag is authoritative even when the DI run mode is missing or contradicts it, and a registered Preview also fails closed.

### GREEN

After the minimum production boundary was implemented:

- Focused Debug: 32 passed, 0 failed, 0 skipped.
- Focused Release: 32 passed, 0 failed, 0 skipped. The exact `--local-preview` flag remains false in Release compilation.
- Full Debug unit suite: 45 passed, 0 failed, 0 skipped.
- Debug App build: succeeded, 0 warnings, 0 errors.
- Release App build: succeeded, 0 warnings, 0 errors.

`NETSDK1057` is the SDK's informational preview-version message; both App builds explicitly reported zero warnings and zero errors.

## Flag and composition evidence

- `AppRunMode` accepts only an argument sequence. Exact case-insensitive token equality is used inside `#if DEBUG`; the `#else` branch always assigns `false`.
- No environment variable, debugger state, runtime build-name string, preference, or persisted setting can enable preview.
- `MauiProgram.CreateMauiApp` creates exactly one `AppRunMode` instance and passes it to `LocalPreviewComposition.Register`; the same instance is registered as `IAppRunMode`.
- The composition plan selects singleton implementation types before container build:

| Service | Debug preview | Normal mode |
| --- | --- | --- |
| `IUpdateService` | `LocalPreviewUpdateService` | `UpdateService` |
| `IAutoUpdateManager` | `LocalPreviewAutoUpdateManager` | `AutoUpdateManager` |
| `IDiscordPresenceService` | `LocalPreviewDiscordPresenceService` | `DiscordPresenceService` |
| `ILicenseService` | `LocalPreviewLicenseService` | `LicenseService` |
| `IAccessGateService` | `LocalPreviewAccessGateService` | `AccessGateService` |
| `IArkLinkService` | `LocalPreviewArkLinkService` | `ArkLinkService` |
| `IPaletteCommandProvider` | `LocalPreviewPaletteCommandProvider` | `PaletteCommandProvider` |
| `IHudOverlayService` | `LocalPreviewHudOverlayService` | `HudOverlayService` |
| `ISessionHudService` | `LocalPreviewSessionHudService` | `SessionHudService` |

- Inert replacements have no constructor dependencies and no timers, preferences, SecureStorage, filesystem, registry, process, Discord, network, environment, global-hotkey, or external state mutation. They expose deterministic disabled/free state, an empty license key, no entitlement, no server-validation claim, empty command actions, and deterministic non-persisted HUD defaults.
- `LocalPreviewUpdateService` exposes only the local assembly version and a deterministic disabled result with Unix-epoch check time; it never constructs `HttpClient`, `ITelemetryService`, or the production `UpdateService`. `MainLayout`, `SharedNavbar`, and any other `IUpdateService` component injection therefore resolve the inert mapping in Preview. The registration plan test covers all nine mappings and asserts this Preview type is parameterless; Normal remains exactly `UpdateService`.
- The Preview palette provider never requests `IEnumerable<AutomationScriptBase>`, so rendering `<GlobalSearch />` cannot construct scripts or call their hotkey registration path. Navigation/page/deep-link entries remain supplied truthfully by Global Search itself; only unsafe executable command entries are absent.
- Direct `/hud-overlay` injection resolves the inert HUD service, and the inert session-HUD service cannot construct automation, process, server-query, settings-file, timer, or overlay-window graphs.

## Startup order and non-resolution evidence

- `App` now receives only `IAppRunMode` and `IServiceProvider`; none of the seven effectful production integrations is constructor-resolved.
- `AppStartupPolicy.Queue` returns before calling the action factory or queue callback in preview. The focused test proves both factory and queue call counts remain zero.
- Normal-mode tests invoke every action exactly once and assert this literal order:
  1. `font-install`
  2. `scope-mode`
  3. `update-check`
  4. `telemetry-start`
  5. `access-gate`
  6. `discord-rpc`
  7. `ark-link`
- The normal-only action factory resolves services in the former constructor-injection order, subscribes the existing updater handoff, and preserves each existing startup operation.

## Layout, Windows bootstrap, shutdown, and profile evidence

- `MainLayout` keeps local navigation, appearance, rendering, and title behavior available in preview.
- Its preview policy skips HUD/session resolution, version-upgrade detection, license validation, access-event subscription, access-block rendering, initial Discord/HUD reflection, and navigation reflection subscription. Tests prove every corresponding action/provider remains at zero calls.
- Normal layout tests prove each existing integration action/provider is invoked exactly once.
- `Platforms/Windows/App.xaml.cs` resolves only `IAppRunMode` before the preview return. The pre-mutex `_isLocalPreview` flag is passed separately and is authoritative: tests prove the integration wiring stays off for authoritative Preview with null DI and with contradictory Normal DI, and also stays off for a registered Preview. Preview exits before Discord, ARK Link, crosshair/tray, show-signal listener, closing-to-tray behavior, and integration callbacks are resolved or wired. Normal behavior after the guard is unchanged, including the historical null-DI fallback.
- The App subscribes production exception/process hooks only outside preview. Window shutdown is still guarded separately.
- `AppShutdownPolicy` returns before its action factory in preview. Tests prove zero updater handoff/reset, Discord shutdown, telemetry stop, or telemetry tracking calls. Normal supplied actions are each invoked once.
- `FlushTelemetryShutdown` also has a direct preview/null guard as defense in depth.
- Preview uses `WebView2-local-preview` regardless of elevation and never resets or reuses `WebView2`/`WebView2-admin`. Profile preparation is fail-closed: directory/environment setup exceptions propagate in Preview and abort startup rather than falling back to WebView2's default profile. A pure test proves the original exception instance propagates after exactly one preparation attempt. Normal folder selection and its historical setup fallback are unchanged.
- Preview logging initializes only the in-process level switch and Serilog Debug sink. It returns before every `AppDiagnostics` preference/folder call, does not create or open a production/custom log directory or file, and has no file sink. A pure policy test proves Preview selects `UseProductionDiagnostics=false` and `UseFileSink=false`, while Normal selects both production behaviors.
- `MainPage` assigns StartPath before the WebView is attached: Preview receives static `/credits`; normal mode explicitly retains `/`. Home is therefore not rendered transiently.
- Preview never consumes the production `--elevated-page` return route, so an extra elevation argument cannot redirect the safe start page to Home; the elevation service is resolved lazily only in normal mode.
- `MainPage` checks the run-mode policy before either hosted-media or Convert preview cache directory/property is used. Preview creates/maps neither production cache; normal mappings remain byte-for-byte inside the normal branch.
- Preview Windows synchronization names are `RazorReaper_LocalPreview_SingleInstance_Mutex` and `RazorReaper_LocalPreview_ShowWindow_Event`; normal names remain exactly `RazorReaper_SingleInstance_Mutex` and `RazorReaper_ShowWindow_Event`. The WinUI constructor uses the shared Debug-only flag recognizer without creating a second `AppRunMode` instance. A duplicate Preview never runs the normal process-window fallback.

## Mandatory command checkpoint

All commands used `--no-restore`:

```text
Focused Debug tests: exit 0, 32/32 passed
Focused Release tests: exit 0, 32/32 passed
Full Debug tests: exit 0, 45/45 passed
Debug App build: exit 0, 0 warnings, 0 errors
Release App build: exit 0, 0 warnings, 0 errors
git diff --check: exit 0
```

No `RazorReaper.exe`, `dotnet run`, MAUI/WebView preview, packaged executable, installer, browser-to-App, production service constructor, socket, network/remote service, registry/Run-key command, restore, branch, commit, merge, push, publish, or deploy command was run.
