using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RazorReaper.Services;

namespace RazorReaper.Services.Automation;

public enum AutoClickerClickType { Single, Double }
public enum AutoClickerRepeatMode { Infinite, Count }
public enum AutoClickerPositionMode { Current, Fixed, Multi }
public enum AutoClickerBurstMode { Continuous, Burst }

/// <summary>A point the clicker can target, in physical screen pixels.</summary>
public readonly record struct AutoClickerPoint(int X, int Y);

/// <summary>
/// Everything the click loop needs, as one immutable snapshot, so a live session can never read
/// half-updated settings while the user is still editing the page.
/// </summary>
public sealed record AutoClickerConfig
{
    public int Hours { get; init; }
    public int Minutes { get; init; }
    public int Seconds { get; init; }
    public int Milliseconds { get; init; } = 100;
    public int HoldMs { get; init; } = 5;
    public int PreStartDelaySeconds { get; init; }

    public MouseButton Button { get; init; } = MouseButton.Left;
    public AutoClickerClickType ClickType { get; init; } = AutoClickerClickType.Single;
    public AutoClickerRepeatMode RepeatMode { get; init; } = AutoClickerRepeatMode.Infinite;
    public int RepeatCount { get; init; } = 10;

    public AutoClickerPositionMode PositionMode { get; init; } = AutoClickerPositionMode.Current;
    public int FixedX { get; init; }
    public int FixedY { get; init; }
    public IReadOnlyList<AutoClickerPoint> MultiPositions { get; init; } = [];

    public AutoClickerBurstMode Mode { get; init; } = AutoClickerBurstMode.Continuous;
    public int BurstClickCount { get; init; } = 10;
    public int BurstPauseSeconds { get; init; } = 2;

    public bool Randomize { get; init; }
    public int RandomVarianceMs { get; init; } = 100;

    /// <summary>Total interval between clicks, floored at 1 ms.</summary>
    public int TotalMilliseconds =>
        Math.Max(1, (((Hours * 60) + Minutes) * 60 + Seconds) * 1000 + Milliseconds);
}

/// <summary>
/// The Auto Clicker's click loop, owned by the app rather than by the page.
///
/// It used to live inside Autoclicker.razor, so the whole feature — timer, burst state, counters —
/// was torn down by the component's Dispose the moment you navigated to another page. A
/// system-wide hotkey is pointless if the thing it toggles only exists on one screen, so the
/// runtime lives here and the page is now just a view onto it.
/// </summary>
public interface IAutoClickerRuntime
{
    bool IsRunning { get; }
    int ClickCount { get; }
    DateTime? NextClickTime { get; }
    AutoClickerConfig Config { get; }

    /// <summary>Raised whenever the running state or the click counter changes.</summary>
    event Action? StateChanged;

    /// <summary>Replaces the configuration and persists it. Ignored while a session is running.</summary>
    void Configure(AutoClickerConfig config);

    Task StartAsync();
    void Stop();
    Task ToggleAsync();
}

public sealed class AutoClickerRuntime : IAutoClickerRuntime, IDisposable
{
    private readonly IInputSimulator _input;
    private readonly IActivityService _activity;
    private readonly ILogger<AutoClickerRuntime> _logger;

    private readonly SemaphoreSlim _clickGate = new(1, 1);
    private readonly object _sessionLock = new();
    private readonly Random _random = new();

    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _timer;
    private ElapsedShim? _handler;
    private bool _timerResolutionRaised;
    private int _burstCount;
    private int _positionIndex;
    private volatile bool _running;
    private int _clickCount;
    private bool _disposed;

    public event Action? StateChanged;

    public bool IsRunning => _running;
    public int ClickCount => _clickCount;
    public DateTime? NextClickTime { get; private set; }
    public AutoClickerConfig Config { get; private set; }

    public AutoClickerRuntime(
        IInputSimulator input,
        IActivityService activity,
        ILogger<AutoClickerRuntime> logger)
    {
        _input = input;
        _activity = activity;
        _logger = logger;
        Config = AutoClickerConfigStore.Load();
    }

    public void Configure(AutoClickerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_running) return;   // never swap settings under a live session

