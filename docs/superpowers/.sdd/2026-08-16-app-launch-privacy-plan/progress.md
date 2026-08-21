# SDD ledger — plan: docs/superpowers/plans/2026-08-16-app-launch-privacy-plan.md

## Binding context

- Spec: `docs/superpowers/specs/2026-08-16-app-trust-privacy-design.md`.
- Workspace is the user's exact critical linked worktree at detached `HEAD`: `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`.
- No branch, commit, merge, push, PR, installer, release, deployment, remote service call, or production action is allowed. All App work stays local until explicit final App release approval.
- Use strict RED -> observed RED -> minimum GREEN -> refactor -> independent task review. Commands use `--no-restore` except the narrowly ruled local-cache assets generation below.
- Do not launch the App before the plan's final launch-safety checkpoint. Discord production semantics remain unchanged.

## Environment / baseline

- Linked-worktree detection: git dir is `...\.git\worktrees\navbar-search-usability-f99f82`, common dir is the parent repository `.git`, and `HEAD` is detached at `17d9be955dfa6c07bc5da252a4496a08dd335201`.
- Starting status: only the pre-existing untracked `docs/` planning tree; no tracked App diff.
- Installed SDK is `.NET 10.0.400-preview`; `maui-windows` is installed. Fresh baseline `dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore` passed with 0 warnings and 0 errors (the SDK printed its informational preview-support message).
- The installed .NET 10 template requests newer packages that are not cached. Ruling: normalize the scaffold before restore to the complete older local package graph already present: xunit 2.9.2, Microsoft.NET.Test.Sdk 17.12.0, xunit.runner.visualstudio 2.8.2, coverlet.collector 6.0.2. Their net10-relevant direct/transitive `.nupkg` closure is present locally (including TestHost/ObjectModel 17.12.0, xunit core/assert/extensibility/abstractions, Newtonsoft.Json, and System.Reflection.Metadata). Cost if wrong: the local-only restore fails and Task 1 stops; it cannot fall through to a remote source.

## Root-cause record

- Inaccurate location is not one isolated rendering bug. The current design conflates optional telemetry initialization, device geolocation, cached coordinates, and edge/network meaning; it can reuse stale data and does not preserve honest source/accuracy/age semantics.
- Privacy failure is at the transport boundary: existing installations are effectively telemetry-enabled by deployment configuration, there is no explicit optional-analytics consent surface, and precise OS location is not a separate opt-in.
- Screenshot-launch risk is at startup orchestration: ordinary Debug launch initializes external integrations. A compile-time Debug-only `--local-preview` boundary must suppress startup integrations before the App may be opened for marketing capture.
- Hypothesis confirmed by the approved architecture: separate run mode, identity, consent, OS provider, time, and transport seams allow policy to be proven without real OS/network activity; fixes at UI copy alone would leave the underlying boundary unsafe.

## Pre-flight interface scan

