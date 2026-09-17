# Release process

How to cut a RazorReaper release, and what fires automatically once you do. See the
[README's "Releases & updates"](../README.md#releases--updates) section for the shorter,
user-facing version of the update path this feeds.

## 1. Build

- Build `RazorReaper.sln` in `Release` configuration (self-contained, `win-x64`, targeting
  `net10.0-windows10.0.19041.0` per `RazorReaper.csproj`).
- Run the test suite (`tests/RazorReaper.UnitTests`), including `ReleaseReadinessTests` — see
  step 2, it fails the build if the version fields below have drifted apart.

## 2. Bump the version — six fields, one number

All of these move together to the same version, or `ReleaseReadinessTests` fails:

| File | Field(s) |
| --- | --- |
| `RazorReaper/RazorReaper.csproj` | `ApplicationDisplayVersion`, `ApplicationVersion` (build counter), `AssemblyVersion`, `FileVersion`, `Version` |
| `installer/RazorReaper.iss` | `MyAppVersion` |

`ReleaseReadinessTests.CompiledVersionMetadataMatchesTheProject` also pins that the compiled
assembly's version metadata, `AppVersionInfo`, and the `RazorReaper/<version>` user-agent string
all derive from `ApplicationDisplayVersion`/`ApplicationVersion` and nothing else (it fails if
runtime code reads `AppInfo.Current.Version` instead — see
`RuntimeCodeDoesNotReadVersionFromMauiAppInfo`).

Bumping the version alone does **not** change what any client is offered — see step 5.

## 3. Package the installer

Build `installer/RazorReaper.iss` with Inno Setup to produce `RazorReaper-Setup.exe`
(currently ~73 MB, self-contained — the .NET runtime is bundled). It installs to `{autopf}`
(Program Files) and, because `PrivilegesRequiredOverridesAllowed=dialog` with Inno's own default
`PrivilegesRequired=admin`, prompts for UAC elevation unless launched with `/VERYSILENT` (which
suppresses the prompt UI but the elevation itself is still required).

## 4. Tag and publish the GitHub Release

- Tag `vX.Y.Z` matching the bumped version.
- Attach `RazorReaper-Setup.exe` as a release asset.
- Write release notes as a `-`/`*` bullet list in the release body if you want them to become
  the in-app "What's new" notes (see step 5) — anything else in the body is ignored by that sync.
- Publish the release (not a draft) — `update-manifest.yml` listens for the `released` event.

## 5. What fires automatically

- **`update-manifest.yml`** (`.github/workflows/update-manifest.yml`) runs on `release: released`
  (or manually via `workflow_dispatch` with a `tag_name` input) and patches `update.xml` on
  `master`:
  - `<version>` → `X.Y.Z.0`
  - `<url>` → `.../releases/download/vX.Y.Z/RazorReaper-Setup.exe`
  - `<changelog>` → `.../releases/tag/vX.Y.Z`
  - `<notes>` → replaced only if the release body has `-`/`*` bullet lines; otherwise the
    existing notes are left untouched.
  - `ReleaseReadinessTests.InstallerMatchesTheBuildAndPublishedManifestIsConsistent` is the guard
    that keeps this file's published version from ever exceeding what's actually installable.
- **`discord-release.yml`** (`.github/workflows/discord-release.yml`) posts the release (and,
  separately, ordinary pushes to `master`/`main`) to the project's Discord webhooks.

`update.xml` only changes through this workflow. A version bump merged to `master` without a
matching published GitHub Release leaves `update.xml`, and therefore the update offered to every
client, on the previous release.

## 6. How the update actually reaches installed clients

- The client (`UpdateService.cs`) reads the manifest from the Cloudflare Worker at
  `https://backend.rr-admin-panel.workers.dev/update/update.xml` (see RR-Admin-Panel's
  `backend-worker`, also deployed as `rr-api` on the NAS for `dl.razorreaper.app` /
  `api.razorreaper.app`). The worker calls `api.github.com` with a server-side token, rewrites
  only `<url>` to its own `/update/download` redirect (which 302s to GitHub's signed asset URL of
  the **latest** GitHub release, not necessarily the version `update.xml` currently names), and
  leaves `<changelog>` pointing straight at `github.com`.
- If the worker call fails, the client falls back to `raw.githubusercontent.com` directly
  (`UpdateService.cs`'s `FallbackManifestUrl`) — **this only works while the repository is
  public**.
- The "free installer" download path (`/update/download/free`) serves the exact same
  `RazorReaper-Setup.exe`; it only increments a separate telemetry counter.

## Prerequisites before making the repo private

These are launch-blockers, not nice-to-haves — flipping the repo private without them breaks
updates for the majority of the current install base:

1. **`GITHUB_TOKEN` must be set in both places**, not just one:
   - as a `wrangler secret` on the Cloudflare Worker (**not yet confirmed set**, as of the
     2026-09-17 audit), and
   - in the NAS's `rr-api.env` (confirmed set).

   Only the worker path can serve a private repo; the `raw.githubusercontent.com` fallback
   cannot authenticate and will simply fail once the repo goes private.
2. **The changelog link keeps pointing at `github.com`.** `update-manifest.yml` writes
   `<changelog>` as a plain `github.com/.../releases/tag/...` URL and the worker does not rewrite
   it (only `<url>` is rewritten). On a private repo, a signed-out visitor following that link
   from the in-app "What's new" / `WhatsNewOverlay.razor` gets a 404, unless they're an
   authenticated collaborator or the panel starts proxying that link too. This is currently
   unresolved.
3. **Stock on old client versions.** Adoption in the 30 days up to 2026-09-17 (829 distinct
   installs): 1.5.2 = 397, 1.4.8 = 253, 1.4.9 = 110, 1.4.10 = 41, 1.5.0 = 9, older = 7,
   legacy = 12. Versions **1.4.8 and older (272 installs, ~33% of the 30-day base) essentially
   never self-update** — see the "Why 1.4.8 never updates" note below — so they will keep
   calling whatever manifest URL is reachable indefinitely. Making the repo private without the
   token wired up strands those installs permanently (no update check ever succeeds again).
4. **The panel's own Versions page reads GitHub unauthenticated.** `src/hooks/useReleaseVersions.ts`
   and `useLatestVersion.ts` in RR-Admin-Panel call the GitHub API from the browser, with no
   token, and fall back to a hard-coded `"1.4.2"` on failure. A private repo will make that page
   fall back to the stale hard-coded version for every visitor until it's given its own
   authenticated path (via the worker, presumably) — tracked separately as the panel's planned
   release-management page.

### Why 1.4.8 (and similar) never updates on its own

1.4.8 stages the installer and only runs it from `window.Destroying` / `ProcessExit`. Its `X`
button hides the window to the tray instead of closing it
(`Platforms/Windows/App.xaml.cs: e.Cancel = true; Hide()`), so those handlers practically never
fire, and `AutoUpdateManager`'s `CleanupStaleInstaller` deletes the staged installer
unconditionally on the next launch — producing a download loop that never installs. UAC prompts
on the elevated installer are a secondary cause. The forced-restart flow landed in commit
`1eee8f5` for 1.4.9+, and does work — 13, 10 and 15 installs are respectively stuck on
1.4.9/1.4.10/1.5.0, i.e. current-generation clients that have successfully updated at least once
and are just waiting on the next release.

## What the hybrid update round changed

Everything this section used to list as "not yet true" now ships, and the README's **Updating**
section describes it as it is:

- The silent download no longer force-installs. A finished download is *staged*: the bell keeps
  its dot, the What's new view and the tray both offer **Restart & update**, and the next start
  applies what is waiting. When a staged installer may run is `UpdateApplyPolicy`, which has no
  disk, clock or MAUI of its own and is unit-tested as a table.
- `<mandatory>` is acted on. Such a release applies without waiting for the button, as soon as
  the gate below is clear, and keeps retrying once a minute until it is.
- That gate exists (`IUpdateActivityGate`): ARK, a macro runner, the Auto Clicker, an automation
  script or a synthesized-input session each hold every trigger back, mandatory included.
- `update_download`, `update_install` and `update_applied` are emitted *and* on the telemetry
  allowlist. A hand-off whose installer returns a non-zero exit code is reported at the next
  start — to the user (a warning, plus an "Update failed" state in the What's new view) and to
  the panel, with the exit code. It is retried only if the user asks; a second failure on the
  same version discards the installer.

## Not yet true (do not imply otherwise)

- There is no setting to opt out of the background download, to defer the check, or to pin a
  version. What the user chooses is *when to restart*, not *whether to fetch*.
- The stranded 1.4.8-and-older installs above are not reachable by any of this: they never get
  far enough to run a new client.
