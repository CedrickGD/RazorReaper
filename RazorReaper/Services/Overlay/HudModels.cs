namespace RazorReaper.Services.Overlay;

/// <summary>The kinds of information the HUD can render.</summary>
public enum HudModuleKind
{
    Clock,
    SessionTimer,
    ServerInfo,
    Notifier,
    ToolStatus,
    // Appended last: settings JSON persists these as numbers, so existing values must not shift.
    ActiveScripts,
    Desync
}

/// <summary>Screen placement for the HUD panel and the alert stack.</summary>
public enum HudAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    /// <summary>Free position (monitor-relative CustomX/CustomY, set by dragging in move mode).</summary>
    Custom
}

/// <summary>Severity of a notifier alert — drives the status color of its left edge.</summary>
public enum HudAlertSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>One toggleable HUD module. Order controls the vertical position inside the panel.</summary>
public sealed record HudModule(HudModuleKind Id, string Title, bool Enabled, int Order);

/// <summary>A single notifier alert line. Timestamp is UTC; the service ages alerts out of view.</summary>
public sealed record HudAlert(string Text, HudAlertSeverity Severity, DateTime Timestamp);

/// <summary>Last-known server info shown by the ServerInfo module. All parts optional.</summary>
public sealed record HudServerInfo(string? Name, int? Players, int? MaxPlayers, int? PingMs);

/// <summary>
/// Immutable per-frame render input. The service builds one ~2×/sec from its data sources and
/// hands it to the overlay window; the window never reaches back into services.
/// </summary>
public sealed record HudSnapshot(
    string TimeText,
    string SessionText,
    HudServerInfo Server,
    string? ActiveTool,
    IReadOnlyList<string> ActiveScripts,
    IReadOnlyList<HudAlert> Alerts,
    IReadOnlyList<HudModule> Modules,
    bool Compact,
    HudAnchor AlertCorner,
    int? DesyncSeconds);

/// <summary>
/// Persisted HUD configuration (JSON at %LOCALAPPDATA%\RazorReaper\hud-overlay.json).
/// Mutable on purpose so pages can bind, then push the whole object back via UpdateSettings.
/// </summary>
public sealed class HudSettings
{
    /// <summary>Whether the overlay should be running (restored on next app start).</summary>
    public bool Enabled { get; set; }

    public HudAnchor Anchor { get; set; } = HudAnchor.TopRight;

    /// <summary>Margin from the anchored corner, in pixels (ignored for Custom anchor).</summary>
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 16;

    /// <summary>Monitor-relative panel top-left when Anchor is Custom (set by move-mode dragging).</summary>
    public int CustomX { get; set; }
    public int CustomY { get; set; }

    /// <summary>Overall overlay opacity, 0.2–1.0.</summary>
    public double Opacity { get; set; } = 0.95;

    /// <summary>Render scale, 0.5–2.0.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Collapse all modules into one condensed line.</summary>
    public bool Compact { get; set; }

    /// <summary>Corner where notifier alerts stack (Custom falls back to BottomRight).</summary>
    public HudAnchor AlertCorner { get; set; } = HudAnchor.BottomRight;

    /// <summary>Target monitor device name (e.g. \\.\DISPLAY1); empty = primary.</summary>
    public string MonitorDeviceName { get; set; } = "";

    /// <summary>Themeable accent pushed from the app theme. Defaults to the app purple.</summary>
    public int AccentR { get; set; } = 139;
    public int AccentG { get; set; } = 92;
    public int AccentB { get; set; } = 246;

    public List<HudModule> Modules { get; set; } = DefaultModules();

    public static List<HudModule> DefaultModules() => new()
    {
        new HudModule(HudModuleKind.Clock, "Time", true, 0),
        new HudModule(HudModuleKind.SessionTimer, "Session", true, 1),
        new HudModule(HudModuleKind.ServerInfo, "Server", true, 2),
        new HudModule(HudModuleKind.ToolStatus, "Tool", true, 3),
        new HudModule(HudModuleKind.ActiveScripts, "Scripts", true, 4),
        new HudModule(HudModuleKind.Notifier, "Alerts", true, 5),
        new HudModule(HudModuleKind.Desync, "Desync", true, 6),
    };

    /// <summary>Clamp ranges and make sure every module kind exists exactly once (survives old JSON).</summary>
    public void Normalize()
    {
        Opacity = Math.Clamp(Opacity, 0.2, 1.0);
        Scale = Math.Clamp(Scale, 0.5, 2.0);
        AccentR = Math.Clamp(AccentR, 0, 255);
        AccentG = Math.Clamp(AccentG, 0, 255);
        AccentB = Math.Clamp(AccentB, 0, 255);
        if (AlertCorner == HudAnchor.Custom) AlertCorner = HudAnchor.BottomRight;

        var seen = new HashSet<HudModuleKind>();
        var cleaned = new List<HudModule>();
        Modules ??= new List<HudModule>();
        foreach (var m in Modules)
        {
            if (m != null && seen.Add(m.Id)) cleaned.Add(m);
        }
        foreach (var def in DefaultModules())
        {
            if (seen.Add(def.Id)) cleaned.Add(def);
        }
        Modules = cleaned.OrderBy(m => m.Order).ToList();
    }

    public HudSettings Clone() => new()
    {
        Enabled = Enabled,
        Anchor = Anchor,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        CustomX = CustomX,
        CustomY = CustomY,
        Opacity = Opacity,
        Scale = Scale,
        Compact = Compact,
        AlertCorner = AlertCorner,
        MonitorDeviceName = MonitorDeviceName,
        AccentR = AccentR,
        AccentG = AccentG,
        AccentB = AccentB,
        Modules = Modules.ToList(),
    };
}
