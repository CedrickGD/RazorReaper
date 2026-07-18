using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;

namespace RazorReaper.Services.Automation;

/// <summary>Kind of action a macro step performs.</summary>
public enum MacroStepType
{
    /// <summary>Press and hold a virtual key.</summary>
    KeyDown,
    /// <summary>Release a virtual key.</summary>
    KeyUp,
    /// <summary>Press and release a virtual key.</summary>
    KeyPress,
    /// <summary>Move the cursor to (X, Y) and click <see cref="MacroStep.Button"/>.</summary>
    ClickAt,
    /// <summary>Move the cursor to (X, Y) without clicking.</summary>
    MoveTo,
    /// <summary>Wait <see cref="MacroStep.DelayMs"/> milliseconds (jitter applies).</summary>
    Delay,
    /// <summary>Bring the ARK window to the foreground (restore + focus only — never touches other apps).</summary>
    FocusGameWindow
}

/// <summary>One step of a macro sequence. Use the static factories for readable construction.</summary>
public sealed record MacroStep
{
    /// <summary>The action this step performs.</summary>
    public MacroStepType Type { get; init; }
    /// <summary>Virtual-key code for key steps.</summary>
    public int VirtualKey { get; init; }
    /// <summary>Absolute screen X for mouse steps (physical pixels).</summary>
    public int X { get; init; }
    /// <summary>Absolute screen Y for mouse steps (physical pixels).</summary>
    public int Y { get; init; }
    /// <summary>Delay duration for <see cref="MacroStepType.Delay"/> steps.</summary>
    public int DelayMs { get; init; }
    /// <summary>Mouse button for <see cref="MacroStepType.ClickAt"/> steps.</summary>
    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>Creates a key-down step.</summary>
    public static MacroStep KeyDown(int virtualKey) => new() { Type = MacroStepType.KeyDown, VirtualKey = virtualKey };
    /// <summary>Creates a key-up step.</summary>
    public static MacroStep KeyUp(int virtualKey) => new() { Type = MacroStepType.KeyUp, VirtualKey = virtualKey };
    /// <summary>Creates a key press (down + up) step.</summary>
    public static MacroStep KeyPress(int virtualKey) => new() { Type = MacroStepType.KeyPress, VirtualKey = virtualKey };
    /// <summary>Creates a click step at an absolute screen point.</summary>
    public static MacroStep ClickAt(int x, int y, MouseButton button = MouseButton.Left)
        => new() { Type = MacroStepType.ClickAt, X = x, Y = y, Button = button };
    /// <summary>Creates a cursor move step.</summary>
    public static MacroStep MoveTo(int x, int y) => new() { Type = MacroStepType.MoveTo, X = x, Y = y };
    /// <summary>Creates a delay step.</summary>
    public static MacroStep Delay(int delayMs) => new() { Type = MacroStepType.Delay, DelayMs = delayMs };
    /// <summary>Creates a focus-game-window step.</summary>
    public static MacroStep FocusGameWindow() => new() { Type = MacroStepType.FocusGameWindow };
}

/// <summary>A named, loopable list of macro steps with shared timing options.</summary>
public sealed class MacroSequence
{
    /// <summary>Display name used in progress events and activity entries.</summary>
    public string Name { get; set; } = "Macro";
    /// <summary>Ordered steps executed each loop.</summary>
    public List<MacroStep> Steps { get; set; } = new();
    /// <summary>Number of loops to run; 0 or negative means run until stopped.</summary>
    public int RepeatCount { get; set; } = 1;
    /// <summary>Extra delay between loops, in milliseconds.</summary>
    public int LoopDelayMs { get; set; }
    /// <summary>Base delay inserted between consecutive steps, in milliseconds.</summary>
    public int InterStepDelayMs { get; set; }
    /// <summary>0..1 fractional randomization applied to every delay in this sequence.</summary>
    public double DelayJitter { get; set; }
}

/// <summary>Lifecycle state of a macro runner.</summary>
public enum MacroRunnerState
{
    /// <summary>No sequence is running.</summary>
    Idle,
    /// <summary>A sequence is executing.</summary>
    Running,
    /// <summary>Stop was requested; the runner is winding down.</summary>
    Stopping
}

/// <summary>
/// Executes one <see cref="MacroSequence"/> at a time. Runners are singletons that keep running
/// when the user navigates away from a page — callers must wire a global stop hotkey via
/// <see cref="IAutomationHotkeyService"/> that calls <see cref="Stop"/> (or
/// <see cref="IMacroEngine.StopAll"/>).
/// </summary>
public interface IMacroRunner
{
    /// <summary>Stable runner name (the key it was created under).</summary>
    string Name { get; }
    /// <summary>Current lifecycle state.</summary>
    MacroRunnerState State { get; }
    /// <summary>Name of the sequence currently running, or null when idle.</summary>
    string? CurrentSequenceName { get; }
    /// <summary>1-based loop currently executing (0 when idle).</summary>
    int CurrentLoop { get; }
    /// <summary>0-based index of the step currently executing (-1 when idle).</summary>
    int CurrentStepIndex { get; }
    /// <summary>Step count of the sequence currently running (0 when idle).</summary>
    int TotalSteps { get; }

