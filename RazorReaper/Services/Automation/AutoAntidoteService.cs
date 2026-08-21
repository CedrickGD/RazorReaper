using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;
using RazorReaper.Configuration;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>Which HUD change fires the antidote burst.</summary>
public enum AutoAntidoteTriggerMode
{
    /// <summary>Fire when the calibrated icon appears (e.g. a debuff icon showing up).</summary>
    IconAppears,
    /// <summary>Fire when the calibrated icon disappears (e.g. a buff icon expiring).</summary>
    IconDisappears,
    /// <summary>
    /// Read the remaining seconds out of the calibrated region with OCR and fire once the timer
    /// drops to/below the threshold — precise refreshing instead of inferring from the icon.
    /// </summary>
    TimerBelow
}

/// <summary>Lifecycle state of the Auto Antidote watcher.</summary>
public enum AutoAntidoteState
{
    /// <summary>Not scanning.</summary>
    Off,
    /// <summary>Scanning the calibrated region on an interval.</summary>
    Watching,
    /// <summary>A trigger just fired; the burst is running or the cooldown is counting down.</summary>
    Cooldown
}

/// <summary>User-tunable Auto Antidote options, persisted under "antidote.*" Preferences.</summary>
public sealed class AutoAntidoteSettings
{
    /// <summary>Which icon transition fires the burst.</summary>
    public AutoAntidoteTriggerMode Mode { get; set; } = AutoAntidoteTriggerMode.IconAppears;
    /// <summary>Milliseconds between screen scans.</summary>
    public int ScanIntervalMs { get; set; } = 1000;
    /// <summary>Similarity (0–100) at or above which the region counts as "icon visible".</summary>
    public double MatchThresholdPercent { get; set; } = 92;
    /// <summary>Display name of the key pressed by the burst (parsed to a virtual key at run time).</summary>
    public string BurstKey { get; set; } = "5";
    /// <summary>How many times the burst presses the key.</summary>
    public int BurstPresses { get; set; } = 1;
    /// <summary>Milliseconds between presses inside one burst.</summary>
    public int BurstDelayMs { get; set; } = 250;
    /// <summary>Seconds after a trigger during which no new trigger can fire.</summary>
    public int CooldownSeconds { get; set; } = 5;
    /// <summary>System-wide start/stop hotkey, e.g. "Alt + A".</summary>
    public string ToggleHotkey { get; set; } = "Alt + A";
    /// <summary>Seconds remaining at/below which <see cref="AutoAntidoteTriggerMode.TimerBelow"/> fires.</summary>
    public int TimerThresholdSeconds { get; set; } = 10;
}

/// <summary>
/// Watches a user-calibrated HUD region for the antidote/debuff icon and refreshes it with a
/// timed key burst. Detection is pure screen capture (GDI BitBlt via <see cref="IScreenSampler"/>)
/// compared against a reference snapshot the user takes with the icon visible — no game assets,
/// no memory reads. A singleton: it keeps watching when the user navigates away from the page and
/// always hard-stops via its system-wide toggle hotkey, even while ARK has focus.
/// </summary>
public interface IAutoAntidoteService : IDisposable
{
    /// <summary>Current watcher state.</summary>
    AutoAntidoteState State { get; }
    /// <summary>How many times the watcher has triggered since app start.</summary>
    int TriggerCount { get; }
    /// <summary>When the watcher last triggered, or null if it hasn't yet.</summary>
    DateTime? LastTriggerAt { get; }
    /// <summary>Similarity (0–100) of the last scan against the reference, or null when not scanning.</summary>
    double? LastMatchPercent { get; }
    /// <summary>True when an icon region is calibrated for the current resolution.</summary>
    bool HasRegion { get; }
    /// <summary>True when a reference snapshot exists (captured this app session).</summary>
    bool HasReference { get; }
    /// <summary>Human-readable summary of the calibrated region, or "" when none.</summary>
    string RegionSummary { get; }
    /// <summary>Live settings instance. Mutate, then call <see cref="SaveSettings"/>.</summary>
    AutoAntidoteSettings Settings { get; }

