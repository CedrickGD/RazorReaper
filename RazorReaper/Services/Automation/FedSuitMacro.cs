using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation;

/// <summary>User-configurable keys and timings for the Fed-Suit transmitter macro.</summary>
public sealed class FedSuitSettings
{
    /// <summary>Key that opens the transmitter in game.</summary>
    public string OpenKey { get; set; } = "F";
    /// <summary>Key that closes the transmitter UI.</summary>
    public string ExitKey { get; set; } = "Esc";
    /// <summary>Key pressed repeatedly to transfer items between slots.</summary>
    public string TransferKey { get; set; } = "T";
    /// <summary>Transfer-key presses per cycle.</summary>
    public int PressesPerCycle { get; set; } = 20;
    /// <summary>Delay between transfer presses, in milliseconds.</summary>
    public int PressDelayMs { get; set; } = 50;
    /// <summary>Wait after opening the transmitter before pressing, in milliseconds.</summary>
    public int WaitAfterOpenMs { get; set; } = 700;
    /// <summary>Delay before the next cycle starts, in milliseconds.</summary>
    public int RepeatDelayMs { get; set; } = 500;
    /// <summary>Global hotkey combo string (HotkeyField format) that starts the macro.</summary>
    public string StartHotkey { get; set; } = "F5";
    /// <summary>Global hotkey combo string (HotkeyField format) that stops the macro.</summary>
    public string StopHotkey { get; set; } = "F6";
    /// <summary>Click the calibrated first slot after opening the transmitter (off by default).</summary>
    public bool ClickFirstSlot { get; set; }

    /// <summary>Shallow copy so callers never share a mutable instance with the service.</summary>
    public FedSuitSettings Clone() => (FedSuitSettings)MemberwiseClone();
}

/// <summary>
/// Fed-Suit transmitter automation: each cycle focuses ARK, opens the transmitter, waits, presses
/// the transfer key N times with a delay, exits, then repeats until stopped. Runs as a singleton —
/// it keeps going when the user navigates away from the page; the stop hotkey is system-wide
/// (works while ARK has focus) and is the hard stop.
/// </summary>
public interface IFedSuitMacro : IDisposable
{
    /// <summary>Snapshot copy of the current settings.</summary>
    FedSuitSettings Settings { get; }

    /// <summary>True while the transmitter loop is running.</summary>
    bool IsRunning { get; }

    /// <summary>1-based cycle currently executing (0 when idle).</summary>
    int CurrentCycle { get; }

    /// <summary>Cycles fully completed in the current (or last) run.</summary>
    int CyclesCompleted { get; }

    /// <summary>Human-readable summary of the last run (e.g. "12 cycles in 3m 40s"), or null.</summary>
    string? LastRunSummary { get; }

    /// <summary>True while the start hotkey holds a live system-wide registration.</summary>
    bool StartHotkeyRegistered { get; }

    /// <summary>True while the stop hotkey holds a live system-wide registration.</summary>
    bool StopHotkeyRegistered { get; }

    /// <summary>Raised when running state, cycle counters, or settings change. May fire on a background thread.</summary>
    event Action? Changed;

    /// <summary>Validates, persists, and applies new settings (re-registers hotkeys when they changed).</summary>
    void UpdateSettings(FedSuitSettings settings);

    /// <summary>Starts the loop. Returns false when already running or a configured key is invalid.</summary>
    bool Start();

    /// <summary>Requests a hard stop. Safe from any thread, including hotkey callbacks.</summary>
    void Stop();
}

/// <summary>Default <see cref="IFedSuitMacro"/> implementation built on the shared automation core.</summary>
public sealed class FedSuitMacro : IFedSuitMacro
{
    /// <summary>Calibration point name for the first transmitter slot (per-resolution).</summary>
    public const string FirstSlotPointName = "fedsuit-first-slot";

    private const string RunnerName = "fed-suit";
    private const int SlotClickSettleMs = 150;

    private readonly IMacroEngine _engine;
    private readonly IAutomationHotkeyService _hotkeys;
    private readonly ICalibrationService _calibration;
    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;
    private readonly IUsageGateService _usageGate;
    private readonly ILogger<FedSuitMacro> _logger;
    private readonly IMacroRunner _runner;
    private readonly object _gate = new();

