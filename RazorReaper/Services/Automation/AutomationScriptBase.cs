using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation;

/// <summary>Lifecycle state of an automation script.</summary>
public enum ScriptState
{
    /// <summary>Not running.</summary>
    Off,
    /// <summary>The run loop is active.</summary>
    Running
}

/// <summary>
/// Shared scaffold for ARK automation scripts (Yuty, Mammoth, Turret Manager, …). Provides the
/// common plumbing every script needs so we don't re-implement it 16 times: start/stop/toggle with
/// a lifecycle state + <see cref="Changed"/> event, a persisted bindable start/stop hotkey wired
/// through <see cref="IAutomationHotkeyService"/>, a foreground-gated scan-loop helper, and
/// notification/activity helpers. A singleton per script: it keeps running across page navigation
/// and hard-stops via its system-wide hotkey even while ARK has focus. All input stays external
/// (SendInput / screen capture) — BattlEye-safe, like the rest of the automation platform.
/// </summary>
public abstract class AutomationScriptBase : IDisposable
{
    protected readonly IForegroundGate Foreground;
    protected readonly IAutomationHotkeyService Hotkeys;
    protected readonly INotificationService Notifications;
    protected readonly IActivityService Activity;
    protected readonly ILogger Logger;

    private readonly string _scriptKey;
    private readonly string _displayName;
    private readonly string _defaultHotkey;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private volatile ScriptState _state = ScriptState.Off;
    private int _hotkeyId;
    private string? _registeredHotkeyText;
    private bool _disposed;

    /// <summary>Last logged gate state, so the scan loop only reports transitions.</summary>
    private bool? _lastGateOpen;

    protected AutomationScriptBase(
        string scriptKey,
        string displayName,
        string defaultHotkey,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger logger)
    {
        _scriptKey = scriptKey;
        _displayName = displayName;
        _defaultHotkey = defaultHotkey ?? string.Empty;
        Foreground = foreground;
        Hotkeys = hotkeys;
        Notifications = notifications;
        Activity = activity;
        Logger = logger;

        StartStopHotkey = LoadHotkey();
        try { ApplyHotkey(notifyOnFailure: false); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} hotkey setup failed", _displayName); }
    }

    public ScriptState State => _state;
    public bool IsRunning => _state == ScriptState.Running;
    public string DisplayName => _displayName;
    public string ScriptKey => _scriptKey;

    /// <summary>Raised whenever state or a script-specific stat changes. May fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Bindable start/stop hotkey (HotkeyField text). Set it, then call <see cref="SaveHotkey"/>.</summary>
    public string StartStopHotkey { get; set; }

    // ─── Lifecycle ─────────────────────────────────────────────────────────────

    public bool Start()
    {
        if (_disposed) return false;
        if (!CanStart(out var reason))
        {
            if (!string.IsNullOrWhiteSpace(reason)) Notifications.ShowWarning(reason!);
            return false;
        }

        lock (_gate)
        {
            if (_state == ScriptState.Running) return true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _state = ScriptState.Running;
            _task = Task.Run(() => RunGuardedAsync(token));
        }

        Notifications.ShowSuccess($"{_displayName} started.");
        TryActivity($"{_displayName} started", "success");
        RaiseChanged();
        return true;
    }

    public void Stop() => StopCore(notify: true);

    public void Toggle()
    {
        if (_state == ScriptState.Off) Start();
        else Stop();
    }