| Producer task | Consumer task | Shared file/interface | Finding / ruling |
|---|---|---|---|
| 1 | 2-6 | Windows xUnit host, fakes, solution/project wiring | Load-bearing foundation. New projects cannot run with `--no-restore` until `project.assets.json` exists. Ruling: if exact packages are confirmed in the local cache, allow one restore using only an explicit local hierarchical package source and no configured remote source; all build/test commands remain `--no-restore`. Cost if wrong: local-source layout fails and Task 1 stops, but no network is contacted. |
| 1 | 2-6 | Test fakes vs interfaces introduced later | Plan lists fakes before their production interfaces. Ruling: Task 1 creates only dependency-free reusable primitives that compile now; later owning tasks adapt them to newly introduced interfaces. Do not pull production interfaces into Task 1. Cost if wrong: a later task needs a small fake signature edit, but Task 1 stays behavior-neutral. |
| 1 | 1 | Smoke RED vs project-reference ordering | Plan text adds the project reference before asking for a missing-wiring RED, while `MauiProgram` is already public. Ruling: scaffold and write the smoke test before adding the App reference/solution wiring, observe compile RED, then add wiring for GREEN. Cost if wrong: the RED proves only missing integration wiring, which is exactly Task 1's boundary. |
| 1 | 1 | Template package versions vs offline execution | Installed template requests coverlet 6.0.4, Test SDK 17.14.1, xunit 2.9.3 and VS runner 3.1.4, none cached. Ruling: before any restore, replace them with the complete cached set named above and remove the template `<Using Include="Xunit" />` in favor of the planned `GlobalUsings.cs`. Cost if wrong: features from newer test-runner packages are unavailable, but Task 1 requires only ordinary xUnit discovery and the selected cached runner supports it. |
| 2 | 6 / Shop screenshots | `IAppRunMode.IsLocalPreview`, `App`, `MainLayout` | Preview must suppress every named startup integration centrally and must be impossible to activate in Release. Later screenshot capture depends on this task. |
| 3 | 4-5 | `IClientIdentityService`, legacy install ID and HWID adapter | Identity must move out of telemetry before consent can make telemetry dormant; legacy ID compatibility is preserved. |
| 3 | 2 / Shop screenshots | preview composition for identity, telemetry, and usage | Readiness found that `UsageChip` can resolve the production `IUsageGateService` during preview navigation, causing real identity acquisition and an admin-endpoint request. Ruling: Task 3 also owns inert preview identity, telemetry, and usage implementations plus last-registration-wins tests. Production mappings and behavior remain unchanged. App launch stays forbidden. |
| 3 | 3 | `FeedbackService` and startup concurrency | Feedback is a second direct install-ID reader omitted by the original file list, and telemetry/access are queued concurrently on first run. Ruling: migrate Feedback too and synchronize singleton identity creation so every first caller receives one record and at most one legacy preference write/hardware acquisition occurs. |
| 4 | 5-6 | `IPrivacyConsentService`, consent-linked cancellation | Precise location can never be enabled without telemetry; revocation immediately clears/cancels downstream work. |
| 5 | 6 / backend | `TelemetryLocation` source/precision/accuracy/age and v3 payload | UI and edge ingest consume honest metadata; device code never fabricates edge coordinates or accuracy. |
| 6 | Shop Task 6 | local-preview safety and privacy UX/docs | App launch for real screenshots remains forbidden until full plan verification and independent review are clean. |

## Per-task self-consistency scan

| Task | Tests vs implementation | Files vs later use | Result |
|---|---|---|---|
| 1 | Smoke test validates actual App assembly load; fakes remain dependency-free until interfaces exist. | Correct foundation for every later task. | Clean with three rulings above. |
| 2 | Debug/Release flag and startup-call tests map to one central run-mode guard. | Direct prerequisite for safe screenshot launch. | Clean. |
| 3 | Legacy-ID and single-owner tests map to identity migration. | Must precede telemetry consent. | Clean. |
| 4 | Unknown/denied/granted/revocation tests map to a hard per-event transport boundary. | Supplies Task 5 and Task 6 consent state. | Clean. |
| 5 | No-OS-call, freshness, accuracy/source and minimized-payload tests map to provider/policy split. | Supplies accurate Settings disclosure and backend v3 contract. | Clean. |
| 6 | Copy-contract and UI tests map to separate toggles and honest disclosure. | Full-plan gate precedes App launch. | Clean. |

## Task status