    /// <summary>Raised whenever <see cref="State"/> changes. May fire on a background thread.</summary>
    event Action<MacroRunnerState>? StateChanged;
    /// <summary>Raised when a step starts: (stepIndex, loopNumber). May fire on a background thread.</summary>
    event Action<int, int>? StepStarted;

    /// <summary>
    /// Runs a sequence to completion. Returns true when all loops completed, false when the run
    /// was stopped, aborted (game window unavailable), or another sequence was already running.
    /// </summary>
    Task<bool> RunAsync(MacroSequence sequence, CancellationToken ct = default);

    /// <summary>Requests a hard stop of the current run. Safe to call from any thread, including hotkey callbacks.</summary>
    void Stop();
}

/// <summary>
/// Factory/registry for named macro runners so independent features (e.g. an autoclicker and a
/// crafting macro) can run concurrently without stepping on each other's state.
/// </summary>
public interface IMacroEngine
{
    /// <summary>Gets (or creates) the runner with the given name.</summary>
    IMacroRunner GetRunner(string name);

    /// <summary>All runners created so far.</summary>
    IReadOnlyCollection<IMacroRunner> Runners { get; }

    /// <summary>Global panic stop — requests a stop on every runner.</summary>
    void StopAll();
}

/// <summary>Default <see cref="IMacroEngine"/> implementation.</summary>
public sealed class MacroEngine : IMacroEngine
{
    private readonly IInputSimulator _sim;
    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly IActivityService _activity;
    private readonly ILogger<MacroEngine> _logger;
    private readonly ConcurrentDictionary<string, MacroRunner> _runners = new(StringComparer.OrdinalIgnoreCase);

    public MacroEngine(
        IInputSimulator sim,
        IProcessService process,
        IOptions<AppConfiguration> config,
        IActivityService activity,
        ILogger<MacroEngine> logger)
    {
        _sim = sim;
        _process = process;
        _config = config;
        _activity = activity;
        _logger = logger;
    }

    public IMacroRunner GetRunner(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "default";
        return _runners.GetOrAdd(name, n => new MacroRunner(n, this));
    }

    public IReadOnlyCollection<IMacroRunner> Runners => _runners.Values.ToArray();

    public void StopAll()
    {
        foreach (var runner in _runners.Values)
            runner.Stop();
        try { _activity.AddActivity("All macro runners stopped", "warning"); }
        catch { /* activity is best-effort */ }
    }

    internal IInputSimulator Simulator => _sim;
    internal IActivityService Activity => _activity;
    internal ILogger Logger => _logger;

