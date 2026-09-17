# Release process

How to cut a RazorReaper release, and what fires automatically once you do. See the
[README's "Releases & updates"](../README.md#releases--updates) section for the shorter,
user-facing version of the update path this feeds.

**A release is the owner's button.** A GitHub runner builds the installer and parks it on a
*draft* release; the only thing that turns that draft into a release customers are offered is a
deliberate, confirmed click in the admin panel. No schedule, no webhook, no "publish on green
build", and nothing in CI publishes. The same is true of a rollback.

## 1. Bump the version — six fields, one number

All of these move together to the same version, or `ReleaseReadinessTests` fails:

| File | Field(s) |
| --- | --- |
| `RazorReaper/RazorReaper.csproj` | `ApplicationDisplayVersion`, `ApplicationVersion` (build counter), `AssemblyVersion`, `FileVersion`, `Version` |
| `installer/RazorReaper.iss` | `MyAppVersion` |

The panel makes this bump: one commit on `master` before it dispatches the build. CI bumps
nothing — `build-installer.yml`'s first step only *verifies* that the commit it checked out
already carries the version the run was dispatched with, in both files, and fails the run
otherwise. Editing the six fields by hand and pushing works exactly as well; the guard does not
care who bumped.

`ReleaseReadinessTests.CompiledVersionMetadataMatchesTheProject` also pins that the compiled
assembly's version metadata, `AppVersionInfo`, and the `RazorReaper/<version>` user-agent string
all derive from `ApplicationDisplayVersion`/`ApplicationVersion` and nothing else (it fails if
runtime code reads `AppInfo.Current.Version` instead — see
`RuntimeCodeDoesNotReadVersionFromMauiAppInfo`).

Bumping the version alone does **not** change what any client is offered — see step 3.

## 2. Build the installer on a runner

`.github/workflows/build-installer.yml`, `workflow_dispatch`, inputs `version`, `notes` and
`prerelease`. The panel's **Build installer** button dispatches it; the Actions tab does the same
by hand. On `windows-latest` the run:

1. verifies the committed version against the `version` input (step 1),
2. installs the SDK `global.json` pins and the `maui-windows` workload,
3. `dotnet build RazorReaper/RazorReaper.csproj -c Release` (self-contained `win-x64`, per the
   `csproj`),
4. signs `RazorReaper.exe` — optional, see below,
5. `choco install innosetup` and `ISCC /O<workspace>\artifacts installer\RazorReaper.iss`, then
   signs `RazorReaper-Setup.exe` the same optional way. `/O` overrides the script's `OutputDir`,
   so the `.iss` needs no CI-only edit and a local build still lands on the Desktop,
6. uploads the installer as a workflow artifact, then creates the **draft** release for
   `v{version}` if it is missing and uploads `RazorReaper-Setup.exe` to it with `--clobber`.

`--draft` is load-bearing: this workflow never publishes, so it never fires the `release` event
and never posts to Discord. Re-dispatching the same version replaces the asset, and the
`concurrency` group stops two runs for one version racing over it. No local MAUI workload, no
local Inno Setup, no Desktop folder.

**A CI build ships without ffmpeg.** `RazorReaper/Tools/ffmpeg.exe` is gitignored (97 MB) and the
`csproj` includes it only `Condition="Exists(...)"`, so a runner-built installer does not carry
it and `FfmpegProvider` downloads it at first use. The notes of the first CI-built version have
to say so ("the first conversion downloads ffmpeg").

**Signing is optional, and never silent.** The step runs only when the repository secrets
`RR_SIGN_PFX_BASE64` (the pfx, base64) and `RR_SIGN_PFX_PASSWORD` both exist — only the owner
creates those, the pfx itself stays in the keys repo (`Self-Sign/`, driven by `rr_sign.bat`) and
is copied nowhere. The workflow decodes it into the runner's temp directory, imports the public
certificate into `Root` and `TrustedPublisher` so `signtool verify` can pass, signs with the same
parameters `rr_sign.bat` uses (with a no-timestamp fallback), and the runner is destroyed. With
the secrets absent the build is unsigned and says so in a `::warning::` and the run summary; a
failed *signature* fails the job, a failed *verify* only warns.

## 3. Publish — the owner's button

Publishing is a click in the panel (`#/releases`), not an action on github.com and not something
CI does. It is one confirmed operation that writes, in order: the release title and body, then
`draft: false` on the GitHub release — refused unless it carries `RazorReaper-Setup.exe`, because
publishing without an installer strands every client — then `update.xml` on `master`. A
prerelease stops before `update.xml`: no customer is ever offered one.

Rolling back is the same kind of governed click. **Make current** repoints `update.xml` at an
older published tag; it deletes nothing, unpublishes nothing and fires no `release` event. It is
the only supported rollback — hand-editing `update.xml` is not.

RR-Admin-Panel's `docs/release-management-design.md` §6 has the step list and what each refusal
means.

## 4. What fires automatically

- **`discord-release.yml`** (`.github/workflows/discord-release.yml`) posts on
  `release: published` / `prereleased`. The link in that post is
  `https://dl.razorreaper.app/release-notes/<tag>` — public, served by rr-api — and not the
  github.com release page, which 404s for anyone without repo access once the repo is private.
  Pushes to `master`/`main` still go to the commits channel, unchanged. Because publishing is
  the owner's button, this post is never a surprise: the panel's confirm modal says it will
  happen before the click that causes it.
- **`update-manifest.yml`** does **not** run on a release any more. The panel writes `update.xml`
  itself, and leaving the trigger in would put two writers on `master` racing over the same file
  — and would silently undo a **Make current** rollback the next time anything was published. It
  survives as a manual `workflow_dispatch(tag_name)` fallback for a day the panel or the NAS is
  down, and is not part of the normal path.
  `ReleaseReadinessTests.InstallerMatchesTheBuildAndPublishedManifestIsConsistent` is still the
  guard that keeps `update.xml`'s version from ever exceeding what is actually installable.
- Nothing else. `update.xml` changes through publish, through **Make current**, or through that
  manual fallback — and through nothing else.

A version bump merged to `master` without a published GitHub Release leaves `update.xml`, and
therefore the update offered to every client, on the previous release.

## 5. How the update actually reaches installed clients

- The client (`UpdateService.cs`) reads the manifest from the Cloudflare Worker at
  `https://backend.rr-admin-panel.workers.dev/update/update.xml` (see RR-Admin-Panel's
  `backend-worker`, also deployed as `rr-api` on the NAS for `dl.razorreaper.app` /
  `api.razorreaper.app`). The worker calls `api.github.com` with a server-side token, rewrites
  only `<url>` to its own `/update/download` redirect (which 302s to GitHub's signed asset URL of
  the **latest** GitHub release, not necessarily the version `update.xml` currently names), and
  leaves `<changelog>` pointing straight at `github.com`. A draft release is invisible to both —
  GitHub's "latest" skips drafts — so the installer CI parks on a draft reaches nobody until the
  release is published.
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
2. **The changelog link must stop reaching the customer as a `github.com` URL.** The committed
   `<changelog>` stays a `github.com/.../releases/tag/...` URL on purpose —
   `ReleaseReadinessTests` asserts that shape against the file, so committing a NAS URL would
   fail the build. The fix is a rewrite on the way out, not in the file: rr-api serves
   `<changelog>` as `https://dl.razorreaper.app/release-notes/<tag>`, the same public page the
   Discord post now links (RR-Admin-Panel `docs/release-management-design.md` §8). Until that
   ships and `dl.razorreaper.app/release-notes/v1.5.3` answers, a signed-out visitor following
   the link from the in-app "What's new" / `WhatsNewOverlay.razor` gets a 404 on a private repo.
   The Discord half of this is done; the manifest half is the worker's.
3. **Stock on old client versions.** Adoption in the 30 days up to 2026-09-17 (829 distinct
   installs): 1.5.2 = 397, 1.4.8 = 253, 1.4.9 = 110, 1.4.10 = 41, 1.5.0 = 9, older = 7,
   legacy = 12. Versions **1.4.8 and older (272 installs, ~33% of the 30-day base) essentially
   never self-update** — see the "Why 1.4.8 never updates" note below — so they will keep
   calling whatever manifest URL is reachable indefinitely. Making the repo private without the
   token wired up strands those installs permanently (no update check ever succeeds again).
4. **The panel's own Versions page reads GitHub unauthenticated.** `src/hooks/useReleaseVersions.ts`
   and `useLatestVersion.ts` in RR-Admin-Panel call the GitHub API from the browser, with no
   token, and fall back to a hard-coded `"1.4.2"` on failure. A private repo will make that page
   fall back to the stale hard-coded version for every visitor until both hooks read
   `GET /api/admin/releases/versions` from the panel's own server instead (RR-Admin-Panel
   `docs/release-management-design.md` §10). That is the last GitHub call left in the browser.

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
