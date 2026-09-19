using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>User-configurable keys and timings for the Fed-Suit transmitter macro.</summary>
public sealed class FedSuitSettings
{
    /// <summary>
    /// Key that opens the transmitter in game. Defaults to whatever the player bound to ARK's
    /// <c>AccessInventory</c>, the same way <see cref="Scripts.CraftingScript"/> resolves its
    /// access key — a stored preference still wins. Hard-coding "F" here meant a player who
    /// rebound Access Inventory got a macro that opened nothing and then transferred into
    /// whatever happened to be under the cursor, with the tile still reporting cycles.
    /// </summary>
    public string OpenKey { get; set; } = ArkKeyDefaults.For(ArkActions.AccessInventory, "F");

    /// <summary>
    /// Key that closes the transmitter UI. Stays a literal: Escape is the engine's own close-UI
    /// key, not an ARK <c>ActionMapping</c>, so there is nothing in Input.ini to scan for it.
    /// </summary>
    public string ExitKey { get; set; } = "Esc";

    /// <summary>
    /// Key pressed once per worn piece, with the cursor on its slot — the player's own
    /// <c>TransferItem</c> binding, for the same reason as <see cref="OpenKey"/>.
    /// </summary>
    public string TransferKey { get; set; } = ArkKeyDefaults.For(ArkActions.TransferItem, "T");
    /// <summary>
    /// Delay after each armour slot's transfer press, in milliseconds. 40 was tuned for 60 fps
    /// and lost pieces at the frame rate an open ARK inventory actually runs at (30–40): the next
    /// slot's key arrived before the game had registered the cursor sitting on it.
    /// </summary>
    public int PressDelayMs { get; set; } = 70;
    /// <summary>Wait after opening the transmitter before pressing, in milliseconds.</summary>
    public int WaitAfterOpenMs { get; set; } = 500;
    /// <summary>Delay before the next cycle starts, in milliseconds.</summary>
    public int RepeatDelayMs { get; set; } = 150;

    /// <summary>
    /// How many suits to farm before the macro stops itself; 0 keeps going until it is stopped.
    /// One cycle is one suit, so this is the number the player actually thinks in.
    /// </summary>
    public int Runs { get; set; }

    /// <summary>Shallow copy so callers never share a mutable instance with the service.</summary>
    public FedSuitSettings Clone() => (FedSuitSettings)MemberwiseClone();
}

/// <summary>
/// Fed-Suit transmitter automation, for Genesis 2: opening a Tek Transmitter while wearing no
/// federation exo suit puts a fresh set on the player, so each cycle focuses ARK, opens the
/// transmitter, brings the player's own tab to the front, moves the five worn pieces into the
/// transmitter with the transfer key, closes it again — and the next cycle finds a new suit on.
/// Runs as a singleton: it keeps going when the user navigates away from the page, and the
/// Scripts page's own hotkey is what stops it from inside the game.
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

    /// <summary>
    /// Cycles completed by the last run and how long it took, or null before the first one. The
    /// numbers rather than the sentence: this sits on the page for as long as the app is open,
    /// so a summary worded here would keep the language it was worded in through a switch — the
    /// same split CalibratableScriptBase.MaskCoverage uses.
    /// </summary>
    (int Cycles, TimeSpan Elapsed)? LastRun { get; }

    /// <summary>Raised when running state, cycle counters, or settings change. May fire on a background thread.</summary>
    event Action? Changed;

    /// <summary>Validates, persists, and applies new settings.</summary>
    void UpdateSettings(FedSuitSettings settings);

    /// <summary>
    /// Starts the loop. Returns false when already running or a configured key is invalid.
    /// </summary>
    /// <param name="alreadyMetered">
    /// True when the caller has already charged this start against the user's monthly quota.
    /// <see cref="Scripts.FedSuitScript"/> passes it: the Scripts list meters every script start
    /// through the shared scaffold, and this macro predates that, so one toggle of the Fed Suit
    /// tile used to cost the user two of their free runs — one <c>input_scripts</c> and one
    /// <c>fed_suit</c>. Starting the macro on its own (the command palette) still meters here.
    /// </param>
    bool Start(bool alreadyMetered = false);

    /// <summary>Requests a hard stop. Safe from any thread, including hotkey callbacks.</summary>
    void Stop();
}

/// <summary>Default <see cref="IFedSuitMacro"/> implementation built on the shared automation core.</summary>
public sealed class FedSuitMacro : IFedSuitMacro
{
    private const string RunnerName = "fed-suit";

