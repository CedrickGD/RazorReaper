# Sky Changer — Left-Off / Resume Notes

_Branch: `feature/live-apply`._

## Current state (final)

**"Custom Lab" is now a single "Sky Changer" page** (route still `/custom-lab`, nav group
"Custom ARK"). It contains only the file-inject sky tool — Read Me, Settings, and the Live Apply
tab are gone.

### Sky = file-inject only (no DLL, no injection)
Patches the local `SimpleSky_*` `.uasset` textures on disk (`SkyInjectorService`). Safe on **any**
server — it only edits your own files, so anti-cheat has nothing to flag (BattlEye bans actual
cheat software, not cosmetic file edits — worst case a kick). Shows after the next **map load**
(rejoin/relaunch); won't show at night. Not instant — and instant is impossible rule-safely
(every instant method = DLL/memory injection, which BattlEye blocks). The live `d3d11` proxy was
removed for this reason.

### Removed this session
- Live `d3d11` sky proxy (`LiveSkyService`, native `rr_proxy/`).
- The **Live Apply / Memory Patcher** feature entirely — `MemoryPatcher.razor`,
  `MemoryPatcherService`, `GameInjector`, `ProcessMemoryService`, `MemoryModels`, native `rr_live/`,
  and the `MemoryInjectEnabled` setting. Same BattlEye dead-end as the sky DLL (injection → force-close).
- The Read Me + Settings Custom Lab tabs (the page no longer gates on accept/master-enable).

### Kept
- **INI Changer** (`/ini-changer`) — untouched, works as before.
- `ArkLauncher` + `GameConsoleService` — the **Game page** (`/game`) uses them for launch + console
  key; NOT part of the removed memory feature.

## INI changer live-switch — answered: NOT possible via the file
`BaseDeviceProfiles.ini` (the INI preset = UE4 `r.*`/`ShowFlag.*` graphics cvars) is read **once at
engine startup**. ARK never re-reads it mid-session, so editing it live does nothing; a different
preset only takes effect on the **next game launch**. (This is a UE engine limit, not an ARK quirk,
and it has nothing to do with in-game console commands.) Same "set → relaunch" model as the sky.
