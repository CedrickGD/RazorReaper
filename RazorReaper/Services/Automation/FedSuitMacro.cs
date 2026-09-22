using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>The five pieces of the exo suit, as the player picks which ones the macro moves.</summary>
[Flags]
public enum FedSuitPieces
{
    None = 0,
    Head = 1,
    Chest = 2,
    Hands = 4,
    Legs = 8,
    Feet = 16,
    All = Head | Chest | Hands | Legs | Feet
}

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
    /// Pause after each transfer press before the cursor moves to the next slot, in milliseconds,
    /// so the frame that reads the key still finds the cursor on the piece. Short now that every
    /// piece is checked afterwards and pressed again when it stayed.
    /// </summary>
    public int PressDelayMs { get; set; } = 50;

    /// <summary>
    /// The longest the macro waits for the transmitter to open, in milliseconds. Not a fixed
    /// sleep any more: the cycle carries on the moment the panel is on screen.
    /// </summary>
    public int WaitAfterOpenMs { get; set; } = 1500;

    /// <summary>Delay before the next cycle starts, in milliseconds.</summary>
    public int RepeatDelayMs { get; set; } = 150;

    /// <summary>
    /// Added to the waits the server decides — the transmitter opening, the new set arriving, the
    /// pieces leaving — in milliseconds. The one knob for a laggy server; the client's own steps
    /// (hover, tab, close) never wait for it.
    /// </summary>
    public int LagBufferMs { get; set; }

    /// <summary>Which worn pieces go into the transmitter; the others stay on the player.</summary>
    public FedSuitPieces Pieces { get; set; } = FedSuitPieces.All;

    /// <summary>
    /// How many suits to farm before the macro stops itself; 0 keeps going until it is stopped.
    /// One cycle is one suit, so this is the number the player actually thinks in.
    /// </summary>
    public int Runs { get; set; }

    /// <summary>Shallow copy so callers never share a mutable instance with the service.</summary>
    public FedSuitSettings Clone() => (FedSuitSettings)MemberwiseClone();

    /// <summary>
    /// The piece each <see cref="ArkInventoryLayout.ArmorSlots"/> entry holds, in that list's
    /// order: head, chest, legs down the left column, then hands and feet on the right.
    /// </summary>
    private static readonly FedSuitPieces[] SlotPieces =
        [FedSuitPieces.Head, FedSuitPieces.Chest, FedSuitPieces.Legs, FedSuitPieces.Hands, FedSuitPieces.Feet];

    /// <summary>Indices into <see cref="ArkInventoryLayout.ArmorSlots"/> of the picked pieces, head first.</summary>
    public static int[] SlotsFor(FedSuitPieces pieces)
        => Enumerable.Range(0, SlotPieces.Length).Where(i => pieces.HasFlag(SlotPieces[i])).ToArray();
}

/// <summary>
/// Fed-Suit transmitter automation, for Genesis 2: opening a Tek Transmitter while wearing no
/// federation exo suit puts a fresh set on the player, so each cycle focuses ARK, opens the
/// transmitter, brings the player's own tab to the front, moves the worn pieces into the
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
    /// Re-asks the ARK key scan for the open and transfer keys the player never typed themselves
    /// (see <see cref="ArkKeyDefaults.Follow"/>). The Scripts page's Rescan; ignored mid-run.
    /// </summary>
    void FollowArkKeys();

    /// <summary>
    /// Starts the loop. Returns false when already running, a configured key is invalid, or no
    /// piece is picked.
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

/// <summary>
/// Default <see cref="IFedSuitMacro"/> implementation. The cycle is driven from code and checks
/// the screen at every step instead of sleeping on fixed timers: the old static step list lost a
/// piece every few suits on a slightly slow server — the set was not on the player yet, or a press
/// landed before the hover had — and then closed the transmitter with an odd count inside.
/// </summary>
public sealed class FedSuitMacro : IFedSuitMacro
{
    private const string RunnerName = "fed-suit";