    /// <summary>Let the tab switch draw before the cursor starts hopping between slots.</summary>
    private const int TabSettleMs = 100;

    /// <summary>
    /// How long the cursor rests on a slot before the transfer key is pressed. ARK reads what is
    /// under the cursor once per rendered frame, so a press sent in the same breath as the move
    /// is a press aimed at wherever the cursor was a frame ago. 35 ms was one frame at 30 fps
    /// with nothing to spare, and in game it cost the legs of every other suit — the slot under
    /// the cursor had not become the hovered one yet, tooltip and all, when the key landed.
    /// </summary>
    private const int HoverSettleMs = 80;

    /// <summary>Half-width of the box sampled at each slot centre for the "did anything move?" check.</summary>
    private const int SlotSampleHalf = 6;

    /// <summary>Mean-brightness change (0–255) that counts as a slot having emptied.</summary>
    private const double SlotMovedTolerance = 2.0;

    /// <summary>
    /// Mean-brightness change (0–255) at one slot point that counts as that point having been
    /// covered. Deliberately small: an inventory panel drawn over the world moves those five points
    /// by tens of levels, while a still camera moves them by nothing, so anything in between is
    /// left to the "nothing moved" check rather than stopping a run that is working. A majority of
    /// the five has to clear it before the panel counts as open — see <see cref="StopIfStillShut"/>.
    /// </summary>
    private const double InventoryOpenedTolerance = 4.0;

    /// <summary>Cycles that may move nothing at all before the macro gives up on its own.</summary>
    private const int IdleCyclesBeforeStop = 3;

    private readonly IMacroEngine _engine;
    private readonly IGameDisplayService _displays;
    private readonly IArkPathProvider _arkPath;
    private readonly IScreenSampler _sampler;
    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;
    private readonly ILocalizer _localizer;
    private readonly IUsageGateService _usageGate;
    private readonly ILogger<FedSuitMacro> _logger;
    private readonly IMacroRunner _runner;
    private readonly object _gate = new();

    private FedSuitSettings _settings;
    private volatile bool _running;
    private volatile bool _disposed;
    private bool _stopRequested;
    private int _currentCycle;
    private int _cyclesCompleted;
    private int _lastStepIndex;
    private int _firstTransferStepIndex;
    private int _openKeyStepIndex;
    private int _tabClickStepIndex;
    private (int Cycles, TimeSpan Elapsed)? _lastRun;
    private DateTime _runStartedUtc;

    /// <summary>Where the five slots are this run, and the one box that holds all of them.</summary>
    private Point[] _slotPoints = [];
    private Rectangle _slotsBounds;
    private double[]? _slotsBefore;
    private double[]? _slotsShut;
    private int _idleCycles;

