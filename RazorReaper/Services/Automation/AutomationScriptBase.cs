using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;

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

    /// <summary>
    /// The scripts' own words. Every script shares this scaffold, so the localizer lives here
    /// rather than seventeen times over: a subclass that has something to say resolves it where
    /// it says it — <see cref="CanStart"/>'s refusal, an activity line — and a language switch
    /// reaches all of them at once. <see cref="DisplayName"/> is not one of these: Yuty, Noglin
    /// and Armor Swap are the names the community uses and diagnostics report.
    /// </summary>
    protected readonly ILocalizer Localizer;

    protected readonly ILogger Logger;

    private readonly string _scriptKey;
    private readonly string _displayName;
    private readonly string _defaultHotkey;

    private readonly object _gate = new();

    /// <summary>
    /// Keys this script currently holds down, with the simulator that pressed them. A held key is
    /// the one piece of script state that outlives the script: if the run loop dies between the
    /// down and the up — a thrown tick, a hard Dispose, a stop while the character is sprinting —
    /// the game keeps the key down forever and the user is left running into a wall. Every exit
    /// path drains this.
    /// </summary>
    private readonly HashSet<(IInputSimulator Input, int VirtualKey)> _heldKeys = new();

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
        ILocalizer localizer,
        ILogger logger)
    {
        _scriptKey = scriptKey;
        _displayName = displayName;
        _defaultHotkey = defaultHotkey ?? string.Empty;
        Foreground = foreground;
        Hotkeys = hotkeys;
        Notifications = notifications;
        Activity = activity;
        Localizer = localizer;
        Logger = logger;

        StartStopHotkey = LoadHotkey();
        try { ApplyHotkey(notifyOnFailure: false); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} hotkey setup failed", _displayName); }
    }

    /// <summary>
    /// How many scripts are running across the whole process. Kept as a static counter rather
    /// than by enumerating the DI registrations, because resolving every script just to ask
    /// would construct all seventeen of them — and constructing one claims its global hotkey.
    /// </summary>
    private static int s_runningScripts;

    /// <summary>True while any script's run loop is live. Read by the update gate.</summary>
    public static bool AnyRunning => Volatile.Read(ref s_runningScripts) > 0;

    public ScriptState State => _state;
    public bool IsRunning => _state == ScriptState.Running;
    public string DisplayName => _displayName;
    public string ScriptKey => _scriptKey;

    /// <summary>
    /// True for the screen-reading scripts (they override via <c>CalibratableScriptBase</c>).
    /// Only the input-only scripts draw from the shared monthly free quota — the vision ones
    /// are unquota'd (and unadvertised) until they are proven live.
    /// </summary>
    public virtual bool UsesVision => false;

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
            SetStateLocked(ScriptState.Running);
            _task = Task.Run(() => RunGuardedAsync(token));
        }

        Notifications.ShowSuccess(Localizer.T("scripts.toast.started", _displayName));
        TryActivity(Localizer.T("scripts.activity.started", _displayName), "success", "scripts.activity.started");
        // Start() must stay synchronous (the global hotkey calls it through Toggle), so the
        // quota check trails the start and stops the script again if the month is used up.
        // Vision scripts and stops never count.
        if (!UsesVision) _ = Task.Run(EnforceInputQuotaAsync);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// How a script finds the usage gate. Resolved late, not via the constructor: the base ctor
    /// signature is mirrored by 16 scripts, and the headless test harnesses construct them
    /// without a MAUI application at all. Replaceable so a test can see what was charged — the
    /// Fed Suit double-charge was invisible precisely because nothing could.
    /// </summary>
    internal Func<IUsageGateService?> ResolveUsageGate { get; set; }
        = static () => IPlatformApplication.Current?.Services?.GetService<IUsageGateService>();

    private async Task EnforceInputQuotaAsync()
    {
        try
        {
            var gate = ResolveUsageGate();
            if (gate is null) return;

            var result = await gate.TryConsumeAsync(UsageFeatures.InputScripts);
            if (result.Allowed) return;

            Stop();
            Notifications.ShowWarning(Localizer.T("scripts.toast.quota", result.Limit));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Script} quota check failed — failing open", _displayName);
        }
    }

    public void Stop() => StopCore(notify: true);

    public void Toggle()
    {
        if (_state == ScriptState.Off) Start();
        else Stop();
    }

    /// <summary>
    /// The only writer of <see cref="_state"/>, so the process-wide running count cannot drift.
    /// The caller holds <see cref="_gate"/>.
    /// </summary>
    private void SetStateLocked(ScriptState next)
    {
        if (_state == next) return;

        _state = next;
        if (next == ScriptState.Running)
        {
            Interlocked.Increment(ref s_runningScripts);
        }
        else
        {
            Interlocked.Decrement(ref s_runningScripts);
        }
    }

    private void StopCore(bool notify)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state == ScriptState.Off) return;
            SetStateLocked(ScriptState.Off);
            cts = _cts;
            _cts = null;
        }

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        try { OnStopped(); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} OnStopped threw", _displayName); }

        // After OnStopped, not before: a subclass may release its own keys there, and releasing
        // twice is harmless while releasing too early is not.
        ReleaseHeldKeys();

        if (notify)
        {
            Notifications.ShowInfo(Localizer.T("scripts.toast.stopped", _displayName));
            TryActivity(Localizer.T("scripts.activity.stopped", _displayName), "info", "scripts.activity.stopped");
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
            // Whatever ended the run — a stop, a crash, the body simply returning — no key of
            // ours may still be down once the loop is gone.
            ReleaseHeldKeys();

            // A self-terminated run (returned without a Stop) must reflect Off too.
            lock (_gate)
            {
                if (!ct.IsCancellationRequested)
                    SetStateLocked(ScriptState.Off);
            }
            RaiseChanged();
        }
    }

    // ─── Held keys ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Presses a key and remembers that it is down, so <see cref="Stop"/>, <see cref="Dispose"/>
    /// or a run loop that dies mid-hold all release it. Pair with <see cref="ReleaseKey"/>.
    /// </summary>
    protected void HoldKey(IInputSimulator input, int virtualKey)
    {
        // Recorded before the press: a KeyDown that throws after the key went down would
        // otherwise be forgotten, and an unremembered held key is exactly the bug. A release
        // for a key that never went down is a no-op the game ignores.
        lock (_gate) _heldKeys.Add((input, virtualKey));
        input.KeyDown(virtualKey);
    }

    /// <summary>Releases a key taken with <see cref="HoldKey"/>. Safe to call twice.</summary>
    protected void ReleaseKey(IInputSimulator input, int virtualKey)
    {
        lock (_gate) _heldKeys.Remove((input, virtualKey));
        try { input.KeyUp(virtualKey); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} release of key {Key} failed", _displayName, virtualKey); }
    }

    /// <summary>True while this script holds <paramref name="virtualKey"/> down.</summary>
    protected bool IsKeyHeld(IInputSimulator input, int virtualKey)
    {
        lock (_gate) return _heldKeys.Contains((input, virtualKey));
    }

    /// <summary>Releases every key this script still holds. Idempotent.</summary>
    protected void ReleaseHeldKeys()
    {
        (IInputSimulator Input, int VirtualKey)[] held;
        lock (_gate)
        {
            if (_heldKeys.Count == 0) return;
            held = _heldKeys.ToArray();
            _heldKeys.Clear();
        }

        foreach (var (input, vk) in held)
        {
            // Each release is its own attempt: one that throws must not strand the others.
            try { input.KeyUp(vk); }
            catch (Exception ex) { Logger.LogWarning(ex, "{Script} release of held key {Key} failed", _displayName, vk); }
        }
    }

    /// <summary>The script body. Loop until <paramref name="ct"/> is cancelled, or return to finish.</summary>
    protected abstract Task RunAsync(CancellationToken ct);

    /// <summary>
    /// Override to block <see cref="Start"/> (e.g. missing calibration); set
    /// <paramref name="reason"/> to notify the user. It is shown as written, so resolve it
    /// through <see cref="Localizer"/> here — the refusal is read at the moment Start is
    /// pressed, which is what keeps it in the language the reader picked.
    /// </summary>
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
    ///
    /// <paramref name="intervalMs"/> is the period, not a pause: the tick is timed and only the
    /// remainder is waited out. It used to wait the full interval *after* the tick, so a screen
    /// grab and a compare that took 30 ms turned the Noglin script's 50 ms scan into 80 ms — the
    /// number on the settings page and the number the script ran at were different, and the
    /// slower the machine the wider the gap.
    /// </summary>
    protected async Task RunLoopAsync(int intervalMs, Func<CancellationToken, Task> tickAsync, bool foregroundOnly, CancellationToken ct)
    {
        intervalMs = Math.Clamp(intervalMs, 10, 60000);
        _lastGateOpen = null;   // report the state once per run, whatever it is
        var tickClock = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            tickClock.Restart();
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

            try { await Task.Delay(RemainingDelayMs(intervalMs, tickClock.ElapsedMilliseconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// What is left of <paramref name="intervalMs"/> after a tick that took
    /// <paramref name="tickElapsedMs"/>. A tick that overran its own interval still yields to the
    /// scheduler for a millisecond rather than spinning: a script that cannot keep up should fall
    /// behind, not pin a core.
    /// </summary>
    internal static int RemainingDelayMs(int intervalMs, long tickElapsedMs)
        => (int)Math.Clamp(intervalMs - tickElapsedMs, 1L, intervalMs);

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
            if (notifyOnFailure) Notifications.ShowWarning(Localizer.T("scripts.toast.hotkey.invalid", text));
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
            Notifications.ShowWarning(Localizer.T("scripts.toast.hotkey.inuse", text));
        }
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    protected void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} Changed subscriber threw", _displayName); }
    }

    protected void TryActivity(string title, string type, string? key = null)
    {
        try { Activity.AddActivity(title, type, key); }
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
