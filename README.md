# RazorReaper

RazorReaper is the modern successor to [ReaperV2](https://github.com/CedrickGD/ReaperV2), built with Blazor Hybrid (.NET MAUI). It focuses on ARK: Survival Evolved configuration, utilities, and quick actions in a clean, consistent desktop UI.

## Features

- Home dashboard with time, uptime, network, storage, hardware, resources, recent activity, and ARK paths
- Server management: query servers, connect via Steam, save servers, add to Steam favorites, open Steam server browser
- Game management: launch/close ARK and run quick in-game commands (reconnect, disconnect, debugstructures)
- INI configuration: BaseDeviceProfiles editor with a preset library, load/save/reset tools
- Suit FOV: toggle camera trace settings with automatic file detection
- Game fonts: switch fonts, auto-install options, and quick access to Steam
- Pixel textures: delete or restore texture files by category
- Paintings: manage the MyPaintings folder and access canvas tools/resources
- Mutagen prices: searchable Gen2 dino list
- Building techniques: tutorial videos for foundation raising/lowering
- Auto clicker: advanced timing, randomization/burst options, and a click heatmap
- Notification system with sound and activity feed

## Tech Stack

- Blazor Hybrid (.NET MAUI)
- .NET 9 on Windows

## Download and Run

Requirements:
- Windows 10/11
- .NET SDK 9.0.306
- Steam and ARK: Survival Evolved (for game-dependent features)

Download the latest release:
https://github.com/CedrickGD/RazorReaper/releases/latest

1. Download the zip from the release page
2. Extract it
3. Run `RazorReaper.exe`

## Build From Source

```powershell
dotnet build RazorReaper.sln
dotnet run --project RazorReaper/RazorReaper.csproj -f net9.0-windows10.0.19041.0
```

## ReaperV2 vs RazorReaper

| Feature                 | ReaperV2 (WinForms) | RazorReaper (Blazor Hybrid) |
|------------------------|---------------------|------------------------------|
| UI/UX                  | Basic               | Modern, consistent UI        |
| Platform               | Windows only        | Windows (MAUI base)          |
| Path detection         | Manual setup        | Auto-detect + override       |
| Config presets         | Limited             | Preset library               |
| Server tools           | Basic               | Query + Steam integration    |
| Notifications/activity | No                  | Yes                          |
| Ongoing updates        | Archived            | Active                       |

## License

This project is licensed under a proprietary license.
