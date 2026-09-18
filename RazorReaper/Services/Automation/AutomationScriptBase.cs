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

    /// <summary>
    /// How long a run may go without producing a single effect before the user is told the
    /// foreground gate is what is holding it. Half a minute: long enough that alt-tabbing back
    /// into the game after starting a script from the page never trips it, short enough that
    /// nobody sits watching a "running" script for a whole minute wondering.
    /// </summary>
    private const int DefaultSilentRunWarningMs = 30_000;

    /// <summary>Effects produced by the current (or last) run. Reset by <see cref="Start()"/>.</summary>
    private int _effectCount;

    /// <summary><see cref="Environment.TickCount64"/> of the last effect. Only valid when <see cref="_effectCount"/> is above zero.</summary>
    private long _lastEffectTicks;

    /// <summary><see cref="Environment.TickCount64"/> at the start of the current run.</summary>
    private long _runStartedTicks;

    /// <summary>One silent-run warning per run, and this is the flag that keeps it to one.</summary>
    private int _silenceWarned;

    /// <summary>One stop event per run. Several paths reach the end of a run; only one reports it.</summary>
    private int _runReported;

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

    /// <summary>
    /// True for the scripts that send their sequence on fixed timings and never check that any of
    /// it landed: Astro, Turret Manager, Auto Download, Fast TP and Crafting in Walk mode. They
    /// work when the server keeps up and silently do nothing useful when it does not, and the
    /// catalogue said nothing about the difference — sixteen tiles, all presented alike, five of
    /// them unable to tell success from a wasted keypress. The page marks these.
    ///
    /// Not a field, because Crafting is only one of them in one of its two modes.
    /// </summary>
    public virtual bool IsExperimental => false;

    /// <summary>Raised whenever state or a script-specific stat changes. May fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Bindable start/stop hotkey (HotkeyField text). Set it, then call <see cref="SaveHotkey"/>.</summary>
    public string StartStopHotkey { get; set; }

    // ─── Lifecycle ─────────────────────────────────────────────────────────────

    public bool Start() => Start(fromHotkey: false);

    /// <summary>
    /// <paramref name="fromHotkey"/> rides along for the telemetry only: a script started from
    /// the page and a script started by its global hotkey are the same run, but which of the two
    /// people actually use is a thing the panel cannot infer from anywhere else.
    /// </summary>
    private bool Start(bool fromHotkey)
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
            ResetRunCounters();
            SetStateLocked(ScriptState.Running);
            _task = Task.Run(() => RunGuardedAsync(token));

            // Discarded deliberately: the watchdog catches everything, so there is no fault
            // for anyone to hold, and awaiting it would keep Start() from returning for half
            // a minute — with the global hotkey on the other end of it.
            _ = Task.Run(() => WarnIfSilentAsync(token));
        }

        Notifications.ShowSuccess(Localizer.T("scripts.toast.started", _displayName));
        TryActivity(Localizer.T("scripts.activity.started", _displayName), "success", "scripts.activity.started");
        // Before the quota check is dispatched, not after: that check can end the run within
        // milliseconds, and a stop event arriving ahead of its own start is a row pair the
        // panel cannot make sense of.
        ReportRunStarted(fromHotkey);

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

            // Not Stop(): a run cut off by the quota reads on the panel as a run the user
            // ended, and "people start scripts and immediately stop them" is a very different
            // conclusion from "people run out of free runs".
            StopCore(notify: true, ScriptStopReason.Quota);
            Notifications.ShowWarning(Localizer.T("scripts.toast.quota", result.Limit));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Script} quota check failed — failing open", _displayName);
        }
    }

    public void Stop() => StopCore(notify: true, ScriptStopReason.User);

    /// <summary>
    /// What the global hotkey is bound to, and the only caller of it. That is what lets the
    /// start event say the press came from a hotkey rather than from the page.
    /// </summary>
    public void Toggle()
    {
        if (_state == ScriptState.Off) Start(fromHotkey: true);
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

    private void StopCore(bool notify, ScriptStopReason reason)
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

        // Before the run task has finished unwinding, and that is the point: this call and the
        // loop's own finally are both ends of the same run, the first one through reports it,
        // and the reason the caller knows is better than the one the loop can infer.
        ReportRunEnded(reason);
        RaiseChanged();
    }

    private async Task RunGuardedAsync(CancellationToken ct)
    {
        var faulted = false;
        try
        {
            await RunAsync(ct);
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex)
        {
            faulted = true;
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

            // A crash reports itself; anything else that reached here without a Stop is a run
            // that finished the way it was asked to — the one-shots all end this way. When a
            // Stop did get here first, this is a no-op and its reason is the one that stands.
            ReportRunEnded(faulted ? ScriptStopReason.Error : ScriptStopReason.User);
            RaiseChanged();
        }
    }

    // ─── Effects ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How many times this run has actually done something. Zero on a run that has been going
    /// for a while is the number worth reading: it means the script is alive and the game is
    /// not receiving anything from it.
    /// </summary>
    public int EffectCount => Volatile.Read(ref _effectCount);

    /// <summary>
    /// How long ago this script last acted, or null when it has yet to act at all this run.
    /// A wall-clock age would be wrong across a sleep; the tick count is not.
    /// </summary>
    public TimeSpan? SinceLastEffect
    {
        get
        {
            // The count is the gate, not the timestamp: TickCount64 is legitimately 0 for the
            // first millisecond after a boot, and "no effect yet" and "an effect at t=0" must
            // not be the same answer.
            if (EffectCount == 0) return null;
            var at = Interlocked.Read(ref _lastEffectTicks);
            return TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - at));
        }
    }

    /// <summary>
    /// Called by a script at the moment it actually acts — the key goes out, the click lands,
    /// the console command is sent. Not once per tick: a scan that found nothing is not an
    /// effect, and telling those two apart is the entire point of the counter.
    ///
    /// It deliberately does not <see cref="RaiseChanged"/>. Tek Saddle reports fifty of these a
    /// second and Take All twenty; a render each would be a render storm in service of a line
    /// that reads "2 s ago". The Scripts page repaints on its own 1.5 s tick and reads the
    /// number there.
    /// </summary>
    protected void ReportEffect()
    {
        // Timestamp first, count second: a reader that sees a non-zero count then always finds
        // a timestamp that belongs to it rather than to the effect before.
        Interlocked.Exchange(ref _lastEffectTicks, Environment.TickCount64);
        Interlocked.Increment(ref _effectCount);
    }

    private void ResetRunCounters()
    {
        Volatile.Write(ref _effectCount, 0);
        Interlocked.Exchange(ref _lastEffectTicks, 0);
        Volatile.Write(ref _silenceWarned, 0);
        Volatile.Write(ref _runReported, 0);
        Volatile.Write(ref _runStartedTicks, Environment.TickCount64);
    }

    /// <summary>
    /// The one thing the page cannot say for itself: a script that has been running for half a
    /// minute, has never acted, and is looking at a shut gate right now. That is not a script
    /// finding nothing — it is a script that has never been given a chance to look, and the
    /// fix is a single alt-tab.
    ///
    /// One toast per run, and only that one. The state does not change from tick to tick, so
    /// repeating it would be the same sentence every interval for as long as the window stays
    /// where it is.
    /// </summary>
    /// <summary>
    /// The silent-run window, in milliseconds. Settable so a test can prove the warning without
    /// sitting out half a minute per case — the same reason <see cref="ResolveUsageGate"/> is a
    /// property rather than a constructor argument.
    /// </summary>
    internal int SilentRunWarningMs { get; set; } = DefaultSilentRunWarningMs;

    private async Task WarnIfSilentAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SilentRunWarningMs, ct);

            if (!IsRunning || EffectCount > 0) return;

            // Asked now rather than tracked across the run: a script with zero effects whose
            // gate is open at this moment is a script that is scanning and finding nothing,
            // which is a different sentence and not one to interrupt anybody with.
            if (Foreground.IsGameForeground()) return;

            if (Interlocked.Exchange(ref _silenceWarned, 1) != 0) return;

            Notifications.ShowWarning(Localizer.T("scripts.toast.waitingforark", _displayName));
        }
        catch (OperationCanceledException) { /* stopped inside the window — nothing to say */ }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Script} silent-run check failed", _displayName);
        }
    }

    // ─── Telemetry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// How a script finds the telemetry sink, resolved late for the same reason the usage gate
    /// is: the headless test harnesses construct scripts without a MAUI application, and 16
    /// subclasses mirror the base constructor's signature.
    /// </summary>
    internal Func<ITelemetryService?> ResolveTelemetry { get; set; }
        = static () => IPlatformApplication.Current?.Services?.GetService<ITelemetryService>();

    /// <summary>
    /// Whether this install is premium, for the start event. Free and premium users run these
    /// scripts for different lengths of time and hit different walls, and the quota reason on
    /// the stop event only means something next to it.
    /// </summary>
    internal Func<bool> ResolvePremium { get; set; }
        = static () => IPlatformApplication.Current?.Services?.GetService<ILicenseService>()?.IsPremium ?? false;

    private void ReportRunStarted(bool fromHotkey)
    {
        Track(ScriptTelemetryEvents.Start, new Dictionary<string, object?>
        {
            ["script"] = _scriptKey,
            ["premium"] = SafePremium(),
            ["hotkey"] = fromHotkey
        });
    }

    /// <summary>
    /// One report per run, whichever path reaches the end of it first. A user stop cancels the
    /// loop, so both the stop and the loop's own unwind arrive here for the same run — counted
    /// twice, every duration on the panel would be doubled and every run would appear as two.
    /// </summary>
    private void ReportRunEnded(ScriptStopReason reason)
    {
        if (Interlocked.Exchange(ref _runReported, 1) != 0) return;

        var effects = EffectCount;
        var seconds = (int)Math.Max(0, (Environment.TickCount64 - Volatile.Read(ref _runStartedTicks)) / 1000);
        var reasonText = ReasonText(reason);

        Track(ScriptTelemetryEvents.Stop, new Dictionary<string, object?>
        {
            ["script"] = _scriptKey,
            ["duration_s"] = seconds,
            ["effects"] = effects,
            ["noop"] = effects == 0,
            ["reason"] = reasonText
        });

        // The same fact as noop=true on the row above, as a row of its own: a run that did
        // nothing is the thing worth counting, and counting rows is cheaper on the panel than
        // filtering payloads.
        if (effects == 0)
        {
            Track(ScriptTelemetryEvents.Noop, new Dictionary<string, object?>
            {
                ["script"] = _scriptKey,
                ["duration_s"] = seconds,
                ["reason"] = reasonText
            });
        }
    }

    private static string ReasonText(ScriptStopReason reason) => reason switch
    {
        ScriptStopReason.Quota => "quota",
        ScriptStopReason.Error => "error",
        _ => "user"
    };

    private bool SafePremium()
    {
        try { return ResolvePremium(); }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Script} license lookup failed — reporting as free", _displayName);
            return false;
        }
    }

    /// <summary>
    /// Fire-and-forget, and it has to be: <see cref="Start()"/> is called straight from a global
    /// hotkey callback and must stay synchronous. TrackEventAsync never faults by contract, so
    /// the discarded task cannot resurface on the finalizer thread; resolving the sink is
    /// wrapped because a script still running through app shutdown will find nothing there.
    ///
    /// The payload is the script's own key, three numbers and two flags. No paths, no names, no
    /// identifiers — the script key is one of seventeen fixed strings the app itself defines.
    /// </summary>
    private void Track(string eventName, Dictionary<string, object?> metrics)
    {
        try
        {
            var telemetry = ResolveTelemetry();
            if (telemetry is null) return;

            _ = telemetry.TrackEventAsync(eventName, TelemetryEventStatus.Ok, metrics: metrics);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Script} telemetry push failed for {Event}", _displayName, eventName);
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

        StopCore(notify: false, ScriptStopReason.User);

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
