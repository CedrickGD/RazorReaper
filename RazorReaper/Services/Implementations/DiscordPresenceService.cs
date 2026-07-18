using DiscordRPC;
using Microsoft.Extensions.Logging;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Publishes Discord Rich Presence over the local Discord IPC pipe. Mirrors the
/// <see cref="AutoUpdateManager"/> toggle pattern: the on/off state lives in MAUI
/// <see cref="Preferences"/> and a <see cref="StateChanged"/> event lets the UI refresh.
/// Every path is fully guarded — a missing Discord client, a closed Discord app, or an
/// unset Application ID must never surface an error to the rest of the app.
/// </summary>
public sealed class DiscordPresenceService : IDiscordPresenceService
{
    // Public Discord "Application ID" (a.k.a. Client ID) — NOT a secret, safe to ship in the
    // binary. Reuses the existing "RazorReaper" bot application: Rich Presence only needs the
    // public ID (never the bot token), and the application's name is what Discord shows as the
    // activity ("RazorReaper"). Reset to a non-digit placeholder to disable RPC entirely.
    private const string ApplicationId = "1487518169164812339";

    private const string PrefKeyEnabled = IDiscordPresenceService.EnabledPreferenceKey;
    private const string PrefKeyConnectedUser = IDiscordPresenceService.ConnectedUserPreferenceKey;

    // Asset key uploaded under Rich Presence → Art Assets in the Developer Portal
    // (the RRlogo.png art asset; Discord derived the key from the filename).
    private const string LargeImageKey = "rrlogo";

    // /releases/latest always redirects to the newest release, so the "Download" button
    // tracks new versions with zero maintenance.
    private const string ReleasesLatestUrl = "https://github.com/CedrickGD/RazorReaper/releases/latest";
    private const string RepoUrl = "https://github.com/CedrickGD/RazorReaper";

    private const string DefaultLabel = "Main Menu";
    private const string FallbackLabel = "Browsing";
    private const string TrayLabel = "Idle in tray";

