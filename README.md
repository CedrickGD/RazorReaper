<div align="center">

<img src="RazorReaper/wwwroot/images/RRlogo.png" alt="RazorReaper logo" width="140" />

# RazorReaper

**The all-in-one desktop toolkit for ARK: Survival Evolved.**

35+ tools for configs, visuals, automation and game knowledge — in one fast, themeable Windows app.

[![Latest release](https://img.shields.io/github/v/release/CedrickGD/RazorReaper?label=release&color=7c3aed)](https://github.com/CedrickGD/RazorReaper/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/CedrickGD/RazorReaper/total?color=7c3aed)](https://github.com/CedrickGD/RazorReaper/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue)](#requirements)
[![Built with](https://img.shields.io/badge/built%20with-.NET%2010%20·%20MAUI%20Blazor%20Hybrid-512bd4)](#build-from-source)
[![License](https://img.shields.io/badge/license-proprietary-lightgrey)](LICENSE.txt)

[**Download**](https://github.com/CedrickGD/RazorReaper/releases/latest) · [**Discord**](https://discord.gg/TZJm7Rg53d) · [**Shop**](https://rr.sellhub.cx)

</div>

---

RazorReaper bundles the entire day-to-day ARK workflow into a single desktop app: INI tuning, visual tweaks, custom skies and loading screens, input automation, breeding intel, map knowledge and system utilities — built around how the game is actually played, not around launcher clutter.

> **Steam ARK: Survival Evolved only.** Microsoft Store builds, ARK: Survival Ascended, console and other platforms are not supported.

## Highlights

- **One app, 35+ tools** — organized into searchable sections with instant global search (`Ctrl+K`)
- **Self-contained installer** — bundles the .NET runtime; download, install, play (no prerequisites)
- **Silent auto-updates** — updates download in the background and install when you close the app (toggleable)
- **Fully themeable** — recolor the entire app with a built-in accent color picker
- **Tray-native** — minimizes to the system tray and stays out of your way while you play
- **Discord Rich Presence** — shows the tool you're using as your Discord activity (optional)
- **Session HUD** — click-through in-game overlay with clock, session timer, server info and alerts

## Features

### Core

| Tool | What it does |
| --- | --- |
| **Home** | Dashboard with system info, update controls, accent theming and game path handling |
| **Server** | Connect to and manage ARK servers from one panel |
| **Game** | Launch, control and monitor the ARK game process |

### ARK Tweaks

| Tool | What it does |
| --- | --- |
| **INI Changer** | Three-column INI workspace with preset gallery, editing, import/export |
| **INI Builder** | One-click `Game.ini` / `GameUserSettings.ini` presets with automatic backups |
| **Vision Tools** | TEK camera behavior, scope visibility and FOV in one place |
| **Gamma** | System-wide screen gamma on a hotkey or Logitech G HUB mouse button |
| **Launch Options** | ARK startup flags with their trade-offs explained |
| **Fonts** | In-game font switching with presets |
| **Pixel Glitch** | Texture file utilities with backup and revert support |
| **Paintings** | `MyPaintings` workflow and preset handling |

### Custom ARK

| Tool | What it does |
| --- | --- |
| **Sky Changer** | Replace the in-game sky with an image or solid color by patching local sky files |
| **Loading Screen** | Swap ARK's startup and loading videos for your own — fully reversible |
| **Char Manager** | Manage the appearance presets on ARK's character-creation screen |
| **Stretched Res** | Switch to a stretched resolution safely, with 15-second auto-revert |

### Automation

| Tool | What it does |
| --- | --- |
| **Scripts** | Automation hub — start, stop and configure ARK automation scripts |
| **Global Hotkeys** | Every script's start/stop hotkey, visible and editable at a glance |
| **Auto Clicker** | Advanced mouse-click automation |
| **Macros** | Record, replay and run premade input macros |
| **Fed Suit** | Automated transmitter slot transfers for the Federation Suit grind |
| **Auto Antidote** | Watches the HUD and refreshes your antidote automatically |
| **HUD Overlay** | Click-through overlay: clock, session timer, server info, tool status, alerts |
| **Notifier** | Live in-game alerts for rare dinos, resources and OSD events |

### Mods & Intel

| Tool | What it does |
| --- | --- |
| **Mutagen Prices** | Gen2 creature mutagen values, searchable by name |
| **Line List** | Track breeding lines and build WTS/WTB trade posts |
| **OC BPs** | Genesis 2 mission rewards for overcapped blueprints |
| **Bosses** | Boss and mini-boss tribute requirements, sorted by map |
| **TP Locations** | Teleport-worthy spots per map with copyable `setplayerpos` commands |
| **Underwater Drops** | Underwater loot crates by coordinate, searchable by map and crate type |
| **Map Mods** | Modded-map spots — caves, landmarks, obelisks, POIs — with coordinates and notes |
| **Steam Mods** | Installed workshop mods with fast search and latest-install filtering |

### Utilities

| Tool | What it does |
| --- | --- |
| **Building** | Foundation, wall, layout and meta build patterns with fullscreen lightbox |
| **Desync** | Freeze your character server-side by blocking ARK's outbound traffic — always auto-reverting |
| **File Modifier** | Remove or replace individual ARK files and clear redundant cooked data to reclaim disk space |
| **Crosshair** | Always-on-top crosshair overlay with editor, presets, animations and image import |
| **Macro / AHK** | Crafting macro and AHK references, videos and scripts |
| **Compact ARK** | Shrink the ARK install with transparent NTFS compression |

Plus **Troubleshoot**, **Feedback** and **Credits** pages built in.

## Getting Started

### Requirements

- Windows 10 or Windows 11 (x64)
- Steam **ARK: Survival Evolved**

### Install

1. Grab the [latest release](https://github.com/CedrickGD/RazorReaper/releases/latest)
2. Download **`RazorReaper-Setup.exe`** from the release assets
3. Run the installer and launch RazorReaper

The installer is fully self-contained — the .NET runtime ships inside, so there is nothing else to install.

### Updating

RazorReaper checks for updates on launch, downloads them silently in the background and installs them when you close the app. Prefer manual control? Toggle auto-updates off on the **Home** page and run the newest installer yourself whenever you like.

## Build From Source

```powershell
git clone https://github.com/CedrickGD/RazorReaper.git
cd RazorReaper
dotnet build RazorReaper.sln
dotnet run --project RazorReaper/RazorReaper.csproj -f net10.0-windows10.0.19041.0
```

**Prerequisites:** Windows 10/11, .NET SDK 10.0 (x64), Visual Studio 2022 with the .NET MAUI workload.

## Related Projects

- [**GammaHotkey**](https://github.com/CedrickGD/GammaHotkey) — the standalone tray version of RazorReaper's Gamma tool (any GPU, G HUB Lua export)
- [**Ark-ASE-INI-Files**](https://github.com/CedrickGD/Ark-ASE-INI-Files) — a public archive of ARK: Survival Evolved INI configs

## Notes

- Some tools expect a valid ARK install path and will guide you to set one
- Actions that touch game files create backups and are designed to be reversible
- Telemetry and data handling: see [PRIVACY.md](PRIVACY.md)

## License

Proprietary — see [LICENSE.txt](LICENSE.txt). © 2026 Cedrick Grabe. All rights reserved.

RazorReaper is an independent community tool and is not affiliated with, endorsed by, or connected to Studio Wildcard or Snail Games.
