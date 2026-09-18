using System.Collections.Concurrent;
using RazorReaper.Models;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Infrastructure;

/// <summary>
/// One thing the automation layer asked the input layer to do. The point of recording these as a
/// sequence rather than as counters is that the ordering and the payload are the bugs: a command
/// typed as virtual keys and a command typed as text both "sent input", and only one of them
/// reaches the game intact.
/// </summary>
public abstract record SimulatedInput
{
    public sealed record KeyDown(int VirtualKey) : SimulatedInput;

    public sealed record KeyUp(int VirtualKey) : SimulatedInput;

    public sealed record KeyPress(int VirtualKey, int HoldMs) : SimulatedInput;

    /// <summary>Text typed through the Unicode path — the characters themselves, not key codes.</summary>
    public sealed record Text(string Value) : SimulatedInput;

    public sealed record MoveTo(int X, int Y) : SimulatedInput;

    public sealed record MoveBy(int Dx, int Dy) : SimulatedInput;

    public sealed record MouseDown(MouseButton Button) : SimulatedInput;

    public sealed record MouseUp(MouseButton Button) : SimulatedInput;

    public sealed record Click(MouseButton Button, Point? At, int HoldMs) : SimulatedInput;

    public sealed record Scroll(int Detents) : SimulatedInput;

    public sealed record Delay(int Ms) : SimulatedInput;
}

/// <summary>
/// An <see cref="IInputSimulator"/> that writes down what it was asked to do instead of moving the
/// real mouse and keyboard. Delays are recorded and returned instantly, so a test that drives a
/// script loop does not have to wait out the script's own pacing.
/// </summary>
public sealed class RecordingInputSimulator : IInputSimulator
{
    private readonly object _sync = new();
    private readonly List<SimulatedInput> _events = new();

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<SimulatedInput> Events
    {
        get { lock (_sync) return _events.ToArray(); }
    }

    /// <summary>Every virtual key currently down and not yet released.</summary>
    public IReadOnlyCollection<int> HeldKeys
    {
        get
        {
            lock (_sync)
            {
                var held = new HashSet<int>();
                foreach (var e in _events)
                {
                    switch (e)
                    {
                        case SimulatedInput.KeyDown d: held.Add(d.VirtualKey); break;
                        case SimulatedInput.KeyUp u: held.Remove(u.VirtualKey); break;
                    }
                }
                return held;
            }
        }
    }

    /// <summary>Every mouse button currently down and not yet released.</summary>
    public IReadOnlyCollection<MouseButton> HeldButtons
    {
        get
        {
            lock (_sync)
            {
                var held = new HashSet<MouseButton>();
                foreach (var e in _events)
                {
                    switch (e)
                    {
                        case SimulatedInput.MouseDown d: held.Add(d.Button); break;
                        case SimulatedInput.MouseUp u: held.Remove(u.Button); break;
                    }
                }
                return held;
            }
        }
    }

    /// <summary>All text typed through the Unicode path, concatenated in order.</summary>
    public string TypedText => string.Concat(Events.OfType<SimulatedInput.Text>().Select(t => t.Value));

    public void Clear()
    {
        lock (_sync) _events.Clear();
    }

    private void Record(SimulatedInput e)
    {
        lock (_sync) _events.Add(e);
    }

    public void KeyDown(int virtualKey) => Record(new SimulatedInput.KeyDown(virtualKey));

    public void KeyUp(int virtualKey) => Record(new SimulatedInput.KeyUp(virtualKey));

    public Task KeyPressAsync(int virtualKey, int holdMs = 40, double jitter = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record(new SimulatedInput.KeyPress(virtualKey, holdMs));
        return Task.CompletedTask;
    }

    public Task TypeTextAsync(string text, int perCharDelayMs = 20, double jitter = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record(new SimulatedInput.Text(text));
        return Task.CompletedTask;
    }

    public Point GetCursorPosition() => Point.Empty;

    public void MoveTo(int x, int y) => Record(new SimulatedInput.MoveTo(x, y));

    public void MoveBy(int dx, int dy) => Record(new SimulatedInput.MoveBy(dx, dy));

    public void MouseDown(MouseButton button) => Record(new SimulatedInput.MouseDown(button));

    public void MouseUp(MouseButton button) => Record(new SimulatedInput.MouseUp(button));

    public Task ClickAsync(MouseButton button, Point? at = null, int holdMs = 30, double jitter = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record(new SimulatedInput.Click(button, at, holdMs));
        return Task.CompletedTask;
    }

