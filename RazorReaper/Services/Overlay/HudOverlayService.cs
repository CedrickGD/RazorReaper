using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using RazorReaper.Services.Automation;

namespace RazorReaper.Services.Overlay;

/// <summary>
/// Singleton owner of the HUD overlay: settings persistence, the Win32 overlay window lifetime,
/// and the ~2 Hz snapshot loop that feeds it. Keeps running when the user navigates away.
/// Other features push data in via SetActiveTool / SetServerInfo / PushAlert.
/// </summary>
public interface IHudOverlayService : IDisposable
{
    /// <summary>Fired on any settings or run/move-state change (from any source).</summary>
    event Action? Changed;

    bool IsRunning { get; }
    bool IsMoveMode { get; }

    /// <summary>Snapshot copy of the persisted settings. Mutate it, then pass to UpdateSettings.</summary>
    HudSettings Settings { get; }

    IReadOnlyList<MonitorInfo> GetMonitors();

    void Start();
    void Stop();
    void Toggle();

    /// <summary>While on, the HUD panel is hit-testable and draggable; off restores click-through.</summary>
    void SetMoveMode(bool enabled);

    /// <summary>Persist new settings and push them to the live overlay.</summary>
    void UpdateSettings(HudSettings settings);

    /// <summary>Push the user's themeable accent color (defaults to the app purple 139,92,246).</summary>
    void SetAccent(byte r, byte g, byte b);

    /// <summary>Set the "active tool" label shown by the ToolStatus module (null/empty = Idle).</summary>
    void SetActiveTool(string? label);

    /// <summary>Set the server info shown by the ServerInfo module (nulls clear individual parts).</summary>
    void SetServerInfo(string? name, int? players, int? maxPlayers, int? pingMs);

    /// <summary>
    /// Show a live countdown in the Desync module until the given UTC deadline (null = inactive).
    /// The overlay computes the remaining seconds itself each frame, so one call per activation is enough.
    /// </summary>
    void SetDesync(DateTime? revertAtUtc);

    /// <summary>Queue an alert for the Notifier module. This is the API the Notifier client calls.</summary>
    void PushAlert(HudAlert alert);
    void PushAlert(string text, HudAlertSeverity severity = HudAlertSeverity.Info);

    /// <summary>Recent alerts (newest first) from the in-memory ring buffer, for page display.</summary>
    IReadOnlyList<HudAlert> GetRecentAlerts();

    /// <summary>Push a sample alert so the user can preview placement (cycles severities).</summary>
    void TestAlert();

    /// <summary>Restart the SessionTimer module from now (defaults to app process start).</summary>
    void ResetSessionTimer();

    /// <summary>Anchor the SessionTimer to a known moment (e.g. the detected server-join time).</summary>
    void SetSessionStart(DateTime startUtc);
}

public sealed class HudOverlayService : IHudOverlayService
{
    private const int TickIntervalMs = 500;
    private const int AlertRingCapacity = 32;
    private const int AlertMaxVisible = 4;
    private static readonly TimeSpan AlertVisibleFor = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private const string GameProcessName = "ShooterGame";

    private readonly ILogger<HudOverlayService> _logger;
    private readonly IProcessService _process;
    private readonly AutomationScriptBase[] _scripts;
    private readonly string _settingsPath;

    private volatile bool _gameRunning;

    private readonly object _lock = new();
    private readonly List<HudAlert> _alerts = new();
    private HudSettings _settings;
    private HudOverlayWindow? _window;
    private Timer? _timer;
    private bool _running;
    private bool _moveMode;
    private string? _activeTool;
    private HudServerInfo _server = new(null, null, null, null);
    private DateTime? _desyncUntilUtc;
    private DateTime _sessionStartUtc;
    private int _testAlertCounter;
    private CancellationTokenSource? _saveCts;
    private bool _disposed;

    public event Action? Changed;

    public bool IsRunning { get { lock (_lock) return _running; } }
    public bool IsMoveMode { get { lock (_lock) return _moveMode; } }
    public HudSettings Settings { get { lock (_lock) return _settings.Clone(); } }

    public HudOverlayService(
        ILogger<HudOverlayService> logger,
        IProcessService process,
        IEnumerable<AutomationScriptBase> scripts)
    {
        _logger = logger;
        _process = process;
        _scripts = scripts.ToArray();
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "hud-overlay.json");

        _settings = LoadSettings();

        try
        {
            _sessionStartUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            _sessionStartUtc = DateTime.UtcNow;
        }

        // Repaint promptly when a script starts/stops so the ActiveScripts module stays live.
        // (Also instantiates every script at boot, which registers their global hotkeys.)
        foreach (var script in _scripts) script.Changed += OnScriptChanged;