    private FedSuitSettings _settings;
    private int _startHotkeyId;
    private int _stopHotkeyId;
    private volatile bool _running;
    private volatile bool _disposed;
    private bool _stopRequested;
    private int _currentCycle;
    private int _cyclesCompleted;
    private int _lastStepIndex;
    private string? _lastRunSummary;
    private DateTime _runStartedUtc;

    public FedSuitMacro(
        IMacroEngine engine,
        IAutomationHotkeyService hotkeys,
        ICalibrationService calibration,
        INotificationService notifications,
        IActivityService activity,
        IUsageGateService usageGate,
        ILogger<FedSuitMacro> logger)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _calibration = calibration;
        _notifications = notifications;
        _activity = activity;
        _usageGate = usageGate;
        _logger = logger;

        _settings = LoadSettings();
        _runner = _engine.GetRunner(RunnerName);
        _runner.StepStarted += OnStepStarted;
        // First registration is quiet (log only) — the page surfaces registration state inline.
        RegisterHotkeys(notifyFailures: false);
    }

    public FedSuitSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    public bool IsRunning => _running;

    public int CurrentCycle
    {
        get { lock (_gate) return _currentCycle; }
    }

    public int CyclesCompleted
    {
        get { lock (_gate) return _cyclesCompleted; }
    }

    public string? LastRunSummary
    {
        get { lock (_gate) return _lastRunSummary; }
    }

    public bool StartHotkeyRegistered => _startHotkeyId > 0;

    public bool StopHotkeyRegistered => _stopHotkeyId > 0;

    public event Action? Changed;

    public void UpdateSettings(FedSuitSettings settings)
    {
        if (_disposed || settings is null) return;

        var normalized = Normalize(settings);
        bool rebind;
        lock (_gate)
        {
            rebind = !string.Equals(_settings.StartHotkey, normalized.StartHotkey, StringComparison.OrdinalIgnoreCase)
                  || !string.Equals(_settings.StopHotkey, normalized.StopHotkey, StringComparison.OrdinalIgnoreCase)
                  || _startHotkeyId == 0
                  || _stopHotkeyId == 0;
            _settings = normalized;
        }

        SaveSettings(normalized);
        if (rebind) RegisterHotkeys(notifyFailures: true);
        RaiseChanged();
    }

    public bool Start()
    {
        if (_disposed) return false;

        FedSuitSettings snapshot;
        lock (_gate)
        {
            if (_running) return false;
            snapshot = _settings.Clone();
        }

        var sequence = BuildSequence(snapshot);
        if (sequence is null) return false;

        lock (_gate)
        {
            if (_running) return false; // raced with a hotkey press — first one wins
            _running = true;
            _stopRequested = false;
            _currentCycle = 0;
            _cyclesCompleted = 0;
            _lastStepIndex = sequence.Steps.Count - 1;
            _runStartedUtc = DateTime.UtcNow;
        }

        try { _notifications.ShowInfo($"Fed-Suit macro started — press {snapshot.StopHotkey} to stop."); }
        catch { /* notifications are best-effort */ }

        _ = Task.Run(() => RunToCompletionAsync(sequence));
        // Start() must stay synchronous (the global hotkey calls it), so the quota check runs
        // right behind the start and stops the macro again if the month is used up. Stops
        // themselves never count.
        _ = Task.Run(EnforceQuotaAsync);
        RaiseChanged();
        return true;
    }

    private async Task EnforceQuotaAsync()
    {
        try
        {
            var gate = await _usageGate.TryConsumeAsync(UsageFeatures.FedSuit);
            if (gate.Allowed) return;

            Stop();
            _notifications.ShowWarning($"Free monthly limit reached ({gate.Limit} Fed-Suit starts). Resets next month — Premium is unlimited.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fed-Suit quota check failed — failing open");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _stopRequested = true;
        }
        _runner.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); }
        catch { /* best-effort while shutting down */ }

        try
        {
            if (_startHotkeyId > 0) _hotkeys.UnregisterHotkey(_startHotkeyId);
            if (_stopHotkeyId > 0) _hotkeys.UnregisterHotkey(_stopHotkeyId);
            _startHotkeyId = 0;
            _stopHotkeyId = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fed-Suit hotkey cleanup failed");
        }

        _runner.StepStarted -= OnStepStarted;
    }

    // ─── Run lifecycle ─────────────────────────────────────────────────────────

    private async Task RunToCompletionAsync(MacroSequence sequence)
    {
        try
        {
            await _runner.RunAsync(sequence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fed-Suit macro run crashed");
        }
        finally
        {
            bool stoppedByUser;
            int cycles;
            TimeSpan elapsed;
            lock (_gate)
            {
                _running = false;
                stoppedByUser = _stopRequested;
                cycles = _cyclesCompleted;
                _currentCycle = 0;
                elapsed = DateTime.UtcNow - _runStartedUtc;
                _lastRunSummary = $"{cycles} {CycleWord(cycles)} in {FormatDuration(elapsed)}";
            }

            try
            {
                if (stoppedByUser || cycles > 0)
                    _notifications.ShowInfo($"Fed-Suit macro stopped — {cycles} {CycleWord(cycles)} completed.");
                else
                    _notifications.ShowWarning("Fed-Suit macro could not run — the ARK window was not available.");

                _activity.AddActivity(
                    $"Fed-Suit run: {cycles} {CycleWord(cycles)} ({FormatDuration(elapsed)})",
                    cycles > 0 ? "success" : "warning");
            }
            catch { /* notifications/activity are best-effort */ }

            RaiseChanged();
        }
    }

    private void OnStepStarted(int stepIndex, int loopNumber)
    {
        if (!_running) return;

        var changed = false;
        lock (_gate)
        {
            if (stepIndex == 0 && _currentCycle != loopNumber)
            {
                _currentCycle = loopNumber;
                _cyclesCompleted = loopNumber - 1;
                changed = true;
            }
            else if (stepIndex == _lastStepIndex && _cyclesCompleted != loopNumber)
            {
                // The exit press is the last step — reaching it means the cycle is done.
                _cyclesCompleted = loopNumber;
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    private MacroSequence? BuildSequence(FedSuitSettings s)
    {
        if (!FedSuitKeys.TryParseKey(s.OpenKey, out var openVk)) { NotifyBadKey("Open Transmitter", s.OpenKey); return null; }
        if (!FedSuitKeys.TryParseKey(s.ExitKey, out var exitVk)) { NotifyBadKey("Exit Transmitter", s.ExitKey); return null; }
        if (!FedSuitKeys.TryParseKey(s.TransferKey, out var transferVk)) { NotifyBadKey("Transfer", s.TransferKey); return null; }

        var steps = new List<MacroStep>
        {
            MacroStep.FocusGameWindow(),
            MacroStep.KeyPress(openVk),
            MacroStep.Delay(s.WaitAfterOpenMs)
        };

        if (s.ClickFirstSlot)
        {
            if (_calibration.TryGetPoint(FirstSlotPointName, out var slot))
            {
                steps.Add(MacroStep.ClickAt(slot.X, slot.Y));
                steps.Add(MacroStep.Delay(SlotClickSettleMs));
            }
            else
            {
                try { _notifications.ShowWarning("First slot position is not calibrated for this resolution — the click step was skipped."); }
                catch { /* notifications are best-effort */ }
            }
        }

        for (var i = 0; i < s.PressesPerCycle; i++)
        {
            steps.Add(MacroStep.KeyPress(transferVk));
            steps.Add(MacroStep.Delay(s.PressDelayMs));
        }

        steps.Add(MacroStep.KeyPress(exitVk));

        return new MacroSequence
        {
            Name = "Fed-Suit",
            Steps = steps,
            RepeatCount = 0, // until stopped
            LoopDelayMs = s.RepeatDelayMs
        };
    }

    private void NotifyBadKey(string label, string value)
    {
        _logger.LogWarning("Fed-Suit {Label} key '{Value}' could not be parsed", label, value);
        try { _notifications.ShowError($"{label} key \"{value}\" is not a supported key."); }
        catch { /* notifications are best-effort */ }
    }

    // ─── Hotkeys ───────────────────────────────────────────────────────────────

    private void RegisterHotkeys(bool notifyFailures)
    {
        try
        {
            FedSuitSettings s;
            lock (_gate) s = _settings.Clone();

            if (_startHotkeyId > 0) { _hotkeys.UnregisterHotkey(_startHotkeyId); _startHotkeyId = 0; }
            if (_stopHotkeyId > 0) { _hotkeys.UnregisterHotkey(_stopHotkeyId); _stopHotkeyId = 0; }

            if (FedSuitKeys.TryParseCombo(s.StartHotkey, out var startVk, out var startCtrl, out var startAlt, out var startShift))
                _startHotkeyId = _hotkeys.RegisterHotkey(startVk, startCtrl, startAlt, startShift, OnStartHotkey);

            if (FedSuitKeys.TryParseCombo(s.StopHotkey, out var stopVk, out var stopCtrl, out var stopAlt, out var stopShift))
                _stopHotkeyId = _hotkeys.RegisterHotkey(stopVk, stopCtrl, stopAlt, stopShift, OnStopHotkey);

            if (_startHotkeyId == 0)
            {
                _logger.LogWarning("Fed-Suit start hotkey '{Hotkey}' could not be registered", s.StartHotkey);
                if (notifyFailures)
                {
                    try { _notifications.ShowWarning($"Start hotkey {s.StartHotkey} could not be registered — it may be in use by another app."); }
                    catch { /* notifications are best-effort */ }
                }
            }
            if (_stopHotkeyId == 0)
            {
                _logger.LogWarning("Fed-Suit stop hotkey '{Hotkey}' could not be registered", s.StopHotkey);
                if (notifyFailures)
                {
                    try { _notifications.ShowWarning($"Stop hotkey {s.StopHotkey} could not be registered — it may be in use by another app."); }
                    catch { /* notifications are best-effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fed-Suit hotkey registration failed");
        }
    }

    private void OnStartHotkey()
    {
        if (_disposed || _running) return;
        Start();
    }

    private void OnStopHotkey()
    {
        if (_disposed) return;
        Stop();
    }

    // ─── Settings persistence ──────────────────────────────────────────────────

    private FedSuitSettings LoadSettings()
    {
        var defaults = new FedSuitSettings();
        try
        {
            return Normalize(new FedSuitSettings
            {
                OpenKey = Preferences.Get("fedsuit.openkey", defaults.OpenKey),
                ExitKey = Preferences.Get("fedsuit.exitkey", defaults.ExitKey),
                TransferKey = Preferences.Get("fedsuit.transferkey", defaults.TransferKey),
                PressesPerCycle = Preferences.Get("fedsuit.presses", defaults.PressesPerCycle),
                PressDelayMs = Preferences.Get("fedsuit.pressdelay", defaults.PressDelayMs),
                WaitAfterOpenMs = Preferences.Get("fedsuit.openwait", defaults.WaitAfterOpenMs),
                RepeatDelayMs = Preferences.Get("fedsuit.repeatdelay", defaults.RepeatDelayMs),
                StartHotkey = Preferences.Get("fedsuit.starthotkey", defaults.StartHotkey),
                StopHotkey = Preferences.Get("fedsuit.stophotkey", defaults.StopHotkey),
                ClickFirstSlot = Preferences.Get("fedsuit.clickslot", defaults.ClickFirstSlot)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fed-Suit settings load failed — using defaults");
            return defaults;
        }
    }

    private void SaveSettings(FedSuitSettings s)
    {
        try
        {
            Preferences.Set("fedsuit.openkey", s.OpenKey);
            Preferences.Set("fedsuit.exitkey", s.ExitKey);
            Preferences.Set("fedsuit.transferkey", s.TransferKey);
            Preferences.Set("fedsuit.presses", s.PressesPerCycle);
            Preferences.Set("fedsuit.pressdelay", s.PressDelayMs);
            Preferences.Set("fedsuit.openwait", s.WaitAfterOpenMs);
            Preferences.Set("fedsuit.repeatdelay", s.RepeatDelayMs);
            Preferences.Set("fedsuit.starthotkey", s.StartHotkey);
            Preferences.Set("fedsuit.stophotkey", s.StopHotkey);
            Preferences.Set("fedsuit.clickslot", s.ClickFirstSlot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fed-Suit settings save failed");
        }
    }

    private static FedSuitSettings Normalize(FedSuitSettings s)
    {
        var d = new FedSuitSettings();
        return new FedSuitSettings
        {
            OpenKey = NormalizeKey(s.OpenKey, d.OpenKey),
            ExitKey = NormalizeKey(s.ExitKey, d.ExitKey),
            TransferKey = NormalizeKey(s.TransferKey, d.TransferKey),
            PressesPerCycle = Math.Clamp(s.PressesPerCycle, 1, 500),
            PressDelayMs = Math.Clamp(s.PressDelayMs, 0, 10_000),
            WaitAfterOpenMs = Math.Clamp(s.WaitAfterOpenMs, 0, 30_000),
            RepeatDelayMs = Math.Clamp(s.RepeatDelayMs, 0, 60_000),
            StartHotkey = NormalizeCombo(s.StartHotkey, d.StartHotkey),
            StopHotkey = NormalizeCombo(s.StopHotkey, d.StopHotkey),
            ClickFirstSlot = s.ClickFirstSlot
        };
    }

    private static string NormalizeKey(string? value, string fallback)
        => !string.IsNullOrWhiteSpace(value) && FedSuitKeys.TryParseKey(value, out _)
            ? value.Trim()
            : fallback;

    private static string NormalizeCombo(string? value, string fallback)
        => !string.IsNullOrWhiteSpace(value) && FedSuitKeys.TryParseCombo(value, out _, out _, out _, out _)
            ? value.Trim()
            : fallback;

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* subscriber errors must not kill the macro */ }
    }

    private static string CycleWord(int count) => count == 1 ? "cycle" : "cycles";

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }
}

/// <summary>
/// Parses HotkeyField strings ("F5", "Esc", "Ctrl + Alt + F") into Win32 virtual-key codes and
/// modifier flags. Shared by the Fed-Suit macro; internal so other automation features in this
/// assembly can reuse it.
/// </summary>
internal static class FedSuitKeys
{
    /// <summary>Parses a full combo ("Ctrl + Shift + F5") into vk + modifier flags.</summary>
    public static bool TryParseCombo(string? combo, out int vk, out bool ctrl, out bool alt, out bool shift)
    {
        vk = 0;
        ctrl = alt = shift = false;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    ctrl = true;
                    break;
                case "ALT":
                    alt = true;
                    break;
                case "SHIFT":
                    shift = true;
                    break;
                case "WIN":
                case "META":
                    // The Win modifier is not supported by the hotkey service — ignored.
                    break;
                default:
                    return false;
            }
        }
        return TryParseKey(parts[^1], out vk);
    }

    /// <summary>Parses a single key name (last segment of a combo) into a virtual-key code.</summary>
    public static bool TryParseKey(string? name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var key = name.Trim().ToUpperInvariant();

        if (key.Length == 1)
        {
            var c = key[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                return true;
            }
            vk = c switch
            {
                ';' => 0xBA,
                '=' => 0xBB,
                ',' => 0xBC,
                '-' => 0xBD,
                '.' => 0xBE,
                '/' => 0xBF,
                '`' => 0xC0,
                '[' => 0xDB,
                '\\' => 0xDC,
                ']' => 0xDD,
                '\'' => 0xDE,
                _ => 0
            };
            return vk != 0;
        }

        if (key[0] == 'F' && key.Length <= 3 && int.TryParse(key[1..], out var fn) && fn >= 1 && fn <= 24)
        {
            vk = 0x70 + fn - 1;
            return true;
        }

        vk = key switch
        {
            "SPACE" => 0x20,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            "SCROLLLOCK" => 0x91,
            "PAUSE" => 0x13,
            _ => 0
        };
        return vk != 0;
    }
}