    public void Scroll(int detents) => Record(new SimulatedInput.Scroll(detents));

    public Task DelayAsync(int delayMs, double jitter = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record(new SimulatedInput.Delay(delayMs));
        return Task.CompletedTask;
    }

    public int ApplyJitter(int delayMs, double jitter) => delayMs;
}

/// <summary>A foreground gate a test can open and shut by hand.</summary>
public sealed class FakeForegroundGate : IForegroundGate
{
    public FakeForegroundGate(bool gameIsForeground = true) => GameIsForeground = gameIsForeground;

    public volatile bool GameIsForeground;

    /// <summary>How many times the gate was consulted — a loop that never ticks still asks.</summary>
    private int _asked;

    public int TimesAsked => Volatile.Read(ref _asked);

    public bool IsGameForeground()
    {
        Interlocked.Increment(ref _asked);
        return GameIsForeground;
    }
}

/// <summary>
/// A hotkey service that registers nothing. Scripts claim system-wide hotkeys in their
/// constructor, which a test host must not actually do.
/// </summary>
public sealed class NullAutomationHotkeyService : IAutomationHotkeyService
{
    public int RegisterHotkey(int virtualKey, bool ctrl, bool alt, bool shift, Action callback) => 0;

    public void UnregisterHotkey(int registrationId) { }

    public bool IsRegistered(int registrationId) => false;

    public void UnregisterAll() { }

    public void Dispose() { }
}

/// <summary>Collects the toasts a service or script raised, so a test can read them back.</summary>
public sealed class RecordingNotificationService : INotificationService
{
    public sealed record Toast(string Level, string Message);

    private readonly ConcurrentQueue<Toast> _toasts = new();

    public IReadOnlyList<Toast> Toasts => _toasts.ToArray();

    // Nothing in the tests subscribes, and an auto-implemented event nobody raises is a warning.
    public event Action<NotificationMessage>? OnNotificationAdded { add { } remove { } }

    public event Action<string>? OnNotificationRemoved { add { } remove { } }

    public void ShowSuccess(string message, int durationMs = 3500) => _toasts.Enqueue(new Toast("success", message));

    public void ShowError(string message, int durationMs = 5000) => _toasts.Enqueue(new Toast("error", message));

    public void ShowWarning(string message, int durationMs = 4500) => _toasts.Enqueue(new Toast("warning", message));

    public void ShowWarningWithCountdown(string message, int durationMs) => _toasts.Enqueue(new Toast("warning", message));

    public void ShowInfo(string message, int durationMs = 3500) => _toasts.Enqueue(new Toast("info", message));

    public void RemoveNotification(string id) { }
}

/// <summary>Collects activity rows without touching storage.</summary>
public sealed class RecordingActivityService : IActivityService
{
    private readonly ConcurrentQueue<ActivityItem> _items = new();

    public event EventHandler<ActivityItem>? ActivityAdded;

    public void AddActivity(string title, string type = "info", string? key = null)
    {
        var item = new ActivityItem { Title = title, Type = type, Key = key, Timestamp = DateTime.UtcNow };
        _items.Enqueue(item);
        ActivityAdded?.Invoke(this, item);
    }

    public IReadOnlyList<ActivityItem> GetRecentActivities() => _items.ToArray();

    public void ClearActivities()
    {
        while (_items.TryDequeue(out _)) { }
    }
}

/// <summary>
/// A calibration service backed by a dictionary. Enough for the vision scripts' "is this
/// calibrated?" questions without a screen, a countdown, or a file on disk.
/// </summary>
public sealed class FakeCalibrationService : ICalibrationService
{
    private readonly Dictionary<string, Rectangle> _regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _points = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentResolutionKey { get; set; } = "1920x1080";

    /// <summary>
    /// What the fake says the game's display is. Null is "ARK is not running, so there is no
    /// telling" — the real service answers that too rather than guessing at the primary.
    /// </summary>
    public AttachedDisplay? CurrentGameMonitor { get; set; } =
        new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    /// <summary>Monitor stamps a test has set on a region, keyed the same way the regions are.</summary>
    private readonly Dictionary<string, (string Device, string Resolution)> _stamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times <see cref="StampRegionMonitor"/> was called, per region.</summary>
    public Dictionary<string, int> StampCalls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCapturing => false;

    public void SetRegion(string name, Rectangle region) => _regions[name] = region;