    // Friendly tool names keyed by the first path segment. Mirrors SharedNavbar.NavGroups —
    // keep in sync when routes are added/renamed. Sub-routes (e.g. "vision/scope",
    // "custom-lab/settings") resolve via their first segment, so they need no extra entries.
    private static readonly IReadOnlyDictionary<string, string> ToolLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = DefaultLabel,
            ["server"] = "Server",
            ["game"] = "Game",
            ["ini-changer"] = "INI Changer",
            ["ini-builder"] = "INI Builder",
            ["vision"] = "Vision Tools",
            ["launch-options"] = "Launch Options",
            ["fonts"] = "Fonts",
            ["pixel"] = "Pixel Glitch",
            ["paintings"] = "Paintings",
            ["custom-lab"] = "Sky Changer",
            ["loading-screen"] = "Loading Screen",
            ["char-manager"] = "Char Manager",
            ["dino-prices"] = "Mutagen Prices",
            ["line-list"] = "Line List",
            ["oc-bps"] = "OC BPs",
            ["bosses"] = "Bosses",
            ["tp-locations"] = "TP Locations",
            ["underwater-drops"] = "Underwater Drops",
            ["map-mods"] = "Map Mods",
            ["steam-mods"] = "Steam Mods",
            ["building"] = "Building",
            ["autoclicker"] = "Auto Clicker",
            ["crosshair"] = "Crosshair",
            ["crafting-scripts"] = "Macro / AHK",
            ["compact-ark"] = "Compact ARK",
            ["troubleshoot"] = "Troubleshoot",
            ["credits"] = "Credits",
        };

    private static bool HasValidAppId =>
        ApplicationId.Length is >= 17 and <= 20 && ApplicationId.All(char.IsDigit);

    private readonly IUpdateService updateService;
    private readonly ITelemetryService telemetryService;
    private readonly ILogger<DiscordPresenceService> logger;
    private readonly object gate = new();

    private DiscordRpcClient? client;
    private DateTime sessionStartUtc;
    private string currentLabel = DefaultLabel;
    private bool minimizedToTray;

    public DiscordPresenceService(IUpdateService updateService, ITelemetryService telemetryService, ILogger<DiscordPresenceService> logger)
    {
        this.updateService = updateService;
        this.telemetryService = telemetryService;
        this.logger = logger;
    }

    public event Action? StateChanged;

    public bool IsEnabled
    {
        get => Preferences.Get(PrefKeyEnabled, true);
        set
        {
            var changed = value != IsEnabled;
            Preferences.Set(PrefKeyEnabled, value);

            // Reflect the toggle on the live connection immediately.
            if (value)
                Initialize();
            else
                Shutdown();

            if (changed)
            {
                _ = telemetryService.TrackEventAsync(
                    "discord_rpc_toggle",
                    TelemetryEventStatus.Ok,
                    metrics: new Dictionary<string, object?>
                    {
                        ["enabled"] = value
                    });
            }

            OnStateChanged();
        }
    }

    public void Initialize() => ApplyIfLive();

    public void SetActivityForPath(string relativePath) => SetActivityLabel(ResolveLabel(relativePath));

    public void SetActivityLabel(string label)
    {
        // Always remember where the user is, even while RPC is off — so toggling it on
        // (or a later connect) immediately shows the right tool.
        currentLabel = string.IsNullOrWhiteSpace(label) ? DefaultLabel : label;
        ApplyIfLive();
    }

    public void SetMinimizedToTray(bool minimized)
    {
        minimizedToTray = minimized;
        ApplyIfLive();
    }

    // Connect (if needed) and publish the current presence — but only when RPC is enabled and a
    // real Application ID is configured. The single funnel for Initialize / navigation / tray.
    private void ApplyIfLive()
    {
        if (!IsEnabled || !HasValidAppId)
            return;

        lock (gate)
        {
            EnsureClientLocked();
            ApplyPresenceLocked();
        }
    }

    public void Shutdown()
    {
        lock (gate)
        {
            if (client is null)
                return;

            try
            {
                client.ClearPresence();
                client.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during Discord presence shutdown.");
            }
            finally
            {
                client = null;
            }
        }
    }

    // --- internals (all callers hold `gate`) ---

    private void EnsureClientLocked()
    {
        if (client is not null)
            return;

        try
        {
            // Anchor the elapsed timer to the first connect of this app session so it does
            // not reset on every navigation or on a toggle off/on.
            if (sessionStartUtc == default)
                sessionStartUtc = DateTime.UtcNow;

            var created = new DiscordRpcClient(ApplicationId);

            // Remember which Discord account the local client is signed in as, so telemetry
            // can attribute sessions. Fires on every (re)connect, overwriting the stored name.
            created.OnReady += (_, e) => RecordConnectedUser(e.User);

            // Connects in the background and auto-reconnects; returns false (without throwing)
            // when Discord isn't running, and silently attaches once it launches.
            created.Initialize();
            client = created;
            logger.LogInformation("Discord Rich Presence client initialized.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord Rich Presence failed to initialize.");
            client = null;
        }
    }

    private void ApplyPresenceLocked()
    {
        if (client is null)
            return;

        try
        {
            var version = updateService.CurrentVersionLabel;

            client.SetPresence(new RichPresence
            {
                Details = minimizedToTray ? TrayLabel : currentLabel,
                State = $"v{version} · ARK QoL Tool",
                Timestamps = new Timestamps(sessionStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = LargeImageKey,
                    LargeImageText = $"RazorReaper v{version}",
                },
                // Fully qualified: MAUI's global usings also expose Microsoft.Maui.Controls.Button.
                Buttons = new[]
                {
                    new DiscordRPC.Button { Label = "⬇ Download", Url = ReleasesLatestUrl },
                    new DiscordRPC.Button { Label = "⭐ GitHub", Url = RepoUrl },
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update Discord presence.");
        }
    }

    // Runs on the RPC client's message thread. Deliberately never clears the value on
    // disable/shutdown — the last known name still identifies the user while RPC is off.
    private void RecordConnectedUser(User? user)
    {
        try
        {
            var name = user?.Username;
            if (string.IsNullOrWhiteSpace(name))
                name = user?.ToString();

            if (string.IsNullOrWhiteSpace(name))
                return;

            Preferences.Set(PrefKeyConnectedUser, name);
            logger.LogInformation("Discord Rich Presence connected as {User}.", name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record the connected Discord user.");
        }
    }

    private static string ResolveLabel(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return DefaultLabel;

        var path = relativePath.Split('?', '#')[0].Trim('/');
        if (path.Length == 0)
            return DefaultLabel;

        var key = path.Split('/')[0];
        return ToolLabels.TryGetValue(key, out var label) ? label : FallbackLabel;
    }

    private void OnStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // A UI callback failure must not break presence handling.
        }
    }
}
