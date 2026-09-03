using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Diagnostics;
using RazorReaper.Navigation;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Media;

namespace RazorReaper.Services.Diagnostics;

internal static class DiagnosticChecks
{
    public static DiagnosticCheck Pass(string key, string label, object? value = null, string? detail = null)
        => Make(key, label, "pass", value, detail);

    public static DiagnosticCheck Warning(string key, string label, object? value = null, string? detail = null)
        => Make(key, label, "warning", value, detail);

    public static DiagnosticCheck Fail(string key, string label, object? value = null, string? detail = null)
        => Make(key, label, "fail", value, detail);

    public static DiagnosticCheck Unknown(string key, string label, object? value = null, string? detail = null)
        => Make(key, label, "unknown", value, detail);

    private static DiagnosticCheck Make(string key, string label, string status, object? value, string? detail)
        => new() { Key = key, Label = label, Status = status, Value = value, Detail = detail };
}

/// <summary>Process/runtime facts and the originating support surface.</summary>
public sealed class AppRuntimeDiagnosticProvider(TimeProvider timeProvider) : IDiagnosticProvider
{
    private static readonly DateTimeOffset StartedAt = GetStartedAt();

    public string ProviderId => "app_runtime";

    public Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var lastError = Safe(() => AppDiagnostics.GetLastError());
        var checks = new List<DiagnosticCheck>
        {
            DiagnosticChecks.Pass("app_version", "RazorReaper version", SafeAppVersion()),
            DiagnosticChecks.Pass("runtime", ".NET runtime", RuntimeInformation.FrameworkDescription),
            DiagnosticChecks.Pass("process_arch", "Process architecture", RuntimeInformation.ProcessArchitecture.ToString()),
            DiagnosticChecks.Pass("uptime_minutes", "App session (minutes)", Math.Max(0, (int)(now - StartedAt).TotalMinutes)),
            DiagnosticChecks.Pass("source_route", "Report opened from", context.SourceRoute),
            DiagnosticChecks.Pass("culture", "Display culture", CultureInfo.CurrentCulture.Name),
            DiagnosticChecks.Pass("timezone", "Time zone", TimeZoneInfo.Local.Id),
            lastError is null
                ? DiagnosticChecks.Pass("last_app_error", "Last app error", "none")
                : DiagnosticChecks.Warning("last_app_error", "Last app error", lastError.Code,
                    lastError.Timestamp == DateTimeOffset.MinValue ? null : lastError.Timestamp.ToUniversalTime().ToString("O")),
        };

        return Task.FromResult(new DiagnosticProviderData(
            lastError is null ? "ok" : "warning",
            checks,
            "App/runtime snapshot; no log contents or stack traces are attached."));
    }

    private static DateTimeOffset GetStartedAt()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string SafeAppVersion()
    {
        try { return AppInfo.Current.VersionString; }
        catch { return typeof(AppRuntimeDiagnosticProvider).Assembly.GetName().Version?.ToString(3) ?? "unknown"; }
    }

    private static T? Safe<T>(Func<T?> action) where T : class
    {
        try { return action(); }
        catch { return null; }
    }
}

/// <summary>Read-only Windows capacity/capability checks. No IP, SSID, MAC, or user path.</summary>
public sealed class WindowsHostDiagnosticProvider : IDiagnosticProvider
{
    public string ProviderId => "windows_host";

    public Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isAdmin = IsAdministrator();
        var (freeGiB, totalGiB) = GetSystemDriveSpace();
        var memoryMiB = GetAvailableMemoryMiB();

        var checks = new List<DiagnosticCheck>
        {
            DiagnosticChecks.Pass("os", "Windows version", RuntimeInformation.OSDescription),
            DiagnosticChecks.Pass("os_arch", "Operating-system architecture", RuntimeInformation.OSArchitecture.ToString()),
            DiagnosticChecks.Pass("is_64_bit", "64-bit process", Environment.Is64BitProcess),
            isAdmin
                ? DiagnosticChecks.Pass("administrator", "Running as Administrator", true)
                : DiagnosticChecks.Warning("administrator", "Running as Administrator", false,
                    "Desync and protected file changes can require elevation."),
            DiagnosticChecks.Pass("logical_processors", "Logical processors", Environment.ProcessorCount),
            memoryMiB > 0
                ? DiagnosticChecks.Pass("available_memory_mib", "Memory available to app (MiB)", memoryMiB)
                : DiagnosticChecks.Unknown("available_memory_mib", "Memory available to app (MiB)", "unavailable"),
            totalGiB > 0
                ? DiagnosticChecks.Pass("system_drive_free_gib", "System drive free / total (GiB)", $"{freeGiB}/{totalGiB}")
                : DiagnosticChecks.Unknown("system_drive_free_gib", "System drive free / total (GiB)", "unavailable"),
        };