    public FedSuitMacro(
        IMacroEngine engine,
        IGameDisplayService displays,
        IArkPathProvider arkPath,
        IScreenSampler sampler,
        INotificationService notifications,
        IActivityService activity,
        IUsageGateService usageGate,
        ILocalizer localizer,
        ILogger<FedSuitMacro> logger)
    {
        _engine = engine;
        _displays = displays;
        _arkPath = arkPath;
        _sampler = sampler;
        _notifications = notifications;
        _activity = activity;
        _localizer = localizer;
        _usageGate = usageGate;
        _logger = logger;

        _settings = LoadSettings();
        _runner = _engine.GetRunner(RunnerName);
        _runner.StepStarted += OnStepStarted;
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

    public (int Cycles, TimeSpan Elapsed)? LastRun
    {
        get { lock (_gate) return _lastRun; }
    }

    public event Action? Changed;

    public void UpdateSettings(FedSuitSettings settings)
    {
        if (_disposed || settings is null) return;

        var normalized = Normalize(settings);
        lock (_gate) _settings = normalized;

        SaveSettings(normalized);
        RaiseChanged();
    }

    public bool Start(bool alreadyMetered = false)
    {
        if (_disposed) return false;

        // Settings were read once, in the constructor, hours ago. Re-checking the scan here is
        // what lets a key the player never set by hand follow a rebind they made in ARK since
        // the app started. Done on the way in so both start paths — the Scripts tile and the
        // command palette — get it.
        ArkKeyDefaults.RefreshIfStale();

        FedSuitSettings snapshot;
        lock (_gate)
        {
            if (_running) return false;
            _settings = WithRescannedKeys(_settings);
            snapshot = _settings.Clone();
        }

        var plan = BuildPlan(snapshot);
        if (plan is null) return false;

        lock (_gate)
        {
            if (_running) return false; // raced with a second start — first one wins
            _running = true;
            _stopRequested = false;
            _currentCycle = 0;
            _cyclesCompleted = 0;
            _lastStepIndex = plan.Sequence.Steps.Count - 1;
            _firstTransferStepIndex = plan.FirstTransferStep;
            _openKeyStepIndex = plan.OpenKeyStep;
            _tabClickStepIndex = plan.TabClickStep;
            _slotPoints = plan.Slots;
            _slotsBounds = BoxAround(plan.Slots);
            _slotsBefore = null;
            _slotsShut = null;
            _idleCycles = 0;
            _runStartedUtc = DateTime.UtcNow;
        }

        try
        {
            // Only ever the key that really stops it: the macro's own F5/F6 pair was registered
            // by nothing, and this toast named F6 for years with nothing listening to it.
            var hotkey = ScriptHotkey();
            _notifications.ShowInfo(string.IsNullOrWhiteSpace(hotkey)
                ? _localizer.T("scripts.fed.toast.started.nokey")
                : _localizer.T("scripts.fed.toast.started", hotkey));
        }
        catch { /* notifications are best-effort */ }

        _ = Task.Run(() => RunToCompletionAsync(plan.Sequence));
        // Start() must stay synchronous (the global hotkey calls it), so the quota check runs
        // right behind the start and stops the macro again if the month is used up. Stops
        // themselves never count — and neither does a start the Scripts list already charged for.
        if (!alreadyMetered) _ = Task.Run(EnforceQuotaAsync);
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
            _notifications.ShowWarning(_localizer.T("scripts.fed.toast.quota", gate.Limit));
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

        _runner.StepStarted -= OnStepStarted;
    }

    // ─── Run lifecycle ─────────────────────────────────────────────────────────

    private async Task RunToCompletionAsync(MacroSequence sequence)
    {
        var ran = false;
        try
        {
            ran = await _runner.RunAsync(sequence);
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
                _lastRun = (cycles, elapsed);
            }

            try
            {
                // A run nobody stopped that did not finish its loops is the focus step refusing
                // to hand the game the foreground — mid-run just as much as on the first cycle.
                // Reporting that as "stopped, 12 cycles" hid an alt-tab that left every press
                // after it landing in whatever window took the focus.
                if (stoppedByUser || ran)
                    _notifications.ShowInfo(_localizer.T(
                        cycles == 1 ? "scripts.fed.toast.stopped.one" : "scripts.fed.toast.stopped.many",
                        cycles));
                else
                    _notifications.ShowWarning(_localizer.T("scripts.fed.toast.noark"));

                _activity.AddActivity(
                    _localizer.T(
                        cycles == 1 ? "scripts.fed.activity.run.one" : "scripts.fed.activity.run.many",
                        cycles,
                        FormatDuration(elapsed)),
                    cycles > 0 ? "success" : "warning",
                    cycles == 1 ? "scripts.fed.activity.run.one" : "scripts.fed.activity.run.many");
            }
            catch { /* notifications/activity are best-effort */ }

            RaiseChanged();
        }
    }