    /// <summary>Raised whenever state, counters, or the live match value change. May fire on a background thread.</summary>
    event Action? Changed;

    /// <summary>Starts watching. Returns false when calibration prerequisites are missing.</summary>
    bool Start();
    /// <summary>Hard-stops the watcher and any in-flight burst. Safe from hotkey callbacks.</summary>
    void Stop();
    /// <summary>Toggles between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    void Toggle();
    /// <summary>Runs the two-corner countdown capture for the icon region (stops watching first).</summary>
    Task<bool> CaptureRegionAsync(IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Snapshots the calibrated region right now (icon must be visible in-game) as the reference.</summary>
    bool CaptureReference();
    /// <summary>Discards the reference snapshot (stops watching first).</summary>
    void ClearReference();
    /// <summary>Clamps, persists, and applies the current settings (re-registers the toggle hotkey when it changed).</summary>
    void SaveSettings();
}

/// <summary>Default <see cref="IAutoAntidoteService"/> implementation.</summary>
public sealed class AutoAntidoteService : IAutoAntidoteService
{
    private const string RegionName = "antidote-icon";
    private const string ReferenceKey = "antidote-icon";
    private const string RunnerName = "auto-antidote";
    private const string DefaultToggleHotkey = "Alt + A";
    private const int DefaultBurstVk = 0x35; // '5'
    private const int CountdownSeconds = 3;
    private const int MinScanIntervalMs = 100;
    private const int MaxScanIntervalMs = 10000;
    private const int MaxBurstPresses = 20;
    private const int MaxBurstDelayMs = 5000;
    private const int MaxCooldownSeconds = 600;

    private readonly IScreenSampler _sampler;
    private readonly IScreenOcr _ocr;
    private readonly ICalibrationService _calibration;
    private readonly IMacroEngine _macros;
    private readonly IAutomationHotkeyService _hotkeys;
    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;
    private readonly ILogger<AutoAntidoteService> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile AutoAntidoteState _state = AutoAntidoteState.Off;
    private ScreenCapture? _reference;
    private double? _lastMatchPercent;
    private double? _lastTimerSeconds;
    private int _triggerCount;
    private DateTime? _lastTriggerAt;
    private int _hotkeyId;
    private string? _registeredHotkeyText;
    private volatile bool _disposed;

    // Foreground-check cache: ARK pids, refreshed at most every 5s (loop thread only).
    private HashSet<uint> _gamePids = new();
    private long _pidsRefreshedAt = long.MinValue;

    public AutoAntidoteService(
        IScreenSampler sampler,
        IScreenOcr ocr,
        ICalibrationService calibration,
        IMacroEngine macros,
        IAutomationHotkeyService hotkeys,
        IProcessService process,
        IOptions<AppConfiguration> config,
        INotificationService notifications,
        IActivityService activity,
        ILogger<AutoAntidoteService> logger)
    {
        _sampler = sampler;
        _ocr = ocr;
        _calibration = calibration;
        _macros = macros;
        _hotkeys = hotkeys;
        _process = process;
        _config = config;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;

        LoadSettings();
        try
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto Antidote toggle hotkey setup failed");
        }
    }

    public AutoAntidoteState State => _state;
    public int TriggerCount => _triggerCount;
    public DateTime? LastTriggerAt => _lastTriggerAt;
    public double? LastMatchPercent => _lastMatchPercent;
    public bool HasRegion => _calibration.HasRegion(RegionName);
    public bool HasReference => _reference is { IsEmpty: false };
    public AutoAntidoteSettings Settings { get; } = new();

    public string RegionSummary
        => _calibration.TryGetRegion(RegionName, out Rectangle r)
            ? $"{r.Width}x{r.Height} px at {r.X}, {r.Y}"
            : "";

    public event Action? Changed;

    // ─── Start / stop ──────────────────────────────────────────────────────────