        return Task.FromResult(new DiagnosticProviderData(
            isAdmin ? "ok" : "warning",
            checks,
            "Local Windows capability snapshot."));
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static long GetAvailableMemoryMiB()
    {
        try { return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024); }
        catch { return 0; }
    }

    private static (long FreeGiB, long TotalGiB) GetSystemDriveSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrWhiteSpace(root)) return (0, 0);
            var drive = new DriveInfo(root);
            const long gib = 1024L * 1024 * 1024;
            return (drive.AvailableFreeSpace / gib, drive.TotalSize / gib);
        }
        catch { return (0, 0); }
    }
}

/// <summary>Identity, signing, account access, and local license state without duplicating raw IDs.</summary>
public sealed class IdentityLicenseDiagnosticProvider(
    IClientIdentityService clientIdentity,
    IInstallIdentityService installIdentity,
    ILicenseService license,
    IAccessGateService accessGate) : IDiagnosticProvider
{
    public string ProviderId => "identity_license_access";

    public Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClientIdentity? identity = null;
        try { identity = clientIdentity.GetIdentity(); } catch { }

        var installIdValid = Guid.TryParse(identity?.InstallId, out _);
        var hwidPresent = !string.IsNullOrWhiteSpace(identity?.HardwareId);
        var suspended = Safe(() => accessGate.IsSuspended, false);
        var activated = Safe(() => license.IsActivated, false);
        var premium = Safe(() => license.IsPremium, false);
        var keyPresent = Safe(() => !string.IsNullOrWhiteSpace(license.CurrentLicenseKey), false);

        var checks = new List<DiagnosticCheck>
        {
            installIdValid
                ? DiagnosticChecks.Pass("install_identity", "Install identity", "ready")
                : DiagnosticChecks.Fail("install_identity", "Install identity", "missing or invalid"),
            hwidPresent
                ? DiagnosticChecks.Pass("hardware_identity", "Hardware identity", "ready")
                : DiagnosticChecks.Fail("hardware_identity", "Hardware identity", "missing"),
            installIdentity.IsRegistered
                ? DiagnosticChecks.Pass("request_signing", "Authenticated request signing", "registered")
                : DiagnosticChecks.Warning("request_signing", "Authenticated request signing", "not registered"),
            activated
                ? DiagnosticChecks.Pass("license_activated", "License activated", true)
                : DiagnosticChecks.Warning("license_activated", "License activated", false),
            DiagnosticChecks.Pass("license_tier", "License tier", premium ? "premium" : "free"),
            keyPresent
                ? DiagnosticChecks.Pass("license_key_present", "Saved license key", true)
                : DiagnosticChecks.Warning("license_key_present", "Saved license key", false),
            suspended
                ? DiagnosticChecks.Fail("access_gate", "Account access", accessGate.Mode ?? "suspended")
                : DiagnosticChecks.Pass("access_gate", "Account access", "allowed"),
        };

        var hasWarning = !installIdValid || !hwidPresent || !installIdentity.IsRegistered || !activated || suspended;
        return Task.FromResult(new DiagnosticProviderData(
            hasWarning ? "warning" : "ok",
            checks,
            "Identity values themselves remain in the established top-level fields."));
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }
}

