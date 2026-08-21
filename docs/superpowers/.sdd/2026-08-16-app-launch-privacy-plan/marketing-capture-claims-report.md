# Marketing capture claim fix: review handoff

## Scope and root cause

The inert Preview license intentionally reports the free tier, while `SharedNavbar` rendered its tier footer without consulting the marketing-preview policy. The real Razor boundary now omits the complete sidebar status footer only for `--local-preview`; normal-mode markup, label, tooltip, dot class, and version remain unchanged.

The visible Scripts subtitle no longer claims `BattlEye-safe` and still ends with `External input only.` No source-text test was added for this human copy.

## Files

- `RazorReaper/Services/Implementations/LocalPreviewMarketingPolicy.cs` — adds the pure `ShouldShowSidebarStatus` preview boundary.
- `RazorReaper/Components/Shared/SharedNavbar.razor` — applies that boundary to the actual sidebar status render block.
- `tests/RazorReaper.UnitTests/LocalPreviewMarketingCaptureTests.cs` — covers Preview hidden and Normal visible with literal expectations.
- `RazorReaper/Components/Pages/Scripts.razor` — removes only the unsupported visible claim.

## TDD and verification

- RED: focused Debug compile exited 1 with expected `CS0117` because `ShouldShowSidebarStatus` did not yet exist.
- GREEN: focused Debug marketing tests: 10 passed, 0 failed.
- Full Debug unit suite: 86 passed, 0 failed.
- Debug App build: succeeded, 0 warnings, 0 errors.
- Release App build: succeeded, 0 warnings, 0 errors.
- Static visible-copy gate: `BattlEye-safe` matches under `RazorReaper/Components`: 0; `External input only.` matches in `Scripts.razor`: 1.
- Static boundary gate: one policy call in `SharedNavbar.razor` and one implementation in `LocalPreviewMarketingPolicy.cs`.
- `git diff --check`: exit 0; only existing line-ending notices were printed.

The App was not launched. No restore, branch, commit, merge, push, PR, installer, release, publish, deployment, upload, endpoint, browser, or other remote action was performed. HEAD remained detached at `17d9be955dfa6c07bc5da252a4496a08dd335201`; `progress.md` was not modified.