    private void OnStepStarted(int stepIndex, int loopNumber)
    {
        if (!_running) return;

        // Outside the lock: these grab pixels off the screen, and the step loop is waiting on this
        // callback. See SampleSlots for why each one is a single capture and not five.
        if (stepIndex == _openKeyStepIndex) _slotsShut = SampleSlots();
        else if (stepIndex == _tabClickStepIndex) StopIfStillShut();
        else if (stepIndex == _firstTransferStepIndex) _slotsBefore = SampleSlots();
        else if (stepIndex == _lastStepIndex) StopIfNothingMoved();

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

    /// <summary>One cycle's steps, plus the steps the two screen checks hang off.</summary>
    private sealed record CyclePlan(
        MacroSequence Sequence, Point[] Slots, int OpenKeyStep, int TabClickStep, int FirstTransferStep);

    private CyclePlan? BuildPlan(FedSuitSettings s)
    {
        if (!FedSuitKeys.TryParseKey(s.OpenKey, out var openVk))
        {
            NotifyBadKey("Open Transmitter", _localizer.T("scripts.fed.toast.badkey.open", s.OpenKey));
            return null;
        }
        if (!FedSuitKeys.TryParseKey(s.ExitKey, out var exitVk))
        {
            NotifyBadKey("Exit Transmitter", _localizer.T("scripts.fed.toast.badkey.exit", s.ExitKey));
            return null;
        }
        if (!FedSuitKeys.TryParseKey(s.TransferKey, out var transferVk))
        {
            NotifyBadKey("Transfer", _localizer.T("scripts.fed.toast.badkey.transfer", s.TransferKey));
            return null;
        }

        // Where the panel is drawn, worked out rather than calibrated — see ArkInventoryLayout.
        // Without a window there is no centre to measure from, and every click would land on
        // the desktop instead.
        var client = _displays.GameClientBounds;
        if (client.Width <= 0 || client.Height <= 0)
        {
            _logger.LogWarning("Fed-Suit cannot place its clicks — ARK has no readable window");
            try { _notifications.ShowWarning(_localizer.T("scripts.fed.toast.noark")); }
            catch { /* notifications are best-effort */ }
            return null;
        }

        var uiScaling = ArkInventoryLayout.ReadUiScaling(_arkPath.FindArkPath());
        var tab = ArkInventoryLayout.PlayerTab(client, uiScaling);
        var slots = ArkInventoryLayout.ArmorSlots(client, uiScaling);

        var steps = new List<MacroStep> { MacroStep.FocusGameWindow() };

        var openKeyStep = steps.Count;
        steps.Add(MacroStep.KeyPress(openVk));
        steps.Add(MacroStep.Delay(s.WaitAfterOpenMs));

        // The transmitter opens with its own tab in front, and the transfer key moves what the
        // panel in front is showing — so without this click the whole cycle transfers nothing,
        // which is exactly what it used to do. It is also the first step that touches the mouse,
        // which is why the "did it open at all?" check sits in front of it.
        var tabClickStep = steps.Count;
        steps.Add(MacroStep.ClickAt(tab.X, tab.Y));
        steps.Add(MacroStep.Delay(TabSettleMs));

        var firstTransferStep = steps.Count;
        foreach (var slot in slots)
        {
            steps.Add(MacroStep.MoveTo(slot.X, slot.Y));
            steps.Add(MacroStep.Delay(HoverSettleMs));
            steps.Add(MacroStep.KeyPress(transferVk));
            steps.Add(MacroStep.Delay(s.PressDelayMs));
        }

        // Back to the tab before the last look at the slots: the cursor highlights whatever it
        // rests on, and a slot lit up by the cursor would read as "something moved" every cycle.
        steps.Add(MacroStep.MoveTo(tab.X, tab.Y));
        steps.Add(MacroStep.Delay(HoverSettleMs));
        steps.Add(MacroStep.KeyPress(exitVk));

        return new CyclePlan(
            new MacroSequence
            {
                Name = "Fed-Suit",
                Steps = steps,
                RepeatCount = s.Runs, // 0 = until stopped
                LoopDelayMs = s.RepeatDelayMs
            },
            slots,
            openKeyStep,
            tabClickStep,
            firstTransferStep);
    }

    /// <summary>
    /// Stops the run before the tab is clicked when the open key changed nothing on screen — the
    /// transmitter is out of reach, the server is lagging, or the key found no inventory to open.
    /// Runs on the same two captures the "nothing moved" check uses, taken either side of the open
    /// key instead of either side of the transfers.
    ///
    /// This is the step where the macro stops being harmless: everything before it is one key
    /// press, everything after it is clicks and cursor jumps that land in gameplay when no panel
    /// is there to catch them — the camera swings to the sky and the character punches. Cancelling
    /// from here keeps the click that follows from ever being sent (see IMacroRunner.StepStarted).
    ///
    /// ponytail: a difference, not a recognition. It catches the key that did nothing; it cannot
    /// tell an inventory opening from one our own key press just closed, so a run started with an
    /// inventory already open still fires one out-of-phase cycle before this catches the next.
    /// Upgrade path is a template match on the panel, which needs an ARK UI asset shipped with it.
    ///
    /// ponytail: the tolerance itself is unverified outdoors — the only scene it was ever read
    /// against is a night one with a still camera. The majority rule below is what buys headroom
    /// against daytime foliage and VFX until somebody tests one; a whole-scene brightness change
    /// (sunrise, a lightning flash, the camera swinging as ARK takes the foreground) moves all
    /// five points at once and still reads as "opened".
    /// </summary>
    private void StopIfStillShut()
    {
        var shut = _slotsShut;
        _slotsShut = null;
        if (shut is null) return;

        var opened = SampleSlots();
        if (opened is null || opened.Length != shut.Length) return; // a capture that failed proves nothing

        // A majority of the five, not the first one that differs. The player stands facing a Tek
        // Transmitter's own particle beam, and that — or wind-blown foliage, or a creature walking
        // through one sample point — swings a single point's mean past the tolerance with the
        // inventory still shut, which used to be enough to wave the cycle through into clicks the
        // game has no panel to catch. A panel that really opened covers all five.
        var moved = 0;
        for (var i = 0; i < shut.Length; i++)
            if (Math.Abs(shut[i] - opened[i]) > InventoryOpenedTolerance) moved++;

        if (moved * 2 > shut.Length) return;

        _logger.LogWarning("Fed-Suit stopped itself — the open key left the screen unchanged");
        Stop();
        try { _notifications.ShowWarning(_localizer.T("scripts.fed.toast.notopen")); }
        catch { /* notifications are best-effort */ }
    }

    // ─── "Nothing moved" check ─────────────────────────────────────────────────
    //
    // ponytail: this is over the ~60-line budget the spec put on wiring vision into the macro
    // engine — the four methods below are ~67 lines, ~110 with the fields, the plan hook, the
    // toast and its four translations. Kept rather than skipped because the cheap reuse the
    // budget assumed is not there: AverageColor captures its own region, so a mean per slot
    // costs five grabs inside the step loop, and CaptureReference persists a ~280 KB snapshot
    // to disk every cycle. If the size is not worth it the region deletes cleanly, at the price
    // of a mis-aimed run looping silently again — which is the thing it was written for.

    /// <summary>
    /// Stops the run when three cycles in a row left all five slots looking exactly as they did
    /// before the transfers. Silence is this macro's failure mode: a mis-aimed click or the wrong
    /// place to stand loops forever with the tile happily counting cycles, which is how the old
    /// version wasted whole evenings.
    /// </summary>
    private void StopIfNothingMoved()
    {
        var before = _slotsBefore;
        _slotsBefore = null;
        if (before is null) return;

        var after = SampleSlots();
        if (after is null || after.Length != before.Length) return; // a capture that failed proves nothing

        for (var i = 0; i < before.Length; i++)
        {
            if (Math.Abs(before[i] - after[i]) <= SlotMovedTolerance) continue;
            _idleCycles = 0;
            return;
        }

        if (++_idleCycles < IdleCyclesBeforeStop) return;

        _logger.LogWarning("Fed-Suit stopped itself — {Cycles} cycles moved nothing", _idleCycles);
        Stop();
        try { _notifications.ShowWarning(_localizer.T("scripts.fed.toast.nomove")); }
        catch { /* notifications are best-effort */ }
    }

    /// <summary>
    /// Mean brightness of a small box at each slot centre, all five out of a single capture of
    /// the box that holds them: this runs inside the macro's own step loop, so five separate
    /// grabs would stand between the last transfer and the key that ends the cycle.
    /// </summary>
    private double[]? SampleSlots()
    {
        try
        {
            var slots = _slotPoints;
            if (slots.Length == 0) return null;

            var bounds = _slotsBounds;
            var capture = _sampler.CaptureRegion(bounds);
            if (capture.IsEmpty) return null;

            var means = new double[slots.Length];
            for (var i = 0; i < slots.Length; i++) means[i] = MeanAt(capture, bounds, slots[i]);
            return means;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fed-Suit slot sample failed — the cycle is not judged");
            return null;
        }
    }

    private static double MeanAt(ScreenCapture capture, Rectangle origin, Point centre)
    {
        long sum = 0;
        var channels = 0;
        for (var y = centre.Y - SlotSampleHalf; y <= centre.Y + SlotSampleHalf; y++)
        {
            var row = y - origin.Y;
            if (row < 0 || row >= capture.Height) continue;

            for (var x = centre.X - SlotSampleHalf; x <= centre.X + SlotSampleHalf; x++)
            {
                var column = x - origin.X;
                if (column < 0 || column >= capture.Width) continue;

                var i = (row * capture.Width + column) * 4;
                sum += capture.Bgra[i] + capture.Bgra[i + 1] + capture.Bgra[i + 2];
                channels += 3;
            }
        }
        return channels == 0 ? 0 : sum / (double)channels;
    }

    private static Rectangle BoxAround(Point[] slots)
    {
        if (slots.Length == 0) return Rectangle.Empty;

        var left = slots.Min(p => p.X) - SlotSampleHalf;
        var top = slots.Min(p => p.Y) - SlotSampleHalf;
        var right = slots.Max(p => p.X) + SlotSampleHalf;
        var bottom = slots.Max(p => p.Y) + SlotSampleHalf;
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    /// <summary>
    /// The log keeps the English label — that line is read in a file by whoever is debugging —
    /// while the toast is a whole sentence per key rather than a translated word dropped into an
    /// English frame. "{0} key is not supported" cannot be translated once: the three languages
    /// put the noun somewhere else in the sentence.
    /// </summary>
    private void NotifyBadKey(string label, string message)
    {
        _logger.LogWarning("Fed-Suit {Label} key could not be parsed", label);
        try { _notifications.ShowError(message); }
        catch { /* notifications are best-effort */ }
    }

    // ─── Settings persistence ──────────────────────────────────────────────────

    /// <summary>
    /// The hotkey the Scripts page holds for this script, which is the only one that can stop a
    /// run from inside the game. Read straight out of the preference rather than through the
    /// script: the command palette starts the macro without one, and an empty string is the
    /// honest answer there — no key is bound by default.
    /// </summary>
    private static string ScriptHotkey()
    {
        try { return Preferences.Get("script.fedsuit.hotkey", string.Empty); }
        catch { return string.Empty; }
    }

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
                PressDelayMs = Preferences.Get("fedsuit.pressdelay", defaults.PressDelayMs),
                WaitAfterOpenMs = Preferences.Get("fedsuit.openwait", defaults.WaitAfterOpenMs),
                RepeatDelayMs = Preferences.Get("fedsuit.repeatdelay", defaults.RepeatDelayMs),
                Runs = Preferences.Get("fedsuit.runs", defaults.Runs)
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
            Preferences.Set("fedsuit.pressdelay", s.PressDelayMs);
            Preferences.Set("fedsuit.openwait", s.WaitAfterOpenMs);
            Preferences.Set("fedsuit.repeatdelay", s.RepeatDelayMs);
            Preferences.Set("fedsuit.runs", s.Runs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fed-Suit settings save failed");
        }
    }

    /// <summary>
    /// The two keys that name an ARK action, re-taken from the scan when the player never set one
    /// by hand. Only those two, and only when unset: a stored preference is their own choice and
    /// still wins, and reloading the whole settings block instead would revert a change whose save
    /// had failed. A scanned key the macro cannot press is dropped by <see cref="NormalizeKey"/>,
    /// which is the same guard the stored ones already go through.
    /// </summary>
    private FedSuitSettings WithRescannedKeys(FedSuitSettings current)
    {
        var next = current.Clone();
        try
        {
            if (!Preferences.ContainsKey("fedsuit.openkey"))
                next.OpenKey = NormalizeKey(ArkKeyDefaults.For(ArkActions.AccessInventory, current.OpenKey), current.OpenKey);

            if (!Preferences.ContainsKey("fedsuit.transferkey"))
                next.TransferKey = NormalizeKey(ArkKeyDefaults.For(ArkActions.TransferItem, current.TransferKey), current.TransferKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fed-Suit key re-resolve failed — keeping the keys already loaded");
            return current;
        }

        return next;
    }

    private static FedSuitSettings Normalize(FedSuitSettings s)
    {
        var d = new FedSuitSettings();
        return new FedSuitSettings
        {
            OpenKey = NormalizeKey(s.OpenKey, d.OpenKey),
            ExitKey = NormalizeKey(s.ExitKey, d.ExitKey),
            TransferKey = NormalizeKey(s.TransferKey, d.TransferKey),
            PressDelayMs = Math.Clamp(s.PressDelayMs, 0, 10_000),
            WaitAfterOpenMs = Math.Clamp(s.WaitAfterOpenMs, 0, 30_000),
            RepeatDelayMs = Math.Clamp(s.RepeatDelayMs, 0, 60_000),
            Runs = Math.Clamp(s.Runs, 0, 9_999)
        };
    }

    private static string NormalizeKey(string? value, string fallback)
        => !string.IsNullOrWhiteSpace(value) && FedSuitKeys.TryParseKey(value, out _)
            ? value.Trim()
            : fallback;

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* subscriber errors must not kill the macro */ }
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }
}

/// <summary>
/// Parses a single key name ("F", "Esc", "T") into a Win32 virtual-key code. Shared by the
/// Fed-Suit macro; internal so other automation features in this assembly can reuse it. Combos
/// belong to <see cref="HotkeyParser"/> — this one only ever describes a key the macro presses.
/// </summary>
internal static class FedSuitKeys
{
    /// <summary>Parses a single key name into a virtual-key code.</summary>
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
