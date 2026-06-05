# Live Apply — Left-Off / Resume Notes

_Last updated: 2026-06-06. Branch: `feature/live-apply`._

## The goal (3 live-apply targets, no game restart, triggered from RazorReaper)

1. **Sky** → live texture **replace** (user's image) — Sky Injector, applied live.
2. **Pixel glitch** → live texture **delete/break** — different from sky (it deletes texture files).
3. **INI (BaseDeviceProfiles)** → live device-profile **setting swap**, in-memory (console can NOT carry a whole INI — only a small whitelist of words).

Intended for **unofficial / single-player only**. Launch must use **"Play ARK: No BattlEye"**.

---

## TL;DR status

- ✅ **We CAN change textures live in a running ARK world** — proven (the whole map turned red via the GPU hook). The hardest conceptual barrier is cleared.
- ⏳ **Sky/Pixel not yet clean/targeted** — the broad hook corrupts everything; the *sky's specific* texture-upload path still needs pinning so we hit only it and feed the real image.
- ❌ **INI live not started** — needs in-game CVar-system RE (same depth as the sky work).
- ✅ **App UX cleaned up** (see below). World is back to **normal** by default (`g_redden=false`).

---

## What works / the winning approach

**Proxy DLL (NOT injection).** Runtime DLL injection (`CreateRemoteThread`) is blocked by Windows Defender on this machine — verified 4 ways. Instead we ship our DLL into ARK's `Win64` folder so the **game loads it itself** (ReShade/ENB style) — no injection, nothing for AV to flag. **Proven working.**

- `native/rr_proxy/dxgi.dll` → proxy for `dxgi.dll`, forwards all exports to `dxgiorig.dll`. Runs a **named-pipe RE tool** (`\\.\pipe\rr_live`): `modinfo`, `ascan`/`wscan`/`afind`/`wfind <text>`, `read <hexaddr> <len>`. Found GNames (`ByteProperty` pool) this way.
- `native/rr_proxy/d3d11.dll` → proxy for `d3d11.dll`. Hooks `ID3D11Device::CreateTexture2D` (vtbl 5) + context `Map`(14)/`Unmap`(15)/`CopySubresourceRegion`(46)/`CopyResource`(47)/`UpdateSubresource`(48). Replaces texture pixels with red (proof). `g_redden` master switch (currently **false** = normal world).

Build: `native/rr_proxy/build_dxgi.ps1`, `build_d3d11.ps1` (auto-generate export forwarders via dumpbin → `exports_gen*.h`, compile with VS BuildTools).

**Install (into ARK Win64):** copy `dxgi.dll`+`dxgiorig.dll` (copy of `C:\Windows\System32\dxgi.dll`) and `d3d11.dll`+`d3d11orig.dll`.
**Uninstall = delete those 4 files** → game falls back to system DLLs.

In-game DLL log: `%TEMP%\rr_live.log`.

---

## Key technical findings (hard-won, don't re-litigate)

- **External RPM/WPM can't change textures** — pixels live in VRAM, the CPU copy is freed after GPU upload. `MemoryPatcherService` (external scan/write) is a proven dead end for textures. Kept but unused by the new UI.
- **Runtime injection blocked by Defender** — `VirtualAllocEx`/`CreateRemoteThread` → ACCESS_DENIED. `GameInjector` kept but unusable here. Proxy-DLL is the answer.
- **ARK textures stream in EMPTY** (`init=0`, often TYPELESS formats: BC3=76, BC1=70, BGRA=90). Pixels arrive *after* creation — NOT via `CreateTexture2D` init data, NOT immediate-context `Map`/`UpdateSubresource`. Likely a **staging→Copy** path or a **deferred context**. `CopyResource`/`CopySubresourceRegion` + verbose `Map` diagnostics were added but **not yet captured in-world** (next step).
- **The sky is several textures** (horizon ring + top + patches), some likely an **HDR cubemap** (`fmt=10 256x256 arr=6 misc=0x4`). The broad red hook missed the sky (different format/path) → it stayed normal while everything else corrupted.
- Sky Injector only "covers" ~50% of maps (file-based, hand-grabbed). A live GPU swap would work on ANY map → strictly better.

---

## Next steps (ordered)

1. **Pin the sky's upload path** — load into a world with the current diagnostic d3d11 build; read `%TEMP%\rr_live.log` for `MAP`/`COPYRES`/`COPYSUB` lines to see how the sky's pixels arrive. Hook that path.
2. **Target only the sky** (by the size/format found) and **feed the user's BC3 image** instead of red. Sky Injector already encodes BC3 per dimension — hand it to the hook over the pipe (or a file).
3. **Pixel-glitch live** — same hook, write garbage/break instead of an image.
4. **INI live** — in-game DLL finds `IConsoleManager` (AOB/RE) and sets each BaseDeviceProfiles CVar directly (bypasses console whitelist). Big RE task.
5. **Integrate into app** (task #10) — install/uninstall proxy from RazorReaper; send "swap sky / break pixel / set cvar" commands over the pipe.

---

## File map

**Native:** `native/rr_proxy/` — `proxy.cpp` (dxgi RE tool), `d3d11_proxy.cpp` (texture hook), `build_dxgi.ps1`, `build_d3d11.ps1`. (`native/rr_live/` = old injection DLL, superseded.)

**C# services (new):**
- `IProcessMemoryService` + `Implementations/Memory/ProcessMemoryService.{cs,Imports.cs,Scan.cs}` — external RPM/WPM + scan (dead-end for textures).
- `IGameConsoleService` + `Implementations/Game/GameConsoleService.cs` — console injection, extracted from `Game.razor`.
- `IMemoryPatcherService` + `Implementations/MemoryPatcherService.cs` — external memory orchestration (dead-end).
- `IGameInjector` + `Implementations/Memory/GameInjector.cs` — LoadLibrary injector (Defender-blocked).
- `IArkLauncher` + `Implementations/ArkLauncher.cs` — No-BattlEye launch, strips `culture=*`, BattlEye detection (gates Live Apply).
- `Models/MemoryModels.cs`, `Models/ConsoleBatchResult.cs`.

**App UX done:** `MemoryPatcher.razor` rewritten (Live Apply toggle+settings on top, launch gated on toggle, dead buttons removed); `CustomLab.razor` (tab decoupled from the toggle); `SkyInjector.razor` (image thumbnail + persists across restart); `Game.razor` (launch → No-BattlEye, console extracted); `MauiProgram.cs` DI; csproj ships `rr_live.dll`.

---

## How to resume quickly

1. `git checkout feature/live-apply`
2. Rebuild proxies: `native/rr_proxy/build_dxgi.ps1` + `build_d3d11.ps1`; copy `dxgi.dll`/`dxgiorig.dll`/`d3d11.dll`/`d3d11orig.dll` into ARK `...\ShooterGame\Binaries\Win64\`.
3. Build app: `dotnet build RazorReaper/RazorReaper.csproj -f net10.0-windows10.0.19041.0`.
4. Launch ARK (No-BattlEye / `ShooterGame.exe` directly), watch `%TEMP%\rr_live.log`.
5. To restore a clean game: delete the 4 proxy DLLs from Win64.
