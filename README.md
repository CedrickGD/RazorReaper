# RazorReaper

**RazorReaper** is the modern successor to ReaperV2, rebuilt from the ground up using  
**Blazor Hybrid (.NET MAUI)**.

It is a fast, clean desktop toolkit for **ARK: Survival Evolved**, focused on real
in-game workflows, configuration management, and utility features.

---

## Overview

RazorReaper combines multiple ARK-related tools into a single, modern desktop application:

- Automatic ARK path detection
- Preset-based configuration editing
- Steam server interaction
- Visual and performance utilities
- Lightweight, consistent UI without external services

The project is actively maintained and replaces the legacy WinForms-based ReaperV2.

---

## Features

### Dashboard
- System uptime
- Time and session info
- Storage and resource overview
- Network status
- ARK installation paths

### Server Manager
- Query ARK servers
- Connect via Steam
- Save and manage favorites
- Open Steam server browser

### Game Management
- Launch and close ARK
- Quick in-game commands:
  - reconnect
  - disconnect
  - debugstructures

### Configuration Tools
- BaseDeviceProfiles.ini editor
- Preset library
- Load, save, reset configurations

### Visual & Utility Tools
- Suit FOV camera trace toggle (auto file detection)
- Game font switching with auto-install
- Pixel texture removal and restore by category
- MyPaintings management
- Mutagen price lookup (Genesis Part 2)
- Building technique reference videos

### Automation
- Advanced auto clicker
- Custom timing and randomization
- Burst mode
- Click heatmap visualization

### Notifications
- Toast notifications
- Sound support
- Activity log

---

## ReaperV2 vs RazorReaper

| Feature                    | ReaperV2 (WinForms) | RazorReaper (Blazor Hybrid) |
|----------------------------|---------------------|-----------------------------|
| UI / UX                    | Basic               | Modern and consistent       |
| Platform                   | Windows only        | Windows (MAUI-based)        |
| Path detection             | Manual              | Automatic with override     |
| Config presets             | Limited              | Preset library              |
| Server tools               | Basic                | Steam integration           |
| Notifications & activity   | No                   | Yes                         |
| Development status         | Archived             | Active                      |

---

## Requirements

- Windows 10 / 11
- .NET SDK 9.0.306 (Windows x64)
- Steam with ARK: Survival Evolved installed

.NET SDK download:  
https://builds.dotnet.microsoft.com/dotnet/Sdk/9.0.306/dotnet-sdk-9.0.306-win-x64.exe

---

## Download

Latest release:  
https://github.com/CedrickGD/RazorReaper/releases/latest

Steps:
1. Download the ZIP archive
2. Extract it
3. Run `RazorReaper.exe`

---

## Build From Source

```powershell
dotnet build RazorReaper.sln
dotnet run --project RazorReaper/RazorReaper.csproj -f net9.0-windows10.0.19041.0