    /// <summary>
    /// Focus helper mirroring GameConsoleService: restore + SetForegroundWindow, with an
    /// AttachThreadInput assist when Windows refuses the initial foreground switch. Never
    /// minimizes, suspends, or closes any other application.
    /// </summary>
    internal async Task<bool> FocusGameWindowAsync(CancellationToken ct)
    {
        var processes = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
        try
        {
            if (processes.Length == 0) return false;
            var hwnd = processes[0].MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("ARK is running but has no MainWindowHandle — cannot focus for macro.");
                return false;
            }

            ShowWindow(hwnd, SW_RESTORE);
            await Task.Delay(100, ct);
            SetForegroundWindow(hwnd);
            await Task.Delay(300, ct);
            if (GetForegroundWindow() == hwnd) return true;

            // AttachThreadInput assist: temporarily join input queues with whichever thread owns
            // the current foreground window so SetForegroundWindow is permitted.
            var fg = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fg, out _);
            var cur = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != cur)
            {
                AttachThreadInput(cur, fgThread, true);
                try { SetForegroundWindow(hwnd); }
                finally { AttachThreadInput(cur, fgThread, false); }
                await Task.Delay(150, ct);
            }
            return GetForegroundWindow() == hwnd;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FocusGameWindow failed");
            return false;
        }
        finally
        {
            foreach (var p in processes) p?.Dispose();
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

/// <summary>Single-sequence runner created and owned by <see cref="MacroEngine"/>.</summary>
internal sealed class MacroRunner : IMacroRunner
{
    private readonly MacroEngine _engine;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private volatile MacroRunnerState _state = MacroRunnerState.Idle;

    internal MacroRunner(string name, MacroEngine engine)
    {
        Name = name;
        _engine = engine;
    }

    public string Name { get; }
    public MacroRunnerState State => _state;
    public string? CurrentSequenceName { get; private set; }
    public int CurrentLoop { get; private set; }
    public int CurrentStepIndex { get; private set; } = -1;
    public int TotalSteps { get; private set; }

    public event Action<MacroRunnerState>? StateChanged;
    public event Action<int, int>? StepStarted;

    public async Task<bool> RunAsync(MacroSequence sequence, CancellationToken ct = default)
    {
        if (sequence is null || sequence.Steps is null || sequence.Steps.Count == 0) return false;

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_state != MacroRunnerState.Idle) return false;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _state = MacroRunnerState.Running;
            CurrentSequenceName = sequence.Name;
            TotalSteps = sequence.Steps.Count;
            CurrentLoop = 0;
            CurrentStepIndex = -1;
        }
        RaiseState(MacroRunnerState.Running);
        TryActivity($"Macro '{sequence.Name}' started", "info");

        var sim = _engine.Simulator;
        var token = cts.Token;
        // Keys held down by KeyDown steps without a matching KeyUp yet — released on any exit so
        // a hard stop can never leave a key stuck while ARK has focus.
        var heldKeys = new HashSet<int>();
        var completed = false;

        try
        {
            var loop = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                loop++;
                CurrentLoop = loop;

                for (var i = 0; i < sequence.Steps.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    CurrentStepIndex = i;
                    try { StepStarted?.Invoke(i, loop); }
                    catch { /* subscriber errors must not kill the run */ }

                    await ExecuteStepAsync(sequence.Steps[i], sequence, sim, heldKeys, token);

                    if (sequence.InterStepDelayMs > 0 && i < sequence.Steps.Count - 1)
                        await sim.DelayAsync(sequence.InterStepDelayMs, sequence.DelayJitter, token);
                }

                if (sequence.RepeatCount > 0 && loop >= sequence.RepeatCount) break;
                if (sequence.LoopDelayMs > 0)
                    await sim.DelayAsync(sequence.LoopDelayMs, sequence.DelayJitter, token);
            }
            completed = true;
            TryActivity($"Macro '{sequence.Name}' completed ({CurrentLoop} loop(s))", "success");
            return true;
        }
        catch (OperationCanceledException)
        {
            TryActivity($"Macro '{sequence.Name}' stopped", "warning");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _engine.Logger.LogWarning("Macro '{Name}' aborted: {Reason}", sequence.Name, ex.Message);
            TryActivity($"Macro '{sequence.Name}' aborted — {ex.Message}", "warning");
            return false;
        }
        catch (Exception ex)
        {
            _engine.Logger.LogError(ex, "Macro '{Name}' failed", sequence.Name);
            TryActivity($"Macro '{sequence.Name}' failed", "warning");
            return false;
        }
        finally
        {
            foreach (var vk in heldKeys)
            {
                try { sim.KeyUp(vk); }
                catch { /* best-effort release */ }
            }

            lock (_gate)
            {
                _cts = null;
                _state = MacroRunnerState.Idle;
                CurrentSequenceName = null;
                CurrentStepIndex = -1;
                if (!completed) CurrentLoop = 0;
            }
            cts.Dispose();
            RaiseState(MacroRunnerState.Idle);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state != MacroRunnerState.Running) return;
            _state = MacroRunnerState.Stopping;
            cts = _cts;
        }
        RaiseState(MacroRunnerState.Stopping);
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* run finished between the lock and here */ }
    }

    private async Task ExecuteStepAsync(
        MacroStep step, MacroSequence sequence, IInputSimulator sim, HashSet<int> heldKeys, CancellationToken token)
    {
        switch (step.Type)
        {
            case MacroStepType.KeyDown:
                sim.KeyDown(step.VirtualKey);
                heldKeys.Add(step.VirtualKey);
                break;
            case MacroStepType.KeyUp:
                sim.KeyUp(step.VirtualKey);
                heldKeys.Remove(step.VirtualKey);
                break;
            case MacroStepType.KeyPress:
                await sim.KeyPressAsync(step.VirtualKey, 40, sequence.DelayJitter, token);
                break;
            case MacroStepType.ClickAt:
                await sim.ClickAsync(step.Button, new Point(step.X, step.Y), 30, sequence.DelayJitter, token);
                break;
            case MacroStepType.MoveTo:
                sim.MoveTo(step.X, step.Y);
                break;
            case MacroStepType.Delay:
                await sim.DelayAsync(step.DelayMs, sequence.DelayJitter, token);
                break;
            case MacroStepType.FocusGameWindow:
                // Failing to focus means every following event would land in some random app —
                // abort instead of spraying input at the wrong window.
                if (!await _engine.FocusGameWindowAsync(token))
                    throw new InvalidOperationException("game window not available");
                break;
        }
    }

    private void RaiseState(MacroRunnerState state)
    {
        try { StateChanged?.Invoke(state); }
        catch { /* subscriber errors must not kill the runner */ }
    }

    private void TryActivity(string title, string type)
    {
        try { _engine.Activity.AddActivity(title, type); }
        catch { /* activity is best-effort */ }
    }
}