    /// <summary>Pretends the region was calibrated on a given display, the way a stored entry would.</summary>
    public void SetRegionMonitor(string name, string deviceName, string resolution)
        => _stamps[name] = (deviceName, resolution);

    public CalibrationRegion? GetRegion(string name)
    {
        if (!_regions.TryGetValue(name, out var region)) return null;
        _stamps.TryGetValue(name, out var stamp);
        return new CalibrationRegion(
            name, region.Left, region.Top, region.Right, region.Bottom, CurrentResolutionKey,
            stamp.Device, stamp.Resolution);
    }

    public void StampRegionMonitor(string name)
    {
        StampCalls[name] = StampCalls.TryGetValue(name, out var count) ? count + 1 : 1;
        if (CurrentGameMonitor is { } monitor && _regions.ContainsKey(name))
            _stamps[name] = (monitor.DeviceName, monitor.ResolutionKey);
    }

    public void SetPoint(string name, Point point) => _points[name] = point;

    public Task<CalibrationPoint?> CapturePointAsync(string name, int countdownSeconds = 3, IProgress<int>? countdown = null, CancellationToken ct = default)
        => Task.FromResult<CalibrationPoint?>(null);

    public Task<CalibrationRegion?> CaptureRegionAsync(string name, int countdownSeconds = 3, IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default)
        => Task.FromResult<CalibrationRegion?>(null);

    public bool HasPoint(string name) => _points.ContainsKey(name);

    public bool TryGetPoint(string name, out Point point) => _points.TryGetValue(name, out point);

    public bool HasRegion(string name) => _regions.ContainsKey(name);

    public bool TryGetRegion(string name, out Rectangle region) => _regions.TryGetValue(name, out region);

    public IReadOnlyList<CalibrationPoint> GetPoints()
        => _points.Select(p => new CalibrationPoint(p.Key, p.Value.X, p.Value.Y, CurrentResolutionKey)).ToArray();

    public IReadOnlyList<CalibrationRegion> GetRegions()
        => _regions.Select(r => new CalibrationRegion(r.Key, r.Value.Left, r.Value.Top, r.Value.Right, r.Value.Bottom, CurrentResolutionKey)).ToArray();

    public bool DeletePoint(string name) => _points.Remove(name);

    public bool DeleteRegion(string name) => _regions.Remove(name);
}

/// <summary>
/// A screen sampler whose answers a test sets directly. <see cref="TargetVisible"/> is the one
/// that matters: it is what every vision script's scan ultimately asks.
/// </summary>
public sealed class FakeScreenSampler : IScreenSampler
{
    /// <summary>What <see cref="MatchesReference"/> reports. Flip it to make a script see its target.</summary>
    public volatile bool TargetVisible;

    /// <summary>Reference snapshots the fake claims to hold.</summary>
    public HashSet<string> References { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the fake scores when <see cref="TargetVisible"/> is set.</summary>
    public double SimilarityPercentValue { get; set; } = 100;

    /// <summary>What it scores when it is not. Kept well under every script's default threshold.</summary>
    public double AbsentSimilarityPercentValue { get; set; } = 10;

    /// <summary>Set to make the sampler claim it cannot compare at all (capture failed, size changed).</summary>
    public bool SimilarityUnavailable { get; set; }

    /// <summary>
    /// Set to make every capture differ from the one before it — a screen that moved between two
    /// presses. Left off, the captures are all zeroes and every comparison reads "nothing moved",
    /// which is what the abort paths are tested against.
    /// </summary>
    public bool CapturesDiffer { get; set; }

    private int _captures;

    public ScreenCapture CaptureRegion(Rectangle region)
    {
        int width = Math.Max(region.Width, 1), height = Math.Max(region.Height, 1);
        var bgra = new byte[width * height * 4];
        if (CapturesDiffer && Interlocked.Increment(ref _captures) % 2 == 0) Array.Fill(bgra, byte.MaxValue);
        return new ScreenCapture(width, height, bgra);
    }

    public void CaptureReference(string key, Rectangle region) => References.Add(key);

    public bool HasReference(string key) => References.Contains(key);

    public bool RefineReferenceMask(string key, Rectangle region, out int kept, byte tolerance = 12)
    {
        kept = 0;
        return false;
    }

    public (int Kept, int Total) ReferenceMaskInfo(string key) => (0, 0);

    /// <summary>
    /// Derived from <see cref="TargetVisible"/> rather than set apart from it: the two are the
    /// same comparison in the real sampler, and a fake that could say "not visible" and "100%
    /// similar" in the same breath would let a script pass a test it fails on a real screen.
    /// </summary>
    public double? SimilarityPercent(string key, Rectangle region)
        => SimilarityUnavailable ? null : TargetVisible ? SimilarityPercentValue : AbsentSimilarityPercentValue;

    public void ClearReference(string key) => References.Remove(key);

    public bool MatchesReference(string key, Rectangle region, double tolerance) => TargetVisible;

    public Point? FindTemplate(Rectangle searchRegion, TemplateImage template, double threshold, out double score)
    {
        score = 0;
        return null;
    }

    public bool ContainsTemplate(Rectangle searchRegion, TemplateImage template, double threshold) => false;

    public Color DominantColor(Rectangle region) => Color.Black;

    public Color AverageColor(Rectangle region) => Color.Black;
}

/// <summary>
/// A macro runner that reports a sequence as running without pressing anything, and finishes when
/// it is stopped. Enough for the lifecycle questions — did this start, did it start twice — with
/// no input reaching the machine running the tests.
/// </summary>
public sealed class FakeMacroRunner : IMacroRunner
{
    private volatile TaskCompletionSource? _run;