/// <summary>ARK process and installation prerequisites shared by most feature pages.</summary>
public sealed class ArkEnvironmentDiagnosticProvider(
    IProcessService processes,
    IArkPathProvider arkPaths,
    IOptions<AppConfiguration> options) : IDiagnosticProvider
{
    public string ProviderId => "ark_environment";

    public Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processCount = GetGameProcessCount(options.Value.Ark.GameProcessName);
        var arkRoot = Safe(() => arkPaths.FindArkPath());
        var rootFound = !string.IsNullOrWhiteSpace(arkRoot) && Directory.Exists(arkRoot);
        var executableFound = rootFound && File.Exists(Path.Combine(arkRoot!, options.Value.Ark.ExecutableRelativePath));
        var deviceProfilesFound = rootFound && File.Exists(Path.Combine(arkRoot!, options.Value.Ark.ConfigRelativePath));
        var savedConfigFound = rootFound && Directory.Exists(Path.Combine(
            arkRoot!, "ShooterGame", "Saved", "Config", "WindowsNoEditor"));
        var contentFound = rootFound && Directory.Exists(Path.Combine(arkRoot!, "ShooterGame", "Content"));

        var checks = new List<DiagnosticCheck>
        {
            processCount > 0
                ? DiagnosticChecks.Pass("game_process", "ShooterGame process", $"running ({processCount})")
                : DiagnosticChecks.Warning("game_process", "ShooterGame process", "not running"),
            rootFound
                ? DiagnosticChecks.Pass("install_root", "ARK install detected", true)
                : DiagnosticChecks.Fail("install_root", "ARK install detected", false),
            executableFound
                ? DiagnosticChecks.Pass("game_executable", "ShooterGame.exe", "found")
                : DiagnosticChecks.Fail("game_executable", "ShooterGame.exe", "not found"),
            deviceProfilesFound
                ? DiagnosticChecks.Pass("device_profiles", "BaseDeviceProfiles.ini", "found")
                : DiagnosticChecks.Warning("device_profiles", "BaseDeviceProfiles.ini", "not found"),
            savedConfigFound
                ? DiagnosticChecks.Pass("saved_config", "WindowsNoEditor config folder", "found")
                : DiagnosticChecks.Warning("saved_config", "WindowsNoEditor config folder", "not found"),
            contentFound
                ? DiagnosticChecks.Pass("game_content", "ARK content folder", "found")
                : DiagnosticChecks.Warning("game_content", "ARK content folder", "not found"),
        };

        return Task.FromResult(new DiagnosticProviderData(
            rootFound && executableFound ? (processCount > 0 ? "ok" : "warning") : "warning",
            checks,
            "Paths are checked locally but never attached."));
    }

    private int GetGameProcessCount(string processName)
    {
        Process[] found = [];
        try
        {
            found = processes.GetProcessesByName(processName);
            return found.Length;
        }
        catch { return 0; }
        finally
        {
            foreach (var process in found) process.Dispose();
        }
    }

    private static string? Safe(Func<string?> action)
    {
        try { return action(); }
        catch { return null; }
    }
}

/// <summary>
/// Compact route/script coverage manifest. Environment and settings providers carry the live
/// blockers; this provider makes it explicit which feature the report knows how to reason about.
/// </summary>
public sealed class FeatureCatalogDiagnosticProvider : IDiagnosticProvider
{
    private static readonly (string Key, string Label)[] AutomationScripts =
    [
        ("script_yuty", "Yuty"),
        ("script_auto_walk", "Auto-Walk"),
        ("script_mammoth", "Mammoth"),
        ("script_astro", "Astro"),
        ("script_auto_download", "Auto Download"),
        ("script_fast_tp", "Fast TP"),
        ("script_take_all", "Take All"),
        ("script_tek_saddle", "Tek Saddle"),
        ("script_noglin", "Noglin"),
        ("script_inv_size", "Inv Size"),
        ("script_anti_afk", "Anti-AFK"),
        ("script_turret_manager", "Turret Manager"),
        ("script_armor_swap", "Armor Swap"),
        ("script_dino_ready", "Dino Ready"),
        ("script_crafting", "Crafting"),
        ("script_auto_antidote", "Auto Antidote"),
        ("script_fed_suit", "Fed Suit"),
    ];

    private readonly string _providerId;
    private readonly string _category;
    private readonly bool _includeScripts;
    private readonly IPreferencesStore? _preferences;
    private readonly IActivityService? _activities;
    private readonly IArkPathProvider? _arkPaths;
    private readonly IProcessService? _processes;
    private readonly IOptions<AppConfiguration>? _options;
    private readonly IAutoClickerRuntime? _autoClicker;
    private readonly IStretchedResService? _stretchedRes;
    private readonly IFfmpegProvider? _ffmpeg;

    public FeatureCatalogDiagnosticProvider(
        string providerId,
        string category,
        bool includeScripts = false,
        IPreferencesStore? preferences = null,
        IActivityService? activities = null,
        IArkPathProvider? arkPaths = null,
        IProcessService? processes = null,
        IOptions<AppConfiguration>? options = null,
        IAutoClickerRuntime? autoClicker = null,
        IStretchedResService? stretchedRes = null,
        IFfmpegProvider? ffmpeg = null)
    {
        _providerId = providerId;
        _category = category;
        _includeScripts = includeScripts;
        _preferences = preferences;
        _activities = activities;
        _arkPaths = arkPaths;
        _processes = processes;
        _options = options;
        _autoClicker = autoClicker;
        _stretchedRes = stretchedRes;
        _ffmpeg = ffmpeg;
    }

    public string ProviderId => _providerId;

    public Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = CaptureSharedState();
        var recent = Safe(() => _activities?.GetRecentActivities(), []) ?? [];
        var checks = NavCatalog.Pages
            .Where(page => string.Equals(page.Category, _category, StringComparison.Ordinal))
            .Select(page => BuildRouteCheck(page, state, recent))
            .ToList();