    /// <summary>How often a wait looks at the screen again, in milliseconds.</summary>
    private const int PollMs = 30;

    /// <summary>Let the tab switch draw before the slots are read.</summary>
    private const int TabSettleMs = 60;

    /// <summary>
    /// How long the cursor rests on a slot before the transfer key is pressed. ARK reads what is
    /// under the cursor once per rendered frame, so a press sent in the same breath as the move is
    /// a press aimed at wherever the cursor was a frame ago. Short: a press that missed is caught
    /// by the check after the round and sent again.
    /// </summary>
    private const int HoverSettleMs = 50;

    /// <summary>
    /// How long the transmitter gets to put the new set on the player once the player's tab is
    /// up. It is handed out when the transmitter opens and a worn piece is recognised the frame it
    /// shows, so this only runs out on a lagging server — the lag buffer is added to it. A piece
    /// still missing then is not waited for: the others go, and the next cycle tries again.
    /// </summary>
    private const int SetTimeoutMs = 1000;

    /// <summary>
    /// How long the pressed pieces get to leave their slots, per round of presses, while none of
    /// them has gone yet — the server has not answered. The lag buffer is added to it.
    /// </summary>
    private const int LeaveTimeoutMs = 600;

    /// <summary>
    /// Once a piece of the round has left, how long the rest may stay unchanged before they count
    /// as dropped presses and are pressed again. The server answers a round's presses together, so
    /// a piece still worn this long after its neighbours went was never taken — waiting out
    /// <see cref="LeaveTimeoutMs"/> for it only slowed every such cycle down.
    /// </summary>
    private const int LeaveQuietMs = 150;

    /// <summary>How long the panel gets to settle after it opened or closed before the next step.</summary>
    private const int SettleTimeoutMs = 300;

    /// <summary>How long the transmitter gets to close after the exit key.</summary>
    private const int CloseTimeoutMs = 1500;

    /// <summary>
    /// Rounds of presses a piece gets within one cycle. A piece still worn after them is not the
    /// end of the run: the transmitter closes, opens again, and the piece is pressed in the next
    /// cycle — see <see cref="StuckAfterCycles"/> and <see cref="FullAfterIdleCycles"/>.
    /// </summary>
    private const int MaxPresses = 8;

    /// <summary>
    /// Cycles in a row the same slot may fail — its piece never arrived, or never left — before
    /// the run stops. One bad cycle is lag; three in a row on the same slot is something the
    /// player has to look at.
    /// </summary>
    private const int StuckAfterCycles = 3;

    /// <summary>
    /// Cycles in a row whose presses moved nothing although pieces were there before the run
    /// stops with "the transmitter seems full". Two, so a single cycle lost to lag carries on.
    /// </summary>
    private const int FullAfterIdleCycles = 2;

    /// <summary>
    /// Half-width of the box sampled at each slot centre for the open and close checks, at 1080p
    /// and interface scale 1.0. Scaled with the panel. Worn or not is judged by
    /// <see cref="ArmorSlotLook"/> over the slot's whole interior, not by this box.
    /// </summary>
    private const int SlotSampleHalf = 6;

    /// <summary>
    /// Mean-brightness change (0–255) at one slot point that counts as that point having been
    /// covered. Deliberately small: an inventory panel drawn over the world moves those five points
    /// by tens of levels, while a still camera moves them by nothing. A majority of the five has to
    /// clear it before the panel counts as open or closed — see <see cref="Majority"/>.
    /// </summary>
    private const double InventoryOpenedTolerance = 4.0;

    private readonly IMacroEngine _engine;
    private readonly IGameDisplayService _displays;
    private readonly IArkPathProvider _arkPath;
    private readonly IScreenSampler _sampler;
    private readonly IInputSimulator _input;
    private readonly IForegroundGate _foreground;
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
    private CancellationTokenSource? _cts;
    private int _currentCycle;
    private int _cyclesCompleted;
    private int _piecesMoved;
    private (int Cycles, TimeSpan Elapsed)? _lastRun;
    private DateTime _runStartedUtc;