    public FakeMacroRunner(string name = "fake") => Name = name;

    public string Name { get; }

    public MacroRunnerState State { get; private set; } = MacroRunnerState.Idle;

    public string? CurrentSequenceName { get; private set; }

    public int CurrentLoop { get; private set; }

    public int CurrentStepIndex { get; private set; } = -1;

    public int TotalSteps { get; private set; }

    /// <summary>How many sequences this runner was asked to run.</summary>
    public int RunCount { get; private set; }

    public event Action<MacroRunnerState>? StateChanged;

    public event Action<int, int>? StepStarted;

    public async Task<bool> RunAsync(MacroSequence sequence, CancellationToken ct = default)
    {
        RunCount++;
        CurrentSequenceName = sequence.Name;
        TotalSteps = sequence.Steps.Count;
        SetState(MacroRunnerState.Running);

        var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _run = run;
        StepStarted?.Invoke(0, 1);

        using (ct.Register(() => run.TrySetResult()))
        {
            await run.Task;
        }

        CurrentSequenceName = null;
        CurrentStepIndex = -1;
        CurrentLoop = 0;
        SetState(MacroRunnerState.Idle);
        return true;
    }

    public void Stop()
    {
        SetState(MacroRunnerState.Stopping);
        _run?.TrySetResult();
    }

    private void SetState(MacroRunnerState next)
    {
        State = next;
        StateChanged?.Invoke(next);
    }
}

/// <summary>A macro engine that hands out <see cref="FakeMacroRunner"/>s.</summary>
public sealed class FakeMacroEngine : IMacroEngine
{
    private readonly Dictionary<string, FakeMacroRunner> _runners = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IMacroRunner> Runners => _runners.Values.ToArray();

    public IMacroRunner GetRunner(string name)
    {
        if (!_runners.TryGetValue(name, out var runner))
        {
            runner = new FakeMacroRunner(name);
            _runners[name] = runner;
        }
        return runner;
    }

    public FakeMacroRunner Runner(string name) => (FakeMacroRunner)GetRunner(name);

    public void StopAll()
    {
        foreach (var r in _runners.Values) r.Stop();
    }
}

/// <summary>A usage gate that counts what was charged against which feature.</summary>
public sealed class CountingUsageGateService : IUsageGateService
{
    private readonly ConcurrentQueue<string> _consumed = new();

    /// <summary>Features charged, in order — one entry per <see cref="TryConsumeAsync"/> call.</summary>
    public IReadOnlyList<string> Consumed => _consumed.ToArray();

    /// <summary>What every consume attempt returns. Unlimited by default.</summary>
    public UsageGateResult Result { get; set; } = UsageGateResult.UnlimitedResult;

    public int CountOf(string feature) => Consumed.Count(f => string.Equals(f, feature, StringComparison.Ordinal));

    public event Action? OnUsageChanged;

    public Task<UsageGateResult> TryConsumeAsync(string feature)
    {
        _consumed.Enqueue(feature);
        OnUsageChanged?.Invoke();
        return Task.FromResult(Result);
    }

    public Task<IReadOnlyDictionary<string, FeatureUsage>?> GetStatusAsync()
        => Task.FromResult<IReadOnlyDictionary<string, FeatureUsage>?>(null);
}