        if (_includeScripts)
        {
            checks.AddRange(AutomationScripts.Select(script =>
                BuildScriptCheck(script, state, recent)));
        }

        return Task.FromResult(new DiagnosticProviderData(
            checks.Any(check => check.Status is "warning" or "fail") ? "warning" : "ok",
            checks,
            _includeScripts
                ? "Live prerequisites and last outcomes for every Automation route and built-in script."
                : $"Live prerequisites and last outcomes for every {_category} route."));
    }

    private DiagnosticCheck BuildRouteCheck(
        NavPage page,
        FeatureState state,
        IReadOnlyList<RazorReaper.Models.ActivityItem> recent)
    {
        var route = NavCatalog.Normalize(page.Route);
        var last = FindRecent(route, recent);
        var detail = LastOutcome(route, last);
        DiagnosticCheck check = route switch
        {
            "home" => DiagnosticChecks.Pass(Key(route), page.Label, "dashboard ready", detail),
            "server" => DiagnosticChecks.Pass(Key(route), page.Label,
                state.ServerDataFound ? "saved server data found" : "no saved server data", detail),
            "game" => StateCheck(route, page.Label, state.GameRunning, "ARK running", "ARK not running", detail),
            "settings" => StateCheck(route, page.Label, state.PreferencesReadable, "preferences readable", "preferences unavailable", detail),
            "hotkeys" => DiagnosticChecks.Pass(Key(route), page.Label, $"{state.CustomScriptHotkeys} script overrides", detail),

            "ini-changer" or "ini-builder" or "vision" => StateCheck(route, page.Label, state.SavedConfigFound,
                "ARK config found", "ARK config missing", detail),
            "gamma" => DiagnosticChecks.Pass(Key(route), page.Label,
                state.GammaConfigFound ? "saved config" : "factory config", detail),
            "launch-options" => DiagnosticChecks.Pass(Key(route), page.Label, "Steam handoff ready", detail),
            "fonts" => StateCheck(route, page.Label, state.ArkRootFound, "ARK install found", "ARK install missing", detail),
            "pixel" => StateCheck(route, page.Label, state.ContentFound, "ARK content found", "ARK content missing", detail),
            "paintings" => StateCheck(route, page.Label, state.ArkRootFound,
                $"{state.PaintingCount} painting files", "ARK install missing", detail),

            "custom-lab" => StateCheck(route, page.Label, state.ContentFound, "ARK content found", "ARK content missing", detail),
            "loading-screen" => LoadingScreenCheck(route, page.Label, state, detail),
            "char-manager" => CharManagerCheck(route, page.Label, state, detail),
            "stretched-res" => state.CurrentResolution is null
                ? DiagnosticChecks.Warning(Key(route), page.Label, "display state unavailable", detail)
                : DiagnosticChecks.Pass(Key(route), page.Label,
                    state.StretchedPending ? $"{state.CurrentResolution}; confirmation pending" : state.CurrentResolution,
                    detail),

            "autoclicker" => DiagnosticChecks.Pass(Key(route), page.Label,
                state.AutoClickerRunning ? $"running; {state.AutoClickCount} clicks" : "stopped", detail),
            "scripts" => StateCheck(route, page.Label, state.GameRunning,
                $"ARK running; {AutomationScripts.Length} scripts", $"ARK stopped; {AutomationScripts.Length} scripts", detail),
            "hud-overlay" => DiagnosticChecks.Pass(Key(route), page.Label,
                state.HudConfigFound ? "saved layout" : "default layout", detail),
            "notifier" => StateCheck(route, page.Label, state.NotifierConfigured,
                "endpoint configured", "endpoint not configured", detail),

            "line-list" => DiagnosticChecks.Pass(Key(route), page.Label,
                state.LineListFound ? "saved list found" : "empty local list", detail),
            "steam-mods" => StateCheck(route, page.Label, state.WorkshopFound,
                $"{state.WorkshopModCount} workshop folders", "workshop folder missing", detail),
            "dino-prices" or "oc-bps" or "bosses" or "tp-locations" or "underwater-drops" or "map-mods"
                => DiagnosticChecks.Pass(Key(route), page.Label, "built-in data ready", detail),

            "building" => DiagnosticChecks.Pass(Key(route), page.Label, "built-in guide ready", detail),
            "desync" => DesyncCheck(route, page.Label, state, detail),
            "file-modifier" => StateCheck(route, page.Label, state.ArkRootFound && state.IsAdministrator,
                $"ready; {state.FileModifierBackups} backups", state.ArkRootFound ? "administrator required" : "ARK install missing", detail),
            "crosshair" => DiagnosticChecks.Pass(Key(route), page.Label,
                $"{state.CrosshairPresetCount} local presets", detail),
            "convert" => StateCheck(route, page.Label, state.FfmpegInstalled,
                "FFmpeg ready", "FFmpeg downloads on first use", detail),
            "compact-ark" => StateCheck(route, page.Label, state.ArkRootFound && !state.GameRunning,
                "ARK found; game stopped", state.ArkRootFound ? "close ARK before changing files" : "ARK install missing", detail),

            "troubleshoot" => DiagnosticChecks.Pass(Key(route), page.Label, "diagnostics ready", detail),
            "feedback" => DiagnosticChecks.Pass(Key(route), page.Label, "in-app reports ready", detail),
            "credits" => DiagnosticChecks.Pass(Key(route), page.Label, "support links ready", detail),
            _ => DiagnosticChecks.Unknown(Key(route), page.Label, "state unavailable", detail),
        };

        if (last is not null && last.Type is "warning" or "error" && check.Status == "pass")
        {
            check = check with { Status = "warning" };
        }

        return check;
    }

    private DiagnosticCheck BuildScriptCheck(
        (string Key, string Label) script,
        FeatureState state,
        IReadOnlyList<RazorReaper.Models.ActivityItem> recent)
    {
        var preferenceKey = ScriptPreferenceKey(script.Key);
        var customHotkey = _preferences is not null && Safe(() => _preferences.ContainsKey(preferenceKey), false);
        var last = recent.FirstOrDefault(item =>
            (item.Title ?? string.Empty).Contains(script.Label, StringComparison.OrdinalIgnoreCase));
        var detail = last is null
            ? null
            : $"Last outcome: {NormalizeActivityType(last.Type)}, {last.Timestamp.ToUniversalTime():O}";

        var check = state.GameRunning
            ? DiagnosticChecks.Pass(script.Key, script.Label, customHotkey ? "custom hotkey" : "default hotkey", detail)
            : DiagnosticChecks.Warning(script.Key, script.Label, customHotkey ? "custom hotkey; ARK stopped" : "default hotkey; ARK stopped", detail);
        if (last is not null && last.Type is "warning" or "error") check = check with { Status = "warning" };
        return check;
    }

    private FeatureState CaptureSharedState()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RazorReaper");
        var arkRoot = Safe(() => _arkPaths?.FindArkPath());
        var arkRootFound = !string.IsNullOrWhiteSpace(arkRoot) && Directory.Exists(arkRoot);
        var processName = _options?.Value.Ark.GameProcessName ?? "ShooterGame";
        var executableRelative = _options?.Value.Ark.ExecutableRelativePath ?? @"ShooterGame\Binaries\Win64\ShooterGame.exe";
        var gameRunning = Safe(() => _processes is not null && _processes.IsProcessRunning(processName), false);
        var savedConfig = arkRootFound && Directory.Exists(Path.Combine(arkRoot!, "ShooterGame", "Saved", "Config", "WindowsNoEditor"));
        var content = arkRootFound && Directory.Exists(Path.Combine(arkRoot!, "ShooterGame", "Content"));
        var movies = content && Directory.Exists(Path.Combine(arkRoot!, "ShooterGame", "Content", "Movies"));
        var executable = arkRootFound && File.Exists(Path.Combine(arkRoot!, executableRelative));
        var paintings = arkRootFound ? SafeCountFiles(Path.Combine(arkRoot!, "ShooterGame", "Saved", "MyPaintings"), "*.*") : 0;
        var characterPresetDirectory = arkRootFound
            ? Path.Combine(arkRoot!, "ShooterGame", "Saved", "SavedArksLocal")
            : null;
        var characterPresetDirectoryFound = characterPresetDirectory is not null && Directory.Exists(characterPresetDirectory);
        var characters = characterPresetDirectoryFound
            ? SafeCountFiles(characterPresetDirectory!, "*.arkcharactersetting")
            : 0;
        var workshop = arkRootFound
            ? Safe(() => Path.GetFullPath(Path.Combine(arkRoot!, "..", "..", "workshop", "content", "346110"))) ?? string.Empty
            : string.Empty;
        var workshopFound = !string.IsNullOrWhiteSpace(workshop) && Directory.Exists(workshop);

        string? currentResolution = null;
        var stretchedPending = false;
        try
        {
            if (_stretchedRes is not null)
            {
                var resolution = _stretchedRes.GetCurrentResolution();
                currentResolution = $"{resolution.Width}x{resolution.Height}@{resolution.RefreshHz}";
                stretchedPending = _stretchedRes.IsPendingConfirmation;
            }
        }
        catch { }

        return new FeatureState(
            arkRootFound,
            executable,
            savedConfig,
            content,
            movies,
            gameRunning,
            IsAdministrator(),
            Safe(() => _ffmpeg?.IsInstalled ?? File.Exists(Path.Combine(appData, "Tools", "ffmpeg", "ffmpeg.exe")), false),
            Safe(() => _autoClicker is not null && _autoClicker.IsRunning, false),
            Safe(() => _autoClicker?.ClickCount ?? 0, 0),
            currentResolution,
            stretchedPending,
            _preferences is not null,
            ScriptKeysCustomized(),
            !string.IsNullOrWhiteSpace(Safe(() => _preferences?.Get("notifier.endpoint", string.Empty))),
            File.Exists(Path.Combine(appData, "gamma-config.json")),
            File.Exists(Path.Combine(appData, "hud-overlay.json")),
            File.Exists(Path.Combine(appData, "custom-servers.json")),
            File.Exists(Path.Combine(appData, "line-list.json")),
            paintings,
            characterPresetDirectoryFound,
            characters,
            workshopFound,
            workshopFound ? SafeCountDirectories(workshop) : 0,
            SafeCountFiles(Path.Combine(appData, "Crosshairs"), "*.*"),
            SafeCountFiles(Path.Combine(appData, "FileModBackups", "files"), "*.*"));
    }

    private static DiagnosticCheck DesyncCheck(string route, string label, FeatureState state, string? detail)
    {
        if (!state.IsAdministrator)
            return DiagnosticChecks.Warning(Key(route), label, "administrator required", detail);
        if (!state.GameRunning)
            return DiagnosticChecks.Warning(Key(route), label, "ARK not running", detail);
        if (!state.ArkExecutableFound)
            return DiagnosticChecks.Fail(Key(route), label, "ShooterGame.exe missing", detail);
        return DiagnosticChecks.Pass(Key(route), label, "firewall prerequisites ready", detail);
    }

    private static DiagnosticCheck LoadingScreenCheck(
        string route,
        string label,
        FeatureState state,
        string? detail)
    {
        if (!state.MoviesFound)
            return DiagnosticChecks.Warning(Key(route), label, "ARK movies missing", detail);
        if (!state.FfmpegInstalled)
            return DiagnosticChecks.Warning(Key(route), label, "movies found; converter pending", detail);
        return DiagnosticChecks.Pass(Key(route), label, "movies + converter ready", detail);
    }

    private static DiagnosticCheck CharManagerCheck(
        string route,
        string label,
        FeatureState state,
        string? detail)
    {
        if (!state.ArkRootFound)
            return DiagnosticChecks.Warning(Key(route), label, "ARK install missing", detail);
        if (!state.CharacterPresetDirectoryFound)
            return DiagnosticChecks.Warning(Key(route), label, "SavedArksLocal missing", detail);
        return DiagnosticChecks.Pass(Key(route), label, $"{state.CharacterPresetCount} character presets", detail);
    }

    private static DiagnosticCheck StateCheck(
        string route,
        string label,
        bool ready,
        string readyValue,
        string unavailableValue,
        string? detail)
        => ready
            ? DiagnosticChecks.Pass(Key(route), label, readyValue, detail)
            : DiagnosticChecks.Warning(Key(route), label, unavailableValue, detail);

    private static string Key(string route) => $"route_{route.Replace('-', '_')}";

    private static string ScriptPreferenceKey(string diagnosticKey)
        => diagnosticKey switch
        {
            "script_auto_walk" => "script.autowalk.hotkey",
            "script_auto_download" => "script.autodownload.hotkey",
            "script_fast_tp" => "script.fasttp.hotkey",
            "script_take_all" => "script.takeall.hotkey",
            "script_tek_saddle" => "script.teksaddle.hotkey",
            "script_inv_size" => "script.invsize.hotkey",
            "script_anti_afk" => "script.antiafk.hotkey",
            "script_turret_manager" => "script.turret.hotkey",
            "script_armor_swap" => "script.flak.hotkey",
            "script_dino_ready" => "script.dinoready.hotkey",
            "script_auto_antidote" => "script.antidote.hotkey",
            "script_fed_suit" => "script.fedsuit.hotkey",
            _ => $"script.{diagnosticKey["script_".Length..].Replace("_", string.Empty)}.hotkey",
        };

    private int ScriptKeysCustomized()
        => _preferences is null
            ? 0
            : AutomationScripts.Count(script => Safe(() => _preferences.ContainsKey(ScriptPreferenceKey(script.Key)), false));

    private static RazorReaper.Models.ActivityItem? FindRecent(
        string route,
        IReadOnlyList<RazorReaper.Models.ActivityItem> recent)
    {
        var keywords = route switch
        {
            "server" => new[] { "server", "favorite" },
            "game" => new[] { "game", "ark launched", "ark:" },
            "settings" => new[] { "font set", "accent color" },
            "ini-changer" or "ini-builder" => new[] { "ini" },
            "vision" => new[] { "fov", "camera trace", "scope" },
            "gamma" => new[] { "gamma" },
            "launch-options" => new[] { "launch text", "ark properties" },
            "fonts" => new[] { "font" },
            "pixel" => new[] { "water files", "texture" },
            "paintings" => new[] { "painting" },
            "custom-lab" => new[] { "sky" },
            "loading-screen" => new[] { "ark video", "loading" },
            "char-manager" => new[] { "character preset" },
            "stretched-res" => new[] { "resolution" },
            "autoclicker" => new[] { "autoclicker" },
            "scripts" => new[] { "script", "macro", "yuty", "mammoth", "turret", "fed suit" },
            "hud-overlay" => new[] { "hud" },
            "notifier" => new[] { "notifier" },
            "line-list" => new[] { "breeding line", "wts/wtb" },
            "steam-mods" => new[] { "workshop" },
            "desync" => new[] { "desync" },
            "file-modifier" => new[] { "file modifier", "seekfree" },
            "crosshair" => new[] { "crosshair" },
            "convert" => new[] { "convert" },
            "compact-ark" => new[] { "compact", "uncompress" },
            _ => Array.Empty<string>(),
        };

        return keywords.Length == 0
            ? null
            : recent.FirstOrDefault(activity => keywords.Any(keyword =>
                (activity.Title ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? LastOutcome(string route, RazorReaper.Models.ActivityItem? activity)
    {
        if (activity is null) return null;
        var operation = route == "desync"
            ? SettingsOperationsDiagnosticProvider.ClassifyActivity(activity.Title)
            : "operation";
        return $"Last {operation}: {NormalizeActivityType(activity.Type)}, {activity.Timestamp.ToUniversalTime():O}";
    }

    private static string NormalizeActivityType(string? type)
        => type?.ToLowerInvariant() switch
        {
            "success" => "success",
            "warning" or "error" => "warning",
            _ => "info",
        };

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static int SafeCountFiles(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).Take(1001).Count() : 0; }
        catch { return 0; }
    }

    private static int SafeCountDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Take(1001).Count() : 0; }
        catch { return 0; }
    }

    private static T? Safe<T>(Func<T?> action)
    {
        try { return action(); }
        catch { return default; }
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }

    private sealed record FeatureState(
        bool ArkRootFound,
        bool ArkExecutableFound,
        bool SavedConfigFound,
        bool ContentFound,
        bool MoviesFound,
        bool GameRunning,
        bool IsAdministrator,
        bool FfmpegInstalled,
        bool AutoClickerRunning,
        int AutoClickCount,
        string? CurrentResolution,
        bool StretchedPending,
        bool PreferencesReadable,
        int CustomScriptHotkeys,
        bool NotifierConfigured,
        bool GammaConfigFound,
        bool HudConfigFound,
        bool ServerDataFound,
        bool LineListFound,
        int PaintingCount,
        bool CharacterPresetDirectoryFound,
        int CharacterPresetCount,
        bool WorkshopFound,
        int WorkshopModCount,
        int CrosshairPresetCount,
        int FileModifierBackups);
}