    public FedSuitMacro(
        IMacroEngine engine,
        IGameDisplayService displays,
        IArkPathProvider arkPath,
        IScreenSampler sampler,
        IInputSimulator input,
        IForegroundGate foreground,
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
        _input = input;
        _foreground = foreground;
        _notifications = notifications;
        _activity = activity;
        _localizer = localizer;
        _usageGate = usageGate;
        _logger = logger;

        _settings = LoadSettings();
        _runner = _engine.GetRunner(RunnerName);
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
        FedSuitSettings before;
        lock (_gate)
        {
            before = _settings;
            _settings = normalized;
        }

        SaveSettings(normalized, before);
        RaiseChanged();
    }

    public void FollowArkKeys()
    {
        lock (_gate)
        {
            if (_running) return;
            _settings = WithRescannedKeys(_settings);
        }
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

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running) return false; // raced with a second start — first one wins
            _running = true;
            _stopRequested = false;
            _cts = cts = new CancellationTokenSource();
            _currentCycle = 0;
            _cyclesCompleted = 0;
            _piecesMoved = 0;
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

        _ = Task.Run(() => RunToCompletionAsync(plan, cts));
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
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running) return;
            _stopRequested = true;
            cts = _cts;
        }
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the run finished between the lock and here */ }
        _runner.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); }
        catch { /* best-effort while shutting down */ }
    }

    // ─── Run lifecycle ─────────────────────────────────────────────────────────

    /// <summary>How a run ended — which toast it gets.</summary>
    private enum Outcome { Finished, Stopped, NoArk, NotOpen, NotClosed, Stuck, Full }

    /// <summary>Thrown by an input helper when ARK is no longer the window in front.</summary>
    private sealed class FocusLostException : Exception;

    /// <summary>
    /// The runner's part of a run: focus ARK once, then hold. The hold is a wait only a stop ends,
    /// and it is what keeps the run visible to everything that asks the runners whether a macro is
    /// busy (the update gate) — the cycles themselves are driven from here, because a fixed list of
    /// steps cannot wait for the game or press a missed piece again.
    /// </summary>
    private static MacroSequence HoldSequence() => new()
    {
        Name = "Fed-Suit",
        Steps = [MacroStep.FocusGameWindow(), MacroStep.Delay(int.MaxValue)],
        RepeatCount = 1
    };

    private async Task RunToCompletionAsync(CyclePlan plan, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var outcome = Outcome.Stopped;
        Task<bool>? hold = null;

        // Step 1 is the hold: reaching it means the focus step handed ARK the foreground.
        var focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, int> onStep = (step, _) => { if (step == 1) focused.TrySetResult(); };
        _runner.StepStarted += onStep;

        try
        {
            hold = _runner.RunAsync(HoldSequence(), ct);
            outcome = await Task.WhenAny(focused.Task, hold) == focused.Task
                ? await RunCyclesAsync(plan, ct)
                : Outcome.NoArk;
        }
        catch (FocusLostException)
        {
            // An alt-tab mid-run: every press after it would land in whatever window took the focus.
            _logger.LogWarning("Fed-Suit stopped itself — ARK lost the foreground");
            outcome = Outcome.NoArk;
        }
        catch (OperationCanceledException)
        {
            outcome = Outcome.Stopped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fed-Suit macro run crashed");
        }
        finally
        {
            _runner.StepStarted -= onStep;
            _runner.Stop();
            if (hold is not null)
            {
                try { await hold; }
                catch { /* the runner reports its own failures */ }
            }

            int cycles, pieces;
            TimeSpan elapsed;
            bool stoppedByUser;
            lock (_gate)
            {
                _running = false;
                stoppedByUser = _stopRequested;
                _cts = null;
                cycles = _cyclesCompleted;
                pieces = _piecesMoved;
                _currentCycle = 0;
                elapsed = DateTime.UtcNow - _runStartedUtc;
                _lastRun = (cycles, elapsed);
            }
            cts.Dispose();

            // A stop from outside wins over whatever the cycle was in the middle of.
            if (stoppedByUser) outcome = Outcome.Stopped;

            try
            {
                switch (outcome)
                {
                    case Outcome.NoArk:
                        _notifications.ShowWarning(_localizer.T("scripts.fed.toast.noark"));
                        break;
                    case Outcome.NotOpen:
                        _notifications.ShowWarning(_localizer.T("scripts.fed.toast.notopen"));
                        break;
                    case Outcome.NotClosed:
                        _notifications.ShowWarning(_localizer.T("scripts.fed.toast.notclosed", cycles, pieces));
                        break;
                    case Outcome.Stuck:
                        _notifications.ShowWarning(_localizer.T("scripts.fed.toast.stuck", cycles, pieces));
                        break;
                    case Outcome.Full:
                        _notifications.ShowWarning(_localizer.T("scripts.fed.toast.full", cycles, pieces));
                        break;
                    default:
                        _notifications.ShowInfo(_localizer.T("scripts.fed.toast.stopped", cycles, pieces));
                        break;
                }

                _activity.AddActivity(
                    _localizer.T("scripts.fed.activity.run", cycles, pieces, FormatDuration(elapsed)),
                    cycles > 0 ? "success" : "warning",
                    "scripts.fed.activity.run");
            }
            catch { /* notifications/activity are best-effort */ }

            RaiseChanged();
        }
    }

    /// <summary>
    /// The cycles, each one checked against the screen before it moves on:
    /// open → wait for the panel → player tab → wait for the picked pieces → press them in one
    /// round → check which left → press the rest again (up to MaxPresses) → close → wait
    /// for the panel to be gone. A run that reached its count opens once more and closes right
    /// away, so the player ends it wearing a set.
    ///
    /// A piece that did not arrive in time, or did not leave, does not end the run: the pieces
    /// that are there go, the transmitter closes and opens again, and the next cycle presses
    /// whatever is still worn. The run stops only when it is really over — two cycles in a row
    /// moved nothing although the pieces were there (the transmitter is full), or the same slot
    /// failed in three cycles in a row.
    ///
    /// The lag buffer goes where the server decides the timing: the open, the set arriving and the
    /// pieces leaving. Everything the client alone decides — hover, tab, close — runs at its own pace.
    /// </summary>
    private async Task<Outcome> RunCyclesAsync(CyclePlan p, CancellationToken ct)
    {
        var s = p.Settings;
        var lag = s.LagBufferMs;

        // Cycles in a row each slot's piece failed — never arrived, or never left.
        var failures = new int[p.Slots.Length];
        // Cycles in a row whose presses moved nothing although pieces were there.
        var idleCycles = 0;

        for (var cycle = 1; s.Runs <= 0 || cycle <= s.Runs; cycle++)
        {
            lock (_gate) _currentCycle = cycle;
            RaiseChanged();

            var (shut, opened) = await OpenAsync(p, ct);
            if (!opened)
            {
                // Before the tab click on purpose: everything after it is clicks and cursor jumps
                // that land in gameplay when no panel is there to catch them.
                _logger.LogWarning("Fed-Suit stopped itself — the open key left the screen unchanged");
                return Outcome.NotOpen;
            }

            // The panel fades in; a click on its first frame is a click the game can drop.
            await SettleAsync(p, lag, ct);

            // The transmitter opens with its own tab in front, and the transfer key moves what the
            // panel in front is showing — so without this click the whole cycle transfers nothing.
            await ClickAsync(p.Tab, ct);
            await DelayAsync(TabSettleMs, ct);

            var (start, _) = await PollAsync(p, SetTimeoutMs + lag, now => p.Selected.All(i => now.Filled[i]), ct);

            // Whatever is there goes now; a piece that has not shown up is the next cycle's.
            var pending = p.Selected.Where(i => start?.Filled[i] == true).ToList();
            var arrived = pending.Count;
            var failed = p.Selected.Except(pending).ToHashSet();
            var openLook = start;

            for (var round = 0; round < MaxPresses && pending.Count > 0; round++)
            {
                foreach (var i in pending)
                {
                    await MoveAsync(p.Slots[i], ct);
                    await DelayAsync(HoverSettleMs, ct);
                    await PressAsync(p.TransferVk, ct);
                    await DelayAsync(s.PressDelayMs, ct);
                }

                // Off the slots before they are read: the cursor lights up whatever it rests on,
                // and the tooltip of the last piece hovered covers its neighbours.
                //
                // ponytail: a tooltip still fading when the check runs hides the piece under it,
                // which then reads as gone. It is still worn, so the next cycle presses it again;
                // only the pieces count is one high. Upgrade path: two agreeing samples.
                await MoveAsync(p.Tab, ct);
                await DelayAsync(HoverSettleMs, ct);

                // Done when every pressed piece left — or when some did and the rest have sat
                // unchanged for LeaveQuietMs since: those presses were dropped, and the next round
                // presses them again now instead of after the full timeout. Nothing gone yet is the
                // server still thinking, and gets the whole LeaveTimeoutMs + lag.
                var check = pending.ToArray();
                int gone = 0, quietMs = 0;
                var (after, _) = await PollAsync(p, LeaveTimeoutMs + lag, now =>
                {
                    var left = check.Count(i => !now.Filled[i]);
                    if (left == check.Length) return true;
                    if (left != gone) (gone, quietMs) = (left, 0);
                    else if (gone > 0) quietMs += PollMs;
                    return quietMs >= LeaveQuietMs;
                }, ct);
                if (after is null) continue;

                openLook = after;
                pending.RemoveAll(i => !after.Filled[i]);
            }

            var moved = arrived - pending.Count;
            failed.UnionWith(pending);
            foreach (var i in p.Selected) failures[i] = failed.Contains(i) ? failures[i] + 1 : 0;
            if (moved > 0) idleCycles = 0;
            else if (arrived > 0) idleCycles++;

            lock (_gate)
            {
                _piecesMoved += moved;
                if (moved > 0) _cyclesCompleted++;
            }
            RaiseChanged();

            // Left open on purpose: the player looks at what the stop is about.
            if (idleCycles >= FullAfterIdleCycles)
            {
                _logger.LogWarning(
                    "Fed-Suit stopped itself — {Cycles} cycles in a row moved nothing, the transmitter seems full", idleCycles);
                return Outcome.Full;
            }
            if (p.Selected.Any(i => failures[i] >= StuckAfterCycles))
            {
                _logger.LogWarning("Fed-Suit stopped itself — the same piece failed {Cycles} cycles in a row", StuckAfterCycles);
                return Outcome.Stuck;
            }
            if (failed.Count > 0)
            {
                _logger.LogInformation(
                    "Fed-Suit cycle {Cycle}: {Stuck} piece(s) did not move, {Missing} never arrived — carrying on",
                    cycle, pending.Count, failed.Count - pending.Count);
            }

            if (!await CloseAsync(p, shut!, openLook, ct))
            {
                _logger.LogWarning("Fed-Suit stopped itself — the transmitter did not close");
                return Outcome.NotClosed;
            }

            // The fade-out, again: the next cycle's "before" look has to be the world, not the panel on its way out.
            await SettleAsync(p, 0, ct);
            await DelayAsync(s.RepeatDelayMs, ct);
        }

        // Only a run that reached its count gets here — every stop rule returns above, and an
        // endless run only ends by a stop. The last cycle took the suit off; one more open puts a
        // fresh one on, and it closes straight away with nothing pressed, so the player walks off
        // wearing it. Not counted, and a transmitter that will not open again is no warning: the
        // run itself went fine.
        var (lastShut, lastOpened) = await OpenAsync(p, ct);
        if (!lastOpened)
        {
            _logger.LogInformation("Fed-Suit finished without a last set — the transmitter did not open again");
            return Outcome.Finished;
        }

        var panel = await SettleAsync(p, lag, ct);
        if (!await CloseAsync(p, lastShut!, panel, ct))
        {
            _logger.LogWarning("Fed-Suit finished, but the transmitter did not close after the last set");
            return Outcome.NotClosed;
        }

        return Outcome.Finished;
    }

    /// <summary>
    /// Presses the open key on a shut screen and waits for the panel. Returns the look from before
    /// the key — the close check goes back to it — and whether the panel came up.
    /// </summary>
    private async Task<(SlotsLook? Shut, bool Opened)> OpenAsync(CyclePlan p, CancellationToken ct)
    {
        var (shut, _) = await PollAsync(p, 0, _ => true, ct);
        if (shut is null) return (null, false);

        await PressAsync(p.OpenVk, ct);

        // A majority of the five, not the first one that differs. The player stands facing a
        // Tek Transmitter's own particle beam, and that — or wind-blown foliage, or a creature
        // walking through one sample point — swings a single point's mean past the tolerance
        // with the inventory still shut. A panel that really opened covers all five.
        //
        // ponytail: a difference, not a recognition — it cannot tell an inventory opening from
        // one our own key press just closed. The close check at the end of every cycle is what
        // keeps the next open key from ever landing on an open panel. Cycle 1 has no such
        // check: a run started on an open panel (a stuck stop leaves it open, for the player
        // to look at) relies on the open key doing nothing there, which reads as
        // "did not open" — and that toast asks for open inventories to be closed first. Were
        // the key a toggle in ARK, the close would pass for an open; upgrade path is a
        // template match on the panel.
        var (_, opened) = await PollAsync(
            p, p.Settings.WaitAfterOpenMs + p.Settings.LagBufferMs,
            now => Majority(now.Means, shut.Means, InventoryOpenedTolerance, differ: true), ct);
        return (shut, opened);
    }

    /// <summary>Presses the exit key and waits for the panel to be gone; false when it stayed.</summary>
    private async Task<bool> CloseAsync(CyclePlan p, SlotsLook shut, SlotsLook? openLook, CancellationToken ct)
    {
        await PressAsync(p.ExitVk, ct);

        // Gone from the panel's look, or back to the look from before the open — either one
        // says the panel is shut. Only the first would miss a camera nudged on close; only the
        // second would miss a night scene as dark as an empty slot. No lag buffer: closing is
        // the client's own business, and the poll carries on the moment the panel is gone.
        var (_, closed) = await PollAsync(
            p, CloseTimeoutMs,
            now => (openLook is not null && Majority(now.Means, openLook.Means, InventoryOpenedTolerance, differ: true))
                || Majority(now.Means, shut.Means, InventoryOpenedTolerance, differ: false),
            ct);
        return closed;
    }

    /// <summary>
    /// Samples until <paramref name="done"/> holds or <paramref name="timeoutMs"/> has gone by;
    /// returns the last sample either way and whether it held.
    ///
    /// ponytail: the timeout is counted in poll intervals, not read off a clock, so a slow capture
    /// stretches it a little. The upside is a test can drive every wait with instant delays.
    /// </summary>
    private async Task<(SlotsLook? Sample, bool Met)> PollAsync(
        CyclePlan p, int timeoutMs, Func<SlotsLook, bool> done, CancellationToken ct)
    {
        SlotsLook? last = null;
        for (var waited = 0; ; waited += PollMs)
        {
            ct.ThrowIfCancellationRequested();
            var now = SampleSlots(p);
            if (now is not null)
            {
                last = now;
                if (done(now)) return (now, true);
            }
            if (waited >= timeoutMs) return (last, false);
            await _input.DelayAsync(PollMs, 0, ct);
        }
    }

    /// <summary>Waits for two samples in a row to agree — the panel has finished drawing — and returns the last.</summary>
    private async Task<SlotsLook?> SettleAsync(CyclePlan p, int lag, CancellationToken ct)
    {
        SlotsLook? previous = null;
        var (last, _) = await PollAsync(p, SettleTimeoutMs + lag, now =>
        {
            var stable = previous is not null && Majority(now.Means, previous.Means, InventoryOpenedTolerance, differ: false);
            previous = now;
            return stable;
        }, ct);
        return last;
    }

    /// <summary>True when more than half the points differ (or, with <paramref name="differ"/> off, agree).</summary>
    private static bool Majority(double[] now, double[] reference, double tolerance, bool differ)
    {
        var count = 0;
        for (var i = 0; i < now.Length; i++)
            if (Math.Abs(now[i] - reference[i]) > tolerance == differ) count++;
        return count * 2 > now.Length;
    }

    // ─── Input, only while ARK is in front ─────────────────────────────────────

    private void EnsureForeground()
    {
        if (!_foreground.IsGameForeground()) throw new FocusLostException();
    }

    private Task PressAsync(int vk, CancellationToken ct)
    {
        EnsureForeground();
        return _input.KeyPressAsync(vk, 40, 0, ct);
    }

    private Task ClickAsync(Point at, CancellationToken ct)
    {
        EnsureForeground();
        return _input.ClickAsync(MouseButton.Left, at, 30, 0, ct);
    }

    private Task MoveAsync(Point to, CancellationToken ct)
    {
        EnsureForeground();
        _input.MoveTo(to.X, to.Y);
        return Task.CompletedTask;
    }

    private Task DelayAsync(int ms, CancellationToken ct) => _input.DelayAsync(ms, 0, ct);

    // ─── Plan and screen ───────────────────────────────────────────────────────

    /// <summary>Everything a run needs, worked out once at the start.</summary>
    private sealed record CyclePlan(
        FedSuitSettings Settings,
        int OpenVk,
        int ExitVk,
        int TransferVk,
        Point Tab,
        Point[] Slots,
        int[] Selected,
        int SampleHalf,
        double Scale,
        Rectangle SlotsBounds);

    /// <summary>
    /// One look at the five slots: the mean brightness at each centre, for the open and close
    /// checks, and whether each holds a piece.
    /// </summary>
    private sealed record SlotsLook(double[] Means, bool[] Filled);

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

        var selected = FedSuitSettings.SlotsFor(s.Pieces);
        if (selected.Length == 0)
        {
            try { _notifications.ShowWarning(_localizer.T("scripts.fed.toast.nopieces")); }
            catch { /* notifications are best-effort */ }
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
        var slots = ArkInventoryLayout.ArmorSlots(client, uiScaling);
        var scale = ArkInventoryLayout.Scale(client, uiScaling);
        var half = Math.Max(2, (int)Math.Round(SlotSampleHalf * scale));

        // One capture holds every slot's whole interior, and with it the centre boxes.
        var bounds = slots.Select(c => ArmorSlotLook.Interior(c, scale)).Aggregate(Rectangle.Union);

        return new CyclePlan(
            s, openVk, exitVk, transferVk,
            ArkInventoryLayout.PlayerTab(client, uiScaling),
            slots, selected, half, scale, bounds);
    }

    /// <summary>
    /// All five slots out of a single capture — the waits poll this every few frames. All five,
    /// not only the picked ones: the open and close checks go by the majority of them.
    /// </summary>
    private SlotsLook? SampleSlots(CyclePlan p)
    {
        try
        {
            var capture = _sampler.CaptureRegion(p.SlotsBounds);
            if (capture.IsEmpty) return null;

            var means = new double[p.Slots.Length];
            var filled = new bool[p.Slots.Length];
            for (var i = 0; i < p.Slots.Length; i++)
            {
                means[i] = MeanAt(capture, p.SlotsBounds, p.Slots[i], p.SampleHalf);
                filled[i] = ArmorSlotLook.IsFilled(capture, p.SlotsBounds.Location, p.Slots[i], p.Scale);
            }
            return new SlotsLook(means, filled);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fed-Suit slot sample failed — the step is not judged");
            return null;
        }
    }

    private static double MeanAt(ScreenCapture capture, Rectangle origin, Point centre, int half)
    {
        long sum = 0;
        var channels = 0;
        for (var y = centre.Y - half; y <= centre.Y + half; y++)
        {
            var row = y - origin.Y;
            if (row < 0 || row >= capture.Height) continue;

            for (var x = centre.X - half; x <= centre.X + half; x++)
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
        // The keys outside the try: they have their own store, and a failing read of the others
        // must not cost the player the open and transfer keys they typed.
        var defaults = WithRescannedKeys(new FedSuitSettings());
        try
        {
            return Normalize(new FedSuitSettings
            {
                OpenKey = defaults.OpenKey,
                ExitKey = Preferences.Get("fedsuit.exitkey", defaults.ExitKey),
                TransferKey = defaults.TransferKey,
                PressDelayMs = Preferences.Get("fedsuit.pressdelay", defaults.PressDelayMs),
                WaitAfterOpenMs = Preferences.Get("fedsuit.openwait", defaults.WaitAfterOpenMs),
                RepeatDelayMs = Preferences.Get("fedsuit.repeatdelay", defaults.RepeatDelayMs),
                LagBufferMs = Preferences.Get("fedsuit.lagbuffer", defaults.LagBufferMs),
                Pieces = (FedSuitPieces)Preferences.Get("fedsuit.pieces", (int)defaults.Pieces),
                Runs = Preferences.Get("fedsuit.runs", defaults.Runs)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fed-Suit settings load failed — using defaults");
            return defaults;
        }
    }

    /// <summary>
    /// Persists <paramref name="s"/>. The open and transfer keys only when this save changed them:
    /// every edit on the page comes through here with the whole settings block, and writing the
    /// keys along with a new Runs count is how F and T got pinned and stopped following ARK.
    /// </summary>
    private void SaveSettings(FedSuitSettings s, FedSuitSettings before)
    {
        try
        {
            if (!ArkKeyDefaults.SameKey(s.OpenKey, before.OpenKey))
                ArkKeyDefaults.Keep(OpenKeyPref, ArkActions.AccessInventory, "F", s.OpenKey);
            if (!ArkKeyDefaults.SameKey(s.TransferKey, before.TransferKey))
                ArkKeyDefaults.Keep(TransferKeyPref, ArkActions.TransferItem, "T", s.TransferKey);
            Preferences.Set("fedsuit.exitkey", s.ExitKey);
            Preferences.Set("fedsuit.pressdelay", s.PressDelayMs);
            Preferences.Set("fedsuit.openwait", s.WaitAfterOpenMs);
            Preferences.Set("fedsuit.repeatdelay", s.RepeatDelayMs);
            Preferences.Set("fedsuit.lagbuffer", s.LagBufferMs);
            Preferences.Set("fedsuit.pieces", (int)s.Pieces);
            Preferences.Set("fedsuit.runs", s.Runs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fed-Suit settings save failed");
        }
    }

    private const string OpenKeyPref = "fedsuit.openkey";
    private const string TransferKeyPref = "fedsuit.transferkey";

    /// <summary>
    /// The two keys that name an ARK action, asked of the scan again unless the player typed their
    /// own (<see cref="ArkKeyDefaults.Follow"/>). Only those two: reloading the whole settings block
    /// instead would revert a change whose save had failed. A scanned key the macro cannot press
    /// falls to ARK's stock key, never to the one held before, which may be a binding since undone.
    /// </summary>
    private static FedSuitSettings WithRescannedKeys(FedSuitSettings current)
    {
        var next = current.Clone();
        next.OpenKey = NormalizeKey(ArkKeyDefaults.Follow(OpenKeyPref, ArkActions.AccessInventory, "F"), "F");
        next.TransferKey = NormalizeKey(ArkKeyDefaults.Follow(TransferKeyPref, ArkActions.TransferItem, "T"), "T");
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
            WaitAfterOpenMs = Math.Clamp(s.WaitAfterOpenMs, 200, 10_000),
            RepeatDelayMs = Math.Clamp(s.RepeatDelayMs, 0, 60_000),
            LagBufferMs = Math.Clamp(s.LagBufferMs, 0, 1_000),
            Pieces = s.Pieces & FedSuitPieces.All,
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