        // Deliberately NO auto-restore of a previously-running HUD: the overlay starts every
        // session off and only appears when the user turns it on (page toggle or hotkey).
        // Restoring it made the HUD look "on by default" after any session that ended with
        // the HUD running — e.g. a close-with-ARK exit.
    }

    private void OnScriptChanged() => TickNow();

    // ─── Lifecycle ─────────────────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _running) return;
            _running = true;
            _settings.Enabled = true;
        }

        var window = EnsureWindow();
        ApplySettingsToWindow(window);
        window.Show();

        _timer = new Timer(OnTick, null, 0, TickIntervalMs);
        QueueSave();
        RaiseChanged();
    }

    public void Stop()
    {
        Timer? timer;
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            _moveMode = false;
            _settings.Enabled = false;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
        _window?.SetMoveMode(false);
        _window?.Hide();
        QueueSave();
        RaiseChanged();
    }

    public void Toggle()
    {
        if (IsRunning) Stop();
        else Start();
    }

    public void SetMoveMode(bool enabled)
    {
        lock (_lock)
        {
            if (!_running && enabled) return; // move mode only makes sense with a live overlay
            if (_moveMode == enabled) return;
            _moveMode = enabled;
        }
        _window?.SetMoveMode(enabled);
        RaiseChanged();
    }

    // ─── Settings ──────────────────────────────────────────────────────────────────────────

    public void UpdateSettings(HudSettings settings)
    {
        if (settings == null) return;
        var next = settings.Clone();
        next.Normalize();

        bool running;
        lock (_lock)
        {
            next.Enabled = _running; // run state is owned by Start/Stop, not the settings form
            _settings = next;
            running = _running;
        }

        if (running && _window != null)
        {
            ApplySettingsToWindow(_window);
            TickNow();
        }
        QueueSave();
        RaiseChanged();
    }

    public void SetAccent(byte r, byte g, byte b)
    {
        lock (_lock)
        {
            _settings.AccentR = r;
            _settings.AccentG = g;
            _settings.AccentB = b;
        }
        _window?.SetAccent(r, g, b);
        QueueSave();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => HudOverlayWindow.EnumerateMonitors();

    // ─── Data sources ──────────────────────────────────────────────────────────────────────

    public void SetActiveTool(string? label)
    {
        lock (_lock) _activeTool = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        TickNow();
    }

    public void SetServerInfo(string? name, int? players, int? maxPlayers, int? pingMs)
    {
        lock (_lock) _server = new HudServerInfo(
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(), players, maxPlayers, pingMs);
        TickNow();
    }

    public void SetDesync(DateTime? revertAtUtc)
    {
        lock (_lock) _desyncUntilUtc = revertAtUtc;
        TickNow();
    }

    public void PushAlert(HudAlert alert)
    {
        if (alert == null || string.IsNullOrWhiteSpace(alert.Text)) return;
        lock (_lock)
        {
            _alerts.Add(alert);
            while (_alerts.Count > AlertRingCapacity) _alerts.RemoveAt(0);
        }
        TickNow();
    }

    public void PushAlert(string text, HudAlertSeverity severity = HudAlertSeverity.Info)
        => PushAlert(new HudAlert(text, severity, DateTime.UtcNow));

    public IReadOnlyList<HudAlert> GetRecentAlerts()
    {
        lock (_lock) return _alerts.AsEnumerable().Reverse().ToList();
    }

    public void TestAlert()
    {
        var n = Interlocked.Increment(ref _testAlertCounter);
        var (text, severity) = (n % 4) switch
        {
            1 => ("Test alert — this is where alerts appear", HudAlertSeverity.Info),
            2 => ("Test alert — success", HudAlertSeverity.Success),
            3 => ("Test alert — warning", HudAlertSeverity.Warning),
            _ => ("Test alert — error", HudAlertSeverity.Error),
        };
        PushAlert(text, severity);
    }

    public void ResetSessionTimer()
    {
        lock (_lock) _sessionStartUtc = DateTime.UtcNow;
        TickNow();
    }

    public void SetSessionStart(DateTime startUtc)
    {
        if (startUtc > DateTime.UtcNow) startUtc = DateTime.UtcNow;
        lock (_lock) _sessionStartUtc = startUtc;
        TickNow();
    }

    // ─── Snapshot loop ─────────────────────────────────────────────────────────────────────

    private void OnTick(object? state)
    {
        try
        {
            // Cheap game-detection outside the lock (~1 ms); drives the ServerInfo fallback.
            try { _gameRunning = _process.IsProcessRunning(GameProcessName); }
            catch { /* leave the last known value */ }

            HudOverlayWindow? window;
            HudSnapshot snapshot;
            lock (_lock)
            {
                if (!_running || _window == null) return;
                window = _window;
                snapshot = BuildSnapshotLocked();
            }
            window.Render(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HUD snapshot tick failed");
        }
    }

    /// <summary>Render immediately instead of waiting up to 500 ms for the next tick.</summary>
    private void TickNow()
    {
        if (IsRunning) OnTick(null);
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private HudSnapshot BuildSnapshotLocked()
    {
        var elapsed = DateTime.UtcNow - _sessionStartUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var sessionText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        var cutoff = DateTime.UtcNow - AlertVisibleFor;
        var visibleAlerts = _alerts
            .Where(a => a.Timestamp >= cutoff)
            .OrderByDescending(a => a.Timestamp)
            .Take(AlertMaxVisible)
            .ToList();

        // When no server is set from the app, fall back to a best-effort session label:
        // "Single Player" while the game runs, otherwise leave it blank ("No server set").
        // (We can't tell single-player from a Steam-joined server without reading the game,
        // so a server joined outside the app still reads as "Single Player".)
        var server = _server;
        if (string.IsNullOrWhiteSpace(server.Name) && _gameRunning)
            server = new HudServerInfo("Single Player", null, null, null);

        // Script state lives in each script (volatile), not under our lock — cheap to read.
        IReadOnlyList<string> activeScripts = Array.Empty<string>();
        if (_scripts.Length > 0)
        {
            List<string>? running = null;
            foreach (var script in _scripts)
            {
                if (script.IsRunning) (running ??= new List<string>()).Add(script.DisplayName);
            }
            if (running != null) activeScripts = running;
        }

        // Remaining desync seconds straight from the deadline, so the 2 Hz loop ticks it without pushes.
        int? desyncSeconds = null;
        if (_desyncUntilUtc is DateTime until)
        {
            var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            if (remaining > 0) desyncSeconds = remaining;
        }

        return new HudSnapshot(
            TimeText: DateTime.Now.ToString("HH:mm:ss"),
            SessionText: sessionText,
            Server: server,
            ActiveTool: _activeTool,
            ActiveScripts: activeScripts,
            Alerts: visibleAlerts,
            Modules: _settings.Modules.ToList(),
            Compact: _settings.Compact,
            AlertCorner: _settings.AlertCorner,
            DesyncSeconds: desyncSeconds);
    }

    // ─── Window plumbing ───────────────────────────────────────────────────────────────────

    private HudOverlayWindow EnsureWindow()
    {
        lock (_lock)
        {
            if (_window == null)
            {
                _window = new HudOverlayWindow(_logger, OnPanelMoved);
                _window.Start();
            }
            return _window;
        }
    }

    private void ApplySettingsToWindow(HudOverlayWindow window)
    {
        HudSettings s;
        lock (_lock) s = _settings.Clone();
        window.SetMonitor(s.MonitorDeviceName);
        window.SetAnchor(s.Anchor, s.OffsetX, s.OffsetY, s.CustomX, s.CustomY);
        window.SetOpacity(s.Opacity);
        window.SetScale(s.Scale);
        window.SetAccent((byte)s.AccentR, (byte)s.AccentG, (byte)s.AccentB);
    }

    /// <summary>Move-mode drag ended: persist the new free position.</summary>
    private void OnPanelMoved(int customX, int customY)
    {
        lock (_lock)
        {
            _settings.Anchor = HudAnchor.Custom;
            _settings.CustomX = customX;
            _settings.CustomY = customY;
        }
        QueueSave();
        RaiseChanged();
    }

    // ─── Persistence ───────────────────────────────────────────────────────────────────────

    private HudSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<HudSettings>(File.ReadAllText(_settingsPath));
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load HUD settings from {Path} — using defaults", _settingsPath);
        }
        var defaults = new HudSettings();
        defaults.Normalize();
        return defaults;
    }

    /// <summary>Debounced save — drag-end and slider bursts coalesce into one disk write.</summary>
    private void QueueSave()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            cts = _saveCts;
        }
        _ = Task.Delay(SaveDebounce, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SaveNow();
        }, TaskScheduler.Default);
    }

    private void SaveNow()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_settings, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save HUD settings to {Path}", _settingsPath);
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "HUD Changed subscriber threw"); }
    }

    // ─── Disposal ──────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        HudOverlayWindow? window;
        Timer? timer;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            timer = _timer;
            _timer = null;
            window = _window;
            _window = null;
            _saveCts?.Cancel();
            _saveCts = null;
        }

        foreach (var script in _scripts) script.Changed -= OnScriptChanged;
        timer?.Dispose();
        SaveNow(); // flush any pending debounced write so the last edits aren't lost on exit
        window?.Dispose();
    }
}