/// <summary>Bounded safe settings plus recent operation breadcrumbs from the existing activity ring.</summary>
public sealed class SettingsOperationsDiagnosticProvider(
    IPreferencesStore preferences,
    IActivityService activities,
    IOptions<AppConfiguration> options) : IDiagnosticProvider
{
    private const int MaxBreadcrumbs = 6;

    private static readonly string[] ScriptKeys =
    [
        "yuty", "autowalk", "mammoth", "astro", "autodownload", "fasttp", "takeall",
        "teksaddle", "noglin", "invsize", "antiafk", "turret", "flak", "dinoready",
        "crafting", "antidote", "fedsuit",
    ];

    public string ProviderId => "settings_operations";

    public Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loggingEnabled = Safe(() => AppDiagnostics.GetLoggingEnabled(), true);
        var verboseEnabled = Safe(() => AppDiagnostics.GetVerboseLoggingEnabled(), false);
        var logFile = Safe(() => AppDiagnostics.GetLogFilePath(), string.Empty);
        var logInfo = SafeFileInfo(logFile);
        var customHotkeys = ScriptKeys.Count(key => Safe(() => preferences.ContainsKey($"script.{key}.hotkey"), false));

        var checks = new List<DiagnosticCheck>
        {
            DiagnosticChecks.Pass("logging_enabled", "File logging enabled", loggingEnabled),
            DiagnosticChecks.Pass("verbose_logging", "Verbose logging enabled", verboseEnabled),
            logInfo.Exists
                ? DiagnosticChecks.Pass("log_file", "Current log file", $"{logInfo.SizeKiB} KiB",
                    $"Updated {logInfo.LastWriteUtc:O}")
                : DiagnosticChecks.Warning("log_file", "Current log file", "not created"),
            DiagnosticChecks.Pass("telemetry_enabled", "Anonymous telemetry enabled", options.Value.Telemetry.Enabled),
            DiagnosticChecks.Pass("admin_api_configured", "Support API configured",
                Uri.TryCreate(options.Value.AdminPanel.BaseUrl, UriKind.Absolute, out _)),
            DiagnosticChecks.Pass("discord_presence", "Discord Rich Presence enabled",
                Safe(() => preferences.Get(IDiscordPresenceService.EnabledPreferenceKey, true), true)),
            DiagnosticChecks.Pass("start_with_ark", "Start RazorReaper with ARK",
                Safe(() => preferences.Get(IArkLinkService.StartWithArkPreferenceKey, false), false)),
            DiagnosticChecks.Pass("close_with_ark", "Close RazorReaper with ARK",
                Safe(() => preferences.Get(IArkLinkService.CloseWithArkPreferenceKey, false), false)),
            DiagnosticChecks.Pass("script_hotkeys_customized", "Scripts with a saved hotkey", customHotkeys),
            DiagnosticChecks.Pass("notifier_endpoint", "Notifier endpoint configured",
                !string.IsNullOrWhiteSpace(Safe(() => preferences.Get("notifier.endpoint", string.Empty), string.Empty))),
        };

        var recent = Safe(() => activities.GetRecentActivities(), Array.Empty<RazorReaper.Models.ActivityItem>())
            .Take(MaxBreadcrumbs)
            .ToArray();
        for (var index = 0; index < recent.Length; index++)
        {
            var activity = recent[index];
            checks.Add(new DiagnosticCheck
            {
                Key = $"operation_{index + 1}",
                Label = $"Recent operation {index + 1}",
                Status = ActivityStatus(activity.Type),
                Value = ClassifyActivity(activity.Title),
                Detail = activity.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            });
        }

        var providerStatus = checks.Any(check => check.Status == "fail")
            ? "error"
            : checks.Any(check => check.Status == "warning")
                ? "warning"
                : "ok";

        return Task.FromResult(new DiagnosticProviderData(
            providerStatus,
            checks,
            $"{recent.Length} privacy-filtered recent operation(s); names, commands, paths, and server addresses are removed."));
    }

    private static string ActivityStatus(string? type)
        => type?.ToLowerInvariant() switch
        {
            "success" => "pass",
            "warning" => "warning",
            "error" => "fail",
            _ => "unknown",
        };

    internal static string ClassifyActivity(string? title)
    {
        var value = title ?? string.Empty;
        var lower = value.ToLowerInvariant();
        if (lower.Contains("desync"))
        {
            if (lower.Contains("administrator")) return "Desync failed: administrator required";
            if (lower.Contains("not running")) return "Desync failed: ARK not running";
            if (lower.Contains("executable")) return "Desync failed: executable unavailable";
            if (lower.Contains("firewall")) return "Desync failed: firewall operation";
            if (lower.Contains("limit")) return "Desync failed: usage limit";
            if (lower.Contains("activated")) return "Desync activated";
            if (lower.Contains("revert")) return "Desync reverted";
            return "Desync operation";
        }

        if (lower.Contains("autoclicker")) return "Autoclicker operation";
        if (lower.Contains("script") || lower.Contains("macro")) return "Automation operation";
        if (lower.Contains("resolution")) return "Resolution operation";
        if (lower.Contains("compact") || lower.Contains("uncompress")) return "Compact ARK operation";
        if (lower.Contains("font")) return "Font operation";
        if (lower.Contains("ini")) return "INI operation";
        if (lower.Contains("sky")) return "Sky Changer operation";
        if (lower.Contains("video") || lower.Contains("loading")) return "Loading Screen operation";
        if (lower.Contains("game") || lower.Contains("ark")) return "ARK operation";
        return "App operation";
    }

    private static (bool Exists, long SizeKiB, DateTime LastWriteUtc) SafeFileInfo(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (false, 0, default);
            var info = new FileInfo(path);
            return (true, Math.Max(0, info.Length / 1024), info.LastWriteTimeUtc);
        }
        catch { return (false, 0, default); }
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); }
        catch { return fallback; }
    }
}
