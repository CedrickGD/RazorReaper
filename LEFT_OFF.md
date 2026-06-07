# Live Sky — Left-Off / Resume Notes

_Last updated: 2026-06-07. Branch: `feature/live-apply`._

## Goal
In RazorReaper, pick an image/solid color → **Inject sky** → the in-game ARK sky becomes it.
Two delivery lanes:
- **A — file-inject (reliable, shipping):** patch the sky `.uasset`s on disk by name; the game bakes it in on the next **day-cycle reload**. Works on covered maps. Not zero-wait.
- **B — live engine (instant, in progress):** a `d3d11.dll` proxy repaints the sky textures in VRAM with no reload. **Not working yet** — see the wall below.

Single-player / unofficial only. Engine needs a **No-BattlEye** launch.

---

## CURRENT STATUS (this session)

### ✅ Option A is wired + shipping
- `SkyInjector.razor` `InjectAsync` now runs `Injector.InjectAsync(_opts)` (the file patch) as the reliable path, plus a best-effort `LiveSky.ApplyAsync` (the live arm).
- Two user notices added (output log + success toast + coverage caveat): **"may take one in-game day cycle to appear (slomo to speed it up)"** and **"a custom sky normally won't show at night — that's normal."**
- Covered maps only (The Island, Ragnarok, Genesis, etc.). **Fjordur uses its own sky system → not file-patched.**

### ✅ Live engine builds, loads, hooks, and is STABLE
- `native/rr_proxy/d3d11_proxy.cpp`: hooks `CreateTexture2D` (device vtbl 5) for the create-time splice, and `Map` (immediate-ctx vtbl 14) as a **render-thread tick** for in-place re-skin. A **background poller thread** reads the control dir every 500 ms. Both splice paths are thread-safe (the old crash was re-skinning from the worker thread; now it's the Map/render thread).
- **Fixed this session:** `SlurpFile` didn't null-terminate its buffer → `atoi(gen.txt)` read garbage → the gen counter flapped (`gen=1↔10`, `9↔930`) and the engine reloaded constantly. Added `malloc(sz+1)` + `b[rd]=0`. Gen is now stable.
- Diagnostic build logs `BC3 sky <W>x<H> hash=<fnv1a64> match=<0/1> targets=<n>` for the first 50 sky-dim BC3 textures → `%TEMP%\rr_live.log`.

### ❌ THE WALL (don't re-litigate): content-hash matching from disk does NOT work
- Proven cold on a fresh Fjordur load with clean targets: **every sky texture logged `match=0`.**
- Why: the bytes ARK uploads to the GPU at runtime (`init[0]`) are **not** the bytes we hash off the `.uasset` (`DataOffset = end - W*H - 4`). Almost certainly a **mip / streaming difference** — the file-inject patches a region that's visible on the distant sky, but the proxy hashes the full-res base mip, which is different.
- Also: the sky cycles through **~15–20 different textures** (day-cycle keyframes), all different hashes. The disk fingerprint collapses to ~3. So even a "correct" disk hash can't cover the runtime set.
- ⇒ **Fingerprinting the disk is a dead end.** Stop trying to fix `DataOffset`.

---

## NEXT: Option B — "learn the sky from the file-inject" (the route to instant)

The file-inject already changes the sky precisely (by name). Use it as a **marker** to teach the live engine which runtime textures are the sky:

1. User injects image **X** → file-inject patches the sky on disk; C# also hands the proxy X's BC3 (it already writes `sky_<W>x<H>.bin`).
2. On (re)load, the sky textures in VRAM **are X**. In `Hook_CreateTexture2D`, compare the new texture's content to **X's blob** (try matching a mip that lines up — likely NOT `init[0]`; test `init[1]`/`init[2]`, or compare the whole supplied mip chain). The ones that match are the **sky** → `TrackSky` them (AddRef).
3. User injects image **Y** → bump `gen.txt`; the Map-hook re-skin rewrites the tracked textures to **Y** live → instant, no reload.

This sidesteps disk-hash matching entirely (we match runtime↔injected-image, not runtime↔disk-file). Open question to nail in-game: **which mip** the visible sky uses (so we compare/splice the right one).

Fallback if (2) is unreliable: capture the runtime sky hashes over a full day cycle (the diag already logs them) and isolate the sky set by **diffing before/after a file-inject** (the hashes that change are the sky).

---

## Architecture / file map

**Proxy** `native/rr_proxy/d3d11_proxy.cpp` (built via `build_d3d11.ps1` → `d3d11.dll`):
- Control dir `%LOCALAPPDATA%\RazorReaper\LiveSky\`: `enabled`, `gen.txt` (int, bumped per apply), `targets.txt` (`<fnv1a64-hex> <W> <H>` per line), `sky_<W>x<H>.bin` (user image as BC3 full mip chain).
- FNV-1a-64: offset basis `14695981039346656037`, prime `1099511628211`. Must stay byte-identical to C#.

**C#:**
- `ILiveSkyService` / `Implementations/CustomLab/LiveSkyService.cs` — writes the control dir; fingerprints `SimpleSky_*` (DXT5) base-mip → targets (**this disk-fingerprint is the part that doesn't match runtime — rework per Option B**), encodes the image to BC3 per dim. Registered in `MauiProgram.cs`.
- `ISkyInjectorService` / `SkyInjectorService.cs` + `UAssetTextureParser.cs` — the file-inject (by name) and `DiscoverSkyTexturesAsync`. `TryParseDxt5`: `dataSize=W*H`, `dataOffset=end-W*H-4`.
- `SkyInjector.razor` — `InjectAsync` = file-inject + live arm + notices. Restore disarms live + clears preview.
- `Game.razor` `LaunchGame` → `ArkLauncher.LaunchNoBattlEye()`.

**Helper:** `C:\Users\cedri\rr_arm.cs` — .NET 10 file-based app (`dotnet run rr_arm.cs`) that re-arms the control dir from `C:\Users\cedri\Pictures\rr_test_sky.png` (live-only fingerprint of generic `SimpleSky_*` + encode). Writes `gen=1` each run. Quick way to re-arm without the UI (WebView2 doesn't render into screenshots).

---

## How to resume / gotchas
1. Build proxy: `native/rr_proxy/build_d3d11.ps1`. Copy `d3d11.dll` → ARK `...\ShooterGame\Binaries\Win64\`; ensure `d3d11orig.dll` there (copy of `C:\Windows\System32\d3d11.dll`).
2. Build app: `dotnet build RazorReaper/RazorReaper.csproj -f net10.0-windows10.0.19041.0`.
3. **Launch ARK No-BattlEye = run `ShooterGame.exe` directly** (Steam must be running). Do **NOT** pass `-NoBattlEye` (not a real arg → ARK exits immediately). With the proxy installed, the normal Steam/BattlEye launch aborts — always launch the exe directly.
4. Watch `%TEMP%\rr_live.log` for `proxy loaded` / `CreateTexture2D hooked` / `Map hooked` / `armed:` / `BC3 sky ... match=`.
5. Clean game = delete `d3d11.dll` + `d3d11orig.dll` from Win64.
- ARK is **borderless**; foreground it with **AttachThreadInput** (PowerShell). Discord/desktop steal focus — re-foreground ARK, never minimize Discord (user watches a stream on another monitor).
- In-game console: **Tab** opens it. Run **`gcm`** (godmode) first so the char doesn't die during testing, then `slomo <n>` / `settimeofday <hh:mm>`.