        Config = config;
        AutoClickerConfigStore.Save(config);
    }

    public async Task ToggleAsync()
    {
        if (_running) Stop();
        else await StartAsync();
    }

    public async Task StartAsync()
    {
        if (_disposed) return;

        CancellationToken token;
        lock (_sessionLock)
        {
            if (_running) return;
            _running = true;
            _clickCount = 0;
            _burstCount = 0;
            _positionIndex = 0;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        // 1 ms timer resolution only while clicking, so the whole OS is not held hot when idle.
        if (!_timerResolutionRaised)
        {
            try { TimeBeginPeriod(1); _timerResolutionRaised = true; }
            catch { /* older Windows: ignore */ }
        }

        RaiseChanged();

        var config = Config;

        if (config.PreStartDelaySeconds > 0)
        {
            try { await Task.Delay(config.PreStartDelaySeconds * 1000, token); }
            catch (OperationCanceledException) { return; }
            if (!_running) return;
        }

        _activity.AddActivity($"Autoclicker started ({FormatInterval(config)} interval)", "success");

        if (config.Randomize)
        {
            _ = Task.Run(() => RandomizedLoopAsync(config, token), CancellationToken.None);
        }
        else
        {
            var timer = new System.Timers.Timer(config.TotalMilliseconds) { AutoReset = true };
            _handler = new ElapsedShim(async () =>
            {
                // Drop an overlapping tick rather than queueing it — a backlog at high CPS is what
                // turns a clicker into a thread storm.
                if (!await _clickGate.WaitAsync(0)) return;
                try { await PerformClickAsync(config, token); }
                finally { _clickGate.Release(); }
            });
            timer.Elapsed += _handler.OnElapsed;
            _timer = timer;
            timer.Start();

            NextClickTime = DateTime.Now.AddMilliseconds(config.TotalMilliseconds);
            await PerformClickAsync(config, token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sessionLock)
        {
            if (!_running) return;
            _running = false;
            cts = _cts;
            _cts = null;
        }

        // Cancel first: that interrupts an in-flight hold or interval delay, which is what makes
        // stopping feel instant rather than waiting out the current tick.
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }

        var timer = _timer;
        _timer = null;
        if (timer is not null)
        {
            timer.Stop();
            if (_handler is not null)
            {
                timer.Elapsed -= _handler.OnElapsed;
                _handler = null;
            }
            timer.Dispose();
        }

        if (_timerResolutionRaised)
        {
            try { TimeEndPeriod(1); } catch { }
            _timerResolutionRaised = false;
        }

        NextClickTime = null;
        _activity.AddActivity($"Autoclicker stopped ({_clickCount} clicks performed)", "info");
        RaiseChanged();
    }

    private async Task RandomizedLoopAsync(AutoClickerConfig config, CancellationToken token)
    {
        var sw = new Stopwatch();
        while (_running && !token.IsCancellationRequested)
        {
            if (config.RepeatMode == AutoClickerRepeatMode.Count && _clickCount >= config.RepeatCount)
            {
                Stop();
                break;
            }

            sw.Restart();
            await PerformClickAsync(config, token);
            if (!_running) break;

            var variance = _random.Next(-config.RandomVarianceMs, config.RandomVarianceMs + 1);
            var interval = Math.Max(1, config.TotalMilliseconds + variance);
            var remaining = (int)Math.Max(0, interval - sw.ElapsedMilliseconds);

            NextClickTime = DateTime.Now.AddMilliseconds(remaining);
            try { await Task.Delay(remaining, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PerformClickAsync(AutoClickerConfig config, CancellationToken token)
    {
        if (!_running || token.IsCancellationRequested) return;

        // Never click while RazorReaper's own window is focused, otherwise the first synthetic
        // click in Cursor mode lands on the Start button and instantly toggles Stop.
        if (IsOwnWindowForeground()) return;

        if (config.Mode == AutoClickerBurstMode.Burst && _burstCount >= config.BurstClickCount)
        {
            _burstCount = 0;
            try { await Task.Delay(config.BurstPauseSeconds * 1000, token); }
            catch (OperationCanceledException) { return; }
            if (!_running) return;
        }

        if (config.RepeatMode == AutoClickerRepeatMode.Count && _clickCount >= config.RepeatCount)
        {
            Stop();
            return;
        }

        try
        {
            // The cursor deliberately stays where it clicked — restoring it after every click made
            // the pointer unusable at high CPS.
            if (config.PositionMode == AutoClickerPositionMode.Fixed)
            {
                _input.MoveTo(config.FixedX, config.FixedY);
            }
            else if (config.PositionMode == AutoClickerPositionMode.Multi && config.MultiPositions.Count > 0)
            {
                var idx = config.MultiPositions.Count == 1 ? 0 : _positionIndex;
                var p = config.MultiPositions[idx];
                _input.MoveTo(p.X, p.Y);
                if (config.MultiPositions.Count > 1)
                {
                    _positionIndex = (_positionIndex + 1) % config.MultiPositions.Count;
                }
            }

            if (!_running) return;

            var hold = Math.Max(1, config.HoldMs);

            _input.MouseDown(config.Button);
            try { await Task.Delay(hold, token); }
            catch (OperationCanceledException) { _input.MouseUp(config.Button); return; }
            _input.MouseUp(config.Button);

            if (config.ClickType == AutoClickerClickType.Double && _running)
            {
                try { await Task.Delay(hold, token); } catch (OperationCanceledException) { return; }
                if (!_running) return;
                _input.MouseDown(config.Button);
                try { await Task.Delay(hold, token); }
                catch (OperationCanceledException) { _input.MouseUp(config.Button); return; }
                _input.MouseUp(config.Button);
            }

            Interlocked.Increment(ref _clickCount);
            _burstCount++;

            if (!config.Randomize && _running)
            {
                NextClickTime = DateTime.Now.AddMilliseconds(config.TotalMilliseconds);
            }

            if (_running && config.RepeatMode == AutoClickerRepeatMode.Count && _clickCount >= config.RepeatCount)
            {
                Stop();
                return;
            }

            RaiseChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autoclicker click loop error");
        }
    }

    private static string FormatInterval(AutoClickerConfig c)
    {
        var total = c.TotalMilliseconds;
        return total >= 1000
            ? $"{(total / 1000.0).ToString("0.##", CultureInfo.InvariantCulture)}s"
            : $"{total}ms";
    }

    private void RaiseChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Autoclicker StateChanged subscriber threw"); }
    }

    private static bool IsOwnWindowForeground()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _clickGate.Dispose(); } catch { }
        StateChanged = null;
    }

    /// <summary>Adapts the async click body onto the timer's synchronous Elapsed signature.</summary>
    private sealed class ElapsedShim(Func<Task> body)
    {
        public void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e) => _ = body();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint uMilliseconds);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint uMilliseconds);
}