- Task 1 complete: Windows xUnit harness, real-App smoke boundary, solution/friend-assembly wiring, and dependency-free fakes. Strict missing-reference RED was observed after local-only assets generation; GREEN was 13/13 tests plus a clean App Debug build. Independent review verdict: **Approved; no Critical, Important, or Minor findings**. No App launch, branch, commit, remote source, or remote action occurred.
- Task 2 in progress: Debug-only local preview. Read-only flow audit found that the original three-file plan boundary is too narrow for the binding privacy-safe-launch outcome:
  - `App` eagerly resolves all seven named integrations before a guard could run.
  - `MainLayout` eagerly resolves `ILicenseService`; its production constructor can schedule a network validation after two seconds from cached state even if the explicit layout validation call is skipped.
  - `MainLayout` page reflection initializes Discord IPC, and eagerly resolved HUD/session services instantiate local overlay/hotkey machinery.
  - `IArkLinkService` construction migrates preferences, and the Windows platform bootstrap resolves ARK Link and Discord again outside `App`.
  - `GlobalSearch` resolves `IPaletteCommandProvider`, whose production graph constructs every automation script; those constructors read preferences and register global hotkeys. The HUD page similarly resolves a production overlay graph that reads/writes AppData and enumerates scripts.
  - the default `/home` route performs username/Wi-Fi/WMI/drive/ARK-path/resource probes before a capture can navigate away, and can render private data.
  - `MainPage` maps production cache directories, while the Windows bootstrap reuses production mutex/show-event names.
  - shutdown paths can launch an installer staged by an earlier normal session.
  Ruling: Task 2 may minimally extend into `MainPage`, the Windows platform bootstrap, and preview-only composition-root service replacements, including palette/HUD/session replacements. Preview checks stay at composition/orchestration boundaries, not inside production feature services. Preview starts on a verified static non-Home route, uses isolated synchronization/profile/cache policy, and normal registrations, calls, order, and Discord behavior remain unchanged. Cost if wrong: a Debug-only preview could still expose private state, register hotkeys, contact a service, or mutate production state, so the task remains blocked from any App launch until focused Debug/Release tests, App builds, source scans, and independent review are clean.
- Task 2 review fix round 1/5 started: independent review found four Important fail-open paths despite green extracted-policy tests: real `IUpdateService` construction through layout/navbar; production diagnostics Preferences/file logging before the guard; isolated WebView profile preparation falling back to the default profile; and Windows integration wiring treating a missing DI run mode as normal even when the authoritative pre-mutex flag says Preview. All four are accepted and returned to the original implementer with new RED-test requirements. App launch remains forbidden.
- Task 2 review fix round 1/5 complete: all four Important findings addressed with new RED/GREEN tests; no open findings. Preview now uses an inert version-only `IUpdateService`, Debug-only/no-file/no-Preferences logging, fail-closed isolated WebView profile preparation, and the authoritative pre-mutex Preview flag even under null/contradictory DI.
- Task 2 complete: independent re-review verdict **CLEAN / APPROVED with no new Critical, Important, or Minor findings**. Fresh verification: focused Debug 32/32, focused Release 32/32, full Debug 45/45, Debug and Release App builds 0 warnings/errors, and `git diff --check` green. No App launch, restore, branch, commit, remote, installer, or release action occurred. The full privacy plan—not Task 2 alone—remains the gate before screenshot launch.
- Task 3 ready and next: centralize install ID and HWID behind one lazy, synchronized singleton identity service; migrate Telemetry, AccessGate, Feedback, and the existing HWID adapter; preserve exact legacy identity vectors and the `rr.telemetry.install_id` key; and close the preview navigation gap with inert identity/telemetry/usage mappings. Readiness was read-only and found no reason to launch the App. The binding implementation brief is `task-3-brief.md`.
- Task 3 review fix round 1/5: independent review found two Important boundaries despite the first green run—Telemetry's payload path still called static MAUI Preferences from its mandatory consumer test, and MachineGuid whitespace/empty values were normalized instead of hashed byte-for-byte like the legacy implementation. Both findings were reproduced with safe RED tests and corrected by injecting `IPreferencesStore` into Telemetry and preserving every non-null MachineGuid string verbatim; no App launch occurred.
- Task 3 complete: independent re-review verdict **CLEAN / APPROVED with no Critical, Important, or Minor findings**. One synchronized retryable singleton now owns the legacy install ID and HWID; Telemetry, AccessGate, Feedback, Hwid, and preview composition use the approved seams; preview identity/telemetry/usage are inert; direct test/Telemetry MAUI preference access is zero; MachineGuid null/throw and whitespace/empty vectors preserve exact legacy results. Fresh sequential verification: focused Debug 55/55, focused Release 55/55, full Debug 76/76, Debug and Release App builds each 0 warnings/0 errors, and `git diff --check` exit 0. Static gates show one install-ID owner, one OS hardware source, zero old algorithms/raw sinks/unauthorized consumer reads, and no Discord/license/quota/schema drift. No App launch, restore, branch, commit, merge, push, release, deployment, endpoint, or remote action occurred.
