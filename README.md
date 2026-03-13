# RazorReaper

**RazorReaper** is a Windows desktop toolkit for **Steam ARK: Survival Evolved**, built with **Blazor Hybrid (.NET MAUI)**.

It is meant to keep the useful stuff in one place: game tweaks, config handling, utility tools, mod intel, and a cleaner workflow around ARK files and setup.

> Steam version only.  
> Microsoft Store / Windows Store builds, console versions, and other platforms are not supported.

---

## Overview

- Fast desktop UI for day-to-day ARK utility tasks
- Built around actual in-game workflows instead of generic launcher clutter
- Includes tools for configs, visuals, mods, prices, paintings, and quick helpers
- Ships through GitHub Releases with a direct **`.exe` installer**
- Made specifically for **Steam ARK: Survival Evolved**

---

## Features

### Core

- `Home` dashboard with system info, update checks, and game path handling
- `Server` tools for server-related workflow and Steam-connected helpers
- `Game` shortcuts for quick in-game style actions

### ARK Tweaks

- `INI Changer` for loading, editing, saving, importing, and exporting presets
- `Vision Tools` for suit/FOV style visibility helpers
- `Launch Options` management
- `Fonts` page with preset font switching
- `Pixel Glitch` texture utility tools
- `Paintings` helpers for `MyPaintings` workflow and preset handling

### Mods & Intel

- `Mutagen Prices`
- `OC BPs`
- `Bosses`
- `Map Mods`
- `Steam Mods`

### Utilities

- `Building` references and technique helpers
- `Auto Clicker`
- `Troubleshoot` and `Credits`

---

## Download

### Requirements

- Windows 10 or Windows 11
- **Steam** ARK: Survival Evolved

### Install

1. Open the [latest release](https://github.com/CedrickGD/RazorReaper/releases/latest)
2. Download the **`RazorReaper.exe`** installer from the release assets
3. Run the installer
4. Finish setup and launch RazorReaper

> No `.rar` package anymore.  
> The release download is the installer `.exe`.

### Updating

- RazorReaper can point you to newer releases
- Manual updates are simple: download the newest release installer and run it

---

## Build From Source

If you want to build it yourself:

### Requirements for source build

- Windows 10 or Windows 11
- .NET SDK `9.0.306` x64
- Visual Studio 2022 with .NET MAUI workload

Direct SDK download:  
[dotnet-sdk-9.0.306-win-x64.exe](https://builds.dotnet.microsoft.com/dotnet/Sdk/9.0.306/dotnet-sdk-9.0.306-win-x64.exe)

### Commands

```powershell
dotnet build RazorReaper.sln
dotnet run --project RazorReaper/RazorReaper.csproj -f net9.0-windows10.0.19041.0
```

---

## Notes

- RazorReaper is focused on **ARK: Survival Evolved**
- Some tools expect a valid ARK install path
- Steam edition only