    private void StopCore(bool notify)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state == ScriptState.Off) return;
            _state = ScriptState.Off;
            cts = _cts;
            _cts = null;
        }

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        try { OnStopped(); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} OnStopped threw", _displayName); }

        if (notify)
        {
            Notifications.ShowInfo($"{_displayName} stopped.");
            TryActivity($"{_displayName} stopped", "info");
        }
        RaiseChanged();
    }

    private async Task RunGuardedAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(ct);
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Script} run loop error", _displayName);
        }
        finally
        {
            // A self-terminated run (returned without a Stop) must reflect Off too.
            lock (_gate)
            {
                if (!ct.IsCancellationRequested)
                    _state = ScriptState.Off;
            }
            RaiseChanged();
        }
    }

    /// <summary>The script body. Loop until <paramref name="ct"/> is cancelled, or return to finish.</summary>
    protected abstract Task RunAsync(CancellationToken ct);

    /// <summary>Override to block <see cref="Start"/> (e.g. missing calibration); set <paramref name="reason"/> to notify the user.</summary>
    protected virtual bool CanStart(out string? reason)
    {
        reason = null;
        return true;
    }

    /// <summary>Override for extra teardown when the script stops (e.g. release a held key/mouse button).</summary>
    protected virtual void OnStopped() { }

    /// <summary>
    /// Convenience scan loop: runs <paramref name="tickAsync"/> every <paramref name="intervalMs"/> ms,
    /// skipping ticks while ARK isn't the foreground window when <paramref name="foregroundOnly"/> is set.
    /// Per-tick exceptions are logged and swallowed so one bad frame doesn't kill the script.
    /// </summary>
    protected async Task RunLoopAsync(int intervalMs, Func<CancellationToken, Task> tickAsync, bool foregroundOnly, CancellationToken ct)
    {
        intervalMs = Math.Clamp(intervalMs, 10, 60000);
        _lastGateOpen = null;   // report the state once per run, whatever it is
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var gateOpen = !foregroundOnly || Foreground.IsGameForeground();

                // Only on a change. "Is the gate open?" is the first question whenever a script
                // runs but does nothing (it was shut for everyone once, see ForegroundGate), so
                // the answer has to be in the log — but per tick it would be ~100 lines a second
                // at the Auto Clicker's 10ms floor.
                if (gateOpen != _lastGateOpen)
                {
                    _lastGateOpen = gateOpen;
                    Logger.LogDebug("{Script} loop: gate={Gate}", _displayName, gateOpen);
                }

                if (gateOpen)
                    await tickAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Logger.LogError(ex, "{Script} tick error", _displayName); }

            try { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ─── Hotkey ────────────────────────────────────────────────────────────────

    private string HotkeyPrefKey => $"script.{_scriptKey}.hotkey";

    private string LoadHotkey()
    {
        try { return Preferences.Get(HotkeyPrefKey, _defaultHotkey); }
        catch { return _defaultHotkey; }
    }

    /// <summary>Persists and (re)registers the current <see cref="StartStopHotkey"/>.</summary>
    public void SaveHotkey()
    {
        try { Preferences.Set(HotkeyPrefKey, StartStopHotkey ?? string.Empty); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} save hotkey failed", _displayName); }
        try { ApplyHotkey(notifyOnFailure: true); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} apply hotkey failed", _displayName); }
        RaiseChanged();
    }

    private void ApplyHotkey(bool notifyOnFailure)
    {
        var text = StartStopHotkey ?? string.Empty;
        if (_hotkeyId > 0 && string.Equals(text, _registeredHotkeyText, StringComparison.OrdinalIgnoreCase))
            return;

        if (_hotkeyId > 0)
        {
            try { Hotkeys.UnregisterHotkey(_hotkeyId); }
            catch { /* hotkey service may be tearing down */ }
            _hotkeyId = 0;
            _registeredHotkeyText = null;
        }

        if (string.IsNullOrWhiteSpace(text)) return; // no hotkey bound is a valid choice

        if (!HotkeyParser.TryParseHotkey(text, out var vk, out var ctrl, out var alt, out var shift))
        {
            if (notifyOnFailure) Notifications.ShowWarning($"'{text}' can't be used as a hotkey.");
            StartStopHotkey = _registeredHotkeyText ?? _defaultHotkey;
            return;
        }

        var id = Hotkeys.RegisterHotkey(vk, ctrl, alt, shift, Toggle);
        if (id > 0)
        {
            _hotkeyId = id;
            _registeredHotkeyText = text;
        }
        else if (notifyOnFailure)
        {
            Notifications.ShowWarning($"Could not register {text} — it may be in use by another app.");
        }
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    protected void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} Changed subscriber threw", _displayName); }
    }

    protected void TryActivity(string title, string type)
    {
        try { Activity.AddActivity(title, type); }
        catch { /* activity is best-effort */ }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopCore(notify: false);

        if (_hotkeyId > 0)
        {
            try { Hotkeys.UnregisterHotkey(_hotkeyId); }
            catch { /* hotkey service may already be disposed */ }
            _hotkeyId = 0;
        }

        try { _task?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* loop faults surface via its own logging */ }
    }
}
