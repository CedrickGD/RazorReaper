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

- `INI Changer` 3-column workspace with a built-in preset gallery, plus loading, editing, saving, importing, and exporting
- `Vision Tools` for suit/FOV style visibility helpers
- `Launch Options` management
- `Fonts` page with preset font switching
- `Pixel Glitch` texture utility tools with backup + revert support
- `Paintings` helpers for `MyPaintings` workflow and preset handling

### Mods & Intel

- `Mutagen Prices`
- `OC BPs`
- `Bosses`
- `Map Mods`
- `Steam Mods`

### Utilities

- `Building` references with build guides and a fullscreen image lightbox
- `Auto Clicker`
- `Troubleshoot` and `Credits`

### UI

- Resizable, collapsible sidebar with rail mode and themed tooltips
- Silent background auto-updates (toggleable from the `Home` page)

---

## Download

### Requirements

- Windows 10 or Windows 11
- **Steam** ARK: Survival Evolved

### Install

1. Open the [latest release](https://github.com/CedrickGD/RazorReaper/releases/latest)
2. Download the **`RazorReaper-Setup.exe`** installer from the release assets
3. Run the installer
4. Finish setup and launch RazorReaper

> No `.rar` package anymore.  
> The release download is the installer `.exe`.

### Updating

- RazorReaper checks for updates automatically on launch and installs them silently when you close the app
- The auto-update toggle lives on the `Home` page if you'd rather manage updates manually
- Manual updates are simple: download the newest release installer and run it

---

## Build From Source

If you want to build it yourself:

### Requirements for source build

- Windows 10 or Windows 11
- .NET SDK `10.0` x64
- Visual Studio 2022 with .NET MAUI workload

### Commands

```powershell
dotnet build RazorReaper.sln
dotnet run --project RazorReaper/RazorReaper.csproj -f net10.0-windows10.0.19041.0
```

---

## Notes

- RazorReaper is focused on **ARK: Survival Evolved**
- Some tools expect a valid ARK install path
- Steam edition only
- Geo/telemetry notice: see [PRIVACY.md](PRIVACY.md)
