# Sky / Custom Lab — Left-Off / Resume Notes

_Branch: `feature/live-apply`._

## DECISION (final): sky = file-inject only. The live DLL is dropped.

The **Sky Injector** changes the in-game sky by patching the local `SimpleSky_*` `.uasset`
textures on disk (`SkyInjectorService`). This is the **only** path now and it:

- works on **any server** (single-player, No-BattlEye, AND BattlEye servers) — it only edits
  *your own local files*, so there is nothing for anti-cheat to flag (BattlEye bans for actual
  cheat software, not cosmetic file edits — worst case is a kick, no ban);
- shows after the next **map load** — rejoin the server or relaunch (or wait for the in-game
  day to roll over). **Not instant.** It also won't show at night — that's normal.

### Why the live (instant) DLL engine was removed
The instant in-VRAM swap required an unsigned `d3d11.dll` proxy. **BattlEye force-closes ARK on
any join while that proxy is present** — joining a BattlEye server silently relaunches the client
as `ShooterGame_BE.exe`, which trips on the proxy. Confirmed in testing. There is **no rule-safe
way** to swap the sky instantly mid-session on a BattlEye server: every instant method
(DLL injection, memory editing, ReShade — which ARK has blacklisted) is exactly what anti-cheat
blocks. So the proxy only ever worked single-player / No-BattlEye, was pure liability online, and
is now gone.

**Removed this session:** `ILiveSkyService`/`LiveSkyService`, the Live Sky toggle + `LiveSkyEnabled`
setting, the `App.xaml.cs` proxy reconcile, the csproj `d3d11.dll` bundle, and `native/rr_proxy/`.
The file-inject (`SkyInjectorService`, `UAssetTextureParser`, Restore/backup) is untouched.

## OPEN — next task: BaseDeviceProfiles.ini live-apply
Investigate whether an **INI preset switch** (the `IniChanger` — it writes ARK's
`BaseDeviceProfiles.ini`, a set of UE4 `r.*` / `ShowFlag.*` graphics cvars) can be made to apply to
a **running** session **via the file**, with no relaunch. This is strictly about the `.ini` file —
NOT in-game console commands. Open question: does ARK re-read the device-profile file at runtime, or
only at engine startup?

## File map
- `Services/Implementations/CustomLab/SkyInjectorService.cs` (+ `.Images`/`UAssetTextureParser`) —
  the file-inject (patch by name, backup, restore). `TryParseDxt5`: `dataSize=W*H`, `dataOffset=end-W*H-4`.
- `Components/Pages/CustomLab/SkyInjector.razor` — UI; `InjectAsync` = file patch only.
- `IniChanger.razor` + `IniPresetService` + `Resources/Presets/*.ini` — the INI preset feature.
- Memory patcher (`MemoryPatcherService`, `GameInjector`, `native/rr_live/`) — a SEPARATE Custom Lab
  feature; not touched by the sky work.