    public bool Start()
    {
        if (_disposed) return false;
        if (!HasRegion)
        {
            _notifications.ShowWarning("Capture the HUD icon region first (calibration step 1).");
            return false;
        }
        if (!HasReference)
        {
            _notifications.ShowWarning("Capture a reference snapshot with the icon visible (calibration step 2).");
            return false;
        }

        lock (_gate)
        {
            if (_state != AutoAntidoteState.Off) return true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _state = AutoAntidoteState.Watching;
            _loopTask = Task.Run(() => ScanLoopAsync(token));
        }

        _notifications.ShowSuccess("Auto Antidote is watching.");
        TryActivity("Auto Antidote started", "success");
        RaiseChanged();
        return true;
    }

    public void Stop() => StopCore(notify: true);

    public void Toggle()
    {
        if (_state == AutoAntidoteState.Off) Start();
        else Stop();
    }

    private void StopCore(bool notify)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state == AutoAntidoteState.Off) return;
            _state = AutoAntidoteState.Off;
            cts = _cts;
            _cts = null;
        }

        // Cancel first (wakes any in-flight delay), then stop the burst runner so a hard stop
        // interrupts key presses mid-burst even while ARK has focus. The CTS is deliberately
        // not disposed here — the loop may still be observing the token on its way out.
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        try { _macros.GetRunner(RunnerName).Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto Antidote burst runner stop failed"); }

        _lastMatchPercent = null;
        if (notify)
        {
            _notifications.ShowInfo("Auto Antidote stopped.");
            TryActivity("Auto Antidote stopped", "info");
        }
        RaiseChanged();
    }

    // ─── Calibration ───────────────────────────────────────────────────────────

    public async Task<bool> CaptureRegionAsync(IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (_state != AutoAntidoteState.Off) Stop();

            var region = await _calibration.CaptureRegionAsync(RegionName, CountdownSeconds, progress, ct);
            if (region is null) return false;

            // The region moved — any old snapshot now shows different pixels.
            ClearReferenceCore();
            _notifications.ShowInfo("Region updated — capture a new reference snapshot with the icon visible.");
            RaiseChanged();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto Antidote region capture failed");
            _notifications.ShowError("Failed to capture the icon region.");
            return false;
        }
    }

    public bool CaptureReference()
    {
        try
        {
            if (!_calibration.TryGetRegion(RegionName, out Rectangle region))
            {
                _notifications.ShowWarning("Capture the HUD icon region first (calibration step 1).");
                return false;
            }

            var capture = _sampler.CaptureRegion(region);
            if (capture.IsEmpty)
            {
                _notifications.ShowError("Could not capture the reference snapshot.");
                return false;
            }

            _reference = capture;
            _sampler.CaptureReference(ReferenceKey, region);
            _lastMatchPercent = null;
            _notifications.ShowSuccess("Reference snapshot captured.");
            TryActivity("Auto Antidote reference snapshot captured", "success");
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto Antidote reference capture failed");
            _notifications.ShowError("Failed to capture the reference snapshot.");
            return false;
        }
    }

    public void ClearReference()
    {
        if (_state != AutoAntidoteState.Off) Stop();
        ClearReferenceCore();
        _notifications.ShowInfo("Reference snapshot cleared.");
        RaiseChanged();
    }

    private void ClearReferenceCore()
    {
        _reference = null;
        _lastMatchPercent = null;
        try { _sampler.ClearReference(ReferenceKey); }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto Antidote reference clear failed"); }
    }

    // ─── Settings ──────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            var mode = Preferences.Get("antidote.mode", (int)AutoAntidoteTriggerMode.IconAppears);
            Settings.Mode = Enum.IsDefined(typeof(AutoAntidoteTriggerMode), mode)
                ? (AutoAntidoteTriggerMode)mode
                : AutoAntidoteTriggerMode.IconAppears;
            Settings.ScanIntervalMs = Preferences.Get("antidote.scaninterval", 1000);
            Settings.MatchThresholdPercent = Preferences.Get("antidote.threshold", 92.0);
            Settings.BurstKey = Preferences.Get("antidote.burstkey", "5");
            Settings.BurstPresses = Preferences.Get("antidote.burstpresses", 1);
            Settings.BurstDelayMs = Preferences.Get("antidote.burstdelay", 250);
            Settings.CooldownSeconds = Preferences.Get("antidote.cooldown", 5);
            Settings.ToggleHotkey = Preferences.Get("antidote.hotkey", DefaultToggleHotkey);
            Settings.TimerThresholdSeconds = Preferences.Get("antidote.timerthreshold", 10);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto Antidote LoadSettings failed — using defaults");
        }
        ClampSettings();
    }

    public void SaveSettings()
    {
        ClampSettings();

        if (!TryParseKey(Settings.BurstKey, out _))
        {
            Settings.BurstKey = "5";
            _notifications.ShowWarning("That key can't be used for the burst — reset to 5.");
        }

        try
        {
            Preferences.Set("antidote.mode", (int)Settings.Mode);
            Preferences.Set("antidote.scaninterval", Settings.ScanIntervalMs);
            Preferences.Set("antidote.threshold", Settings.MatchThresholdPercent);
            Preferences.Set("antidote.burstkey", Settings.BurstKey);
            Preferences.Set("antidote.burstpresses", Settings.BurstPresses);
            Preferences.Set("antidote.burstdelay", Settings.BurstDelayMs);
            Preferences.Set("antidote.cooldown", Settings.CooldownSeconds);
            Preferences.Set("antidote.hotkey", Settings.ToggleHotkey);
            Preferences.Set("antidote.timerthreshold", Settings.TimerThresholdSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto Antidote SaveSettings failed");
        }

        try
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto Antidote toggle hotkey update failed");
        }
        RaiseChanged();
    }

    private void ClampSettings()
    {
        Settings.ScanIntervalMs = Math.Clamp(Settings.ScanIntervalMs, MinScanIntervalMs, MaxScanIntervalMs);
        Settings.MatchThresholdPercent = Math.Clamp(Settings.MatchThresholdPercent, 50, 100);
        Settings.BurstPresses = Math.Clamp(Settings.BurstPresses, 1, MaxBurstPresses);
        Settings.BurstDelayMs = Math.Clamp(Settings.BurstDelayMs, 0, MaxBurstDelayMs);
        Settings.CooldownSeconds = Math.Clamp(Settings.CooldownSeconds, 0, MaxCooldownSeconds);
        Settings.TimerThresholdSeconds = Math.Clamp(Settings.TimerThresholdSeconds, 1, 600);
        if (string.IsNullOrWhiteSpace(Settings.BurstKey)) Settings.BurstKey = "5";
        if (string.IsNullOrWhiteSpace(Settings.ToggleHotkey)) Settings.ToggleHotkey = DefaultToggleHotkey;
    }

    private void ApplyToggleHotkey(bool notifyOnFailure)
    {
        var text = Settings.ToggleHotkey;
        if (_hotkeyId > 0 && string.Equals(text, _registeredHotkeyText, StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryParseHotkey(text, out var vk, out var ctrl, out var alt, out var shift))
        {
            if (notifyOnFailure)
                _notifications.ShowWarning("That combination can't be used as a toggle hotkey — keeping the previous one.");
            Settings.ToggleHotkey = _registeredHotkeyText ?? DefaultToggleHotkey;
            return;
        }

        if (_hotkeyId > 0)
        {
            _hotkeys.UnregisterHotkey(_hotkeyId);
            _hotkeyId = 0;
            _registeredHotkeyText = null;
        }

        var id = _hotkeys.RegisterHotkey(vk, ctrl, alt, shift, Toggle);
        if (id > 0)
        {
            _hotkeyId = id;
            _registeredHotkeyText = text;
        }
        else
        {
            _logger.LogWarning("Auto Antidote toggle hotkey registration failed for '{Hotkey}'", text);
            if (notifyOnFailure)
                _notifications.ShowWarning($"Could not register {text} — the combination may be in use by another app.");
        }
    }

    // ─── Scan loop ─────────────────────────────────────────────────────────────

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        // Presence at the previous scan; null = unknown (just started, game not foreground,
        // or right after a trigger). Priming with the current state instead of assuming one
        // prevents an instant trigger the moment watching starts.
        bool? previousPresent = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_calibration.TryGetRegion(RegionName, out Rectangle region))
                {
                    StopCore(notify: false);
                    _notifications.ShowWarning("Auto Antidote stopped — no calibrated region for the current resolution.");
                    TryActivity("Auto Antidote stopped (region missing)", "warning");
                    return;
                }

                // OCR mode: read the remaining seconds straight off the HUD and refresh just before
                // it runs out — more precise than inferring from the icon appearing/disappearing.
                if (Settings.Mode == AutoAntidoteTriggerMode.TimerBelow)
                {
                    if (IsGameForeground())
                    {
                        var seconds = await _ocr.ReadSecondsAsync(region);
                        _lastTimerSeconds = seconds;
                        if (seconds is double s && s <= Math.Clamp(Settings.TimerThresholdSeconds, 1, 600))
                        {
                            await HandleTriggerAsync(ct);
                            RaiseChanged();
                            continue;
                        }
                    }
                    else
                    {
                        _lastTimerSeconds = null;
                    }

                    RaiseChanged();
                    await Task.Delay(Math.Clamp(Settings.ScanIntervalMs, MinScanIntervalMs, MaxScanIntervalMs), ct);
                    continue;
                }

                var reference = _reference;
                var current = _sampler.CaptureRegion(region);
                var similarity = reference is null ? null : ComputeSimilarity(reference, current);
                _lastMatchPercent = similarity;

                if (similarity is null)
                {
                    previousPresent = null;
                }
                else
                {
                    var present = similarity.Value >= Math.Clamp(Settings.MatchThresholdPercent, 50, 100);

                    // Only evaluate triggers while ARK is the foreground window — when another
                    // window covers the HUD region the pixels are meaningless and a "changed"
                    // reading would fire a false burst.
                    if (IsGameForeground())
                    {
                        if (previousPresent.HasValue && ShouldTrigger(previousPresent.Value, present))
                        {
                            await HandleTriggerAsync(ct);
                            previousPresent = null; // re-prime with fresh state after the cooldown
                            RaiseChanged();
                            continue;
                        }
                        previousPresent = present;
                    }
                    else
                    {
                        previousPresent = null;
                    }
                }

                RaiseChanged();
                await Task.Delay(Math.Clamp(Settings.ScanIntervalMs, MinScanIntervalMs, MaxScanIntervalMs), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto Antidote scan loop error");
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private bool ShouldTrigger(bool wasPresent, bool isPresent) => Settings.Mode switch
    {
        AutoAntidoteTriggerMode.IconAppears => !wasPresent && isPresent,
        AutoAntidoteTriggerMode.IconDisappears => wasPresent && !isPresent,
        _ => false
    };

    private async Task HandleTriggerAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_state != AutoAntidoteState.Watching) return;
            _state = AutoAntidoteState.Cooldown;
        }

        _triggerCount++;
        _lastTriggerAt = DateTime.Now;
        RaiseChanged();
        TryActivity($"Auto Antidote triggered (#{_triggerCount})", "info");

        var presses = Math.Clamp(Settings.BurstPresses, 1, MaxBurstPresses);
        var delay = Math.Clamp(Settings.BurstDelayMs, 0, MaxBurstDelayMs);
        var vk = TryParseKey(Settings.BurstKey, out var parsed) ? parsed : DefaultBurstVk;

        var steps = new List<MacroStep> { MacroStep.FocusGameWindow() };
        for (var i = 0; i < presses; i++)
        {
            steps.Add(MacroStep.KeyPress(vk));
            if (delay > 0 && i < presses - 1)
                steps.Add(MacroStep.Delay(delay));
        }

        var completed = await _macros.GetRunner(RunnerName).RunAsync(new MacroSequence
        {
            Name = "Auto Antidote burst",
            Steps = steps,
            RepeatCount = 1
        }, ct);

        if (!completed && !ct.IsCancellationRequested)
            TryActivity("Auto Antidote burst did not complete (game window unavailable?)", "warning");

        var cooldown = Math.Clamp(Settings.CooldownSeconds, 0, MaxCooldownSeconds);
        if (cooldown > 0)
            await Task.Delay(cooldown * 1000, ct);

        lock (_gate)
        {
            if (_state == AutoAntidoteState.Cooldown)
                _state = AutoAntidoteState.Watching;
        }
    }

    /// <summary>Mean per-channel similarity (0–100, alpha ignored); null on size mismatch or empty capture.</summary>
    private static double? ComputeSimilarity(ScreenCapture reference, ScreenCapture current)
    {
        if (reference.IsEmpty || current.IsEmpty) return null;
        if (reference.Width != current.Width || reference.Height != current.Height) return null;

        var a = reference.Bgra;
        var b = current.Bgra;
        long diffSum = 0;
        var pixels = current.Width * current.Height;
        for (var i = 0; i < pixels * 4; i += 4)
        {
            diffSum += Math.Abs(a[i] - b[i]);         // B
            diffSum += Math.Abs(a[i + 1] - b[i + 1]); // G
            diffSum += Math.Abs(a[i + 2] - b[i + 2]); // R
        }
        var meanDiff = diffSum / (double)(pixels * 3);
        return Math.Clamp(100.0 * (1.0 - meanDiff / 255.0), 0.0, 100.0);
    }

    private bool IsGameForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);

            var now = Environment.TickCount64;
            // NB: `now - _pidsRefreshedAt` with the field seeded at long.MinValue overflows into a
            // negative number, so the refresh never ran, the pid set stayed empty and this returned
            // false forever — the watcher scanned but could never trigger. Same bug ForegroundGate
            // had; this second copy was missed when that one was fixed.
            if (_pidsRefreshedAt == long.MinValue || now - _pidsRefreshedAt > 5000)
            {
                _pidsRefreshedAt = now;
                var processes = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
                try
                {
                    _gamePids = processes.Select(p => (uint)p.Id).ToHashSet();
                }
                finally
                {
                    foreach (var p in processes) p?.Dispose();
                }
            }
            return _gamePids.Contains(pid);
        }
        catch
        {
            return false;
        }
    }

    // ─── Key parsing ───────────────────────────────────────────────────────────

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Tab"] = 0x09,
        ["Backspace"] = 0x08,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E
    };

    private static bool IsModifierName(string token) => token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Control", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Shift", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Win", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Meta", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a HotkeyField-style label ("5", "F", "F6", "Space", or "Ctrl + 5" — modifiers
    /// ignored) into a virtual-key code for the burst key.
    /// </summary>
    public static bool TryParseKey(string? text, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var token = text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrEmpty(token) || IsModifierName(token)) return false;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        if ((token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
        {
            virtualKey = 0x70 + fn - 1;
            return true;
        }

        return NamedKeys.TryGetValue(token, out virtualKey);
    }

    /// <summary>
    /// Parses a HotkeyField combination ("Alt + A", "Ctrl + Shift + F5", ...) into modifiers and a
    /// main virtual key. "Win" combinations are rejected (RegisterHotKey wrapper has no Win flag).
    /// </summary>
    public static bool TryParseHotkey(string? text, out int virtualKey, out bool ctrl, out bool alt, out bool shift)
    {
        virtualKey = 0;
        ctrl = alt = shift = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string? main = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                ctrl = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                alt = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                shift = true;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                return false;
            else
                main = part;
        }

        return main is not null && TryParseKey(main, out virtualKey);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto Antidote Changed subscriber threw"); }
    }

    private void TryActivity(string title, string type)
    {
        try { _activity.AddActivity(title, type); }
        catch { /* activity is best-effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCore(notify: false);

        if (_hotkeyId > 0)
        {
            try { _hotkeys.UnregisterHotkey(_hotkeyId); }
            catch { /* hotkey service may already be disposed */ }
            _hotkeyId = 0;
        }

        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* loop faults surface via its own logging */ }
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
