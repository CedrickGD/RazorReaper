# RazorReaper 

**RazorReaper** is the modern successor to ReaperV2, rebuilt from the ground up using  
**Blazor Hybrid (.NET MAUI)**.

It is a fast, clean desktop toolkit for **ARK: Survival Evolved**, focused on real
in-game workflows, configuration management, and utility features.

---

## 🌐 Features

- **Dashboard** — system uptime, resources, storage, network status, and ARK path detection
- **Server Manager** — query ARK servers, connect via Steam, manage favorites, open Steam browser
- **Game Controls** — launch/close ARK, reconnect, disconnect, debug commands
- **INI Tools** — BaseDeviceProfiles.ini editor with preset library (load, save, reset)
- **Suit FOV** — one-click camera trace toggle with automatic detection
- **Fonts** — switch ARK fonts, auto install, open ARK in Steam
- **Textures** — delete or restore pixel textures by category
- **Paintings** — manage MyPaintings and related resources
- **Mutagen Prices** — searchable Genesis Part 2 dino list
- **Building Guides** — foundation height technique references
- **Auto Clicker** — advanced timing, randomization, burst mode, click heatmap
- **Notifications** — clean toast system with sound and activity log

---

## 🧠 ReaperV2 vs RazorReaper

| Feature            | ReaperV2 | RazorReaper |
|--------------------|----------|-------------|
| UI / UX            | Basic    | Modern      |
| Technology         | WinForms | Blazor Hybrid |
| Path detection     | Manual   | Automatic   |
| Presets            | Limited  | Built-in    |
| Server tools       | Basic    | Steam-based |
| Notifications      | No       | Yes         |
| Status             | Archived | Active      |

---

## ⬇️ Download

### ✅ Requirements
- Windows 10 / 11  
- .NET SDK 9.0.306 (x64)  
- Steam + ARK: Survival Evolved  

🔗 .NET SDK:  
https://builds.dotnet.microsoft.com/dotnet/Sdk/9.0.306/dotnet-sdk-9.0.306-win-x64.exe

### 📦 Latest Release
https://github.com/CedrickGD/RazorReaper/releases/latest

1. Download the ZIP  
2. Extract it  
3. Run `RazorReaper.exe`

---

## 🛠️ Build From Source

```powershell
dotnet build RazorReaper.sln
dotnet run --project RazorReaper/RazorReaper.csproj -f net9.0-windows10.0.19041.0
