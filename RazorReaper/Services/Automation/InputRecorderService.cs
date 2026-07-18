using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation;

/// <summary>Kind of input event captured by the recorder.</summary>
public enum RecordedEventType
{
    /// <summary>A key went down.</summary>
    KeyDown,
    /// <summary>A key went up.</summary>
    KeyUp,
    /// <summary>The cursor moved (throttled to ~60 Hz).</summary>
    MouseMove,
    /// <summary>A mouse button went down at (X, Y).</summary>
    MouseDown,
    /// <summary>A mouse button went up at (X, Y).</summary>
    MouseUp,
    /// <summary>The wheel scrolled by <c>ScrollDelta</c> detents.</summary>
    Scroll
}

/// <summary>One timestamped input event. Timestamps are milliseconds since recording start.</summary>
public sealed record RecordedInputEvent(
    long TimestampMs,
    RecordedEventType Type,
    int VirtualKey,
    int X,
    int Y,
    int ScrollDelta,
    MouseButton Button);

/// <summary>A named series of recorded input events, serializable to JSON.</summary>
public sealed class InputRecording
{
    /// <summary>Recording name; doubles as the file name under %LOCALAPPDATA%\RazorReaper\Macros.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>When the recording was made (UTC).</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>Total recorded duration in milliseconds.</summary>
    public long DurationMs { get; set; }
    /// <summary>The captured events in chronological order.</summary>
    public List<RecordedInputEvent> Events { get; set; } = new();
}

/// <summary>
/// Records real keyboard/mouse input via low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL on a
/// dedicated STA message-pump thread, mirroring the crosshair overlay's thread pattern) and
/// replays recordings through <see cref="IInputSimulator"/> preserving original timing.
/// Injected events (our own SendInput output) are never recorded, so replay while recording can
/// not feedback-loop. Callers own the record/stop/panic hotkeys — pass those keys as exclusions.
/// </summary>
public interface IInputRecorderService : IDisposable
{
    /// <summary>True while hooks are installed and events are being captured.</summary>
    bool IsRecording { get; }

    /// <summary>True while a replay is executing.</summary>
    bool IsReplaying { get; }

    /// <summary>Number of events captured in the active (or last) recording session.</summary>
    int CapturedEventCount { get; }

    /// <summary>Milliseconds elapsed in the active recording session (0 when idle).</summary>
    long RecordingElapsedMs { get; }

    /// <summary>Raised when recording or replay starts/stops. May fire on a background thread.</summary>
    event Action? StateChanged;

    /// <summary>Raised (on the hook thread) each time an event is captured, with the running count.</summary>
    event Action<int>? EventCaptured;

    /// <summary>
    /// Starts capturing input. Returns false when already recording, replaying, or the hooks
    /// could not be installed.
    /// </summary>
    /// <param name="excludedVirtualKeys">
    /// Virtual keys to omit from the recording — pass the record/stop/panic hotkey keys here so
    /// pressing "stop recording" does not end up inside the recording itself.
    /// </param>
    bool StartRecording(IReadOnlyCollection<int>? excludedVirtualKeys = null);

    /// <summary>Stops capturing and returns the recording (unnamed), or null if not recording.</summary>
    InputRecording? StopRecording();

    /// <summary>
    /// Replays a recording with original timing scaled by <paramref name="speed"/> (1.0 = real
    /// time, clamped to 0.1–10). Returns true when the replay ran to completion. Cancellable via
    /// <paramref name="ct"/> or <see cref="StopReplay"/>; keys/buttons still down at cancellation
    /// are released automatically.
    /// </summary>
    Task<bool> ReplayAsync(InputRecording recording, double speed = 1.0, CancellationToken ct = default);

    /// <summary>Requests a hard stop of the running replay. Safe to call from hotkey callbacks.</summary>
    void StopReplay();

    /// <summary>Names of all saved recordings, sorted alphabetically.</summary>
    IReadOnlyList<string> ListRecordings();

    /// <summary>Saves a recording (by its <see cref="InputRecording.Name"/>), overwriting any same-named file.</summary>
    Task<bool> SaveRecordingAsync(InputRecording recording);

    /// <summary>Loads a saved recording by name, or null when missing/corrupt.</summary>
    Task<InputRecording?> LoadRecordingAsync(string name);

    /// <summary>Deletes a saved recording. Returns true when a file was removed.</summary>
    bool DeleteRecording(string name);

    /// <summary>Renames a saved recording (updates the stored name too). Returns false when the target exists.</summary>
    bool RenameRecording(string oldName, string newName);
}

/// <summary>Default <see cref="IInputRecorderService"/> implementation.</summary>
public sealed class InputRecorderService : IInputRecorderService
{
    private const int MoveThrottleMs = 16; // ~60 Hz

    private static readonly string MacrosFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RazorReaper",
        "Macros");

    private readonly IInputSimulator _sim;
    private readonly IActivityService _activity;
    private readonly ILogger<InputRecorderService> _logger;

    private readonly object _stateGate = new();
    private readonly object _eventsLock = new();
    private volatile bool _recording;
    private volatile bool _replaying;
    private volatile bool _disposed;

    // Recording session state — assigned before the hook thread starts, read from the hook thread.
    private List<RecordedInputEvent> _events = new();
    private HashSet<int> _excluded = new();
    private Stopwatch _recordClock = new();
    private long _lastMoveMs;
    private int _capturedCount;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private ManualResetEventSlim? _hookReady;
    private volatile bool _hookInstallFailed;

    // Keep hook delegates rooted — a collected delegate behind an installed hook crashes the process.
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;

    private CancellationTokenSource? _replayCts;

    public InputRecorderService(
        IInputSimulator sim,
        IActivityService activity,
        ILogger<InputRecorderService> logger)
    {
        _sim = sim;
        _activity = activity;
        _logger = logger;
    }

    public bool IsRecording => _recording;
    public bool IsReplaying => _replaying;
    public int CapturedEventCount => Volatile.Read(ref _capturedCount);
    public long RecordingElapsedMs => _recording ? _recordClock.ElapsedMilliseconds : 0;

    public event Action? StateChanged;
    public event Action<int>? EventCaptured;

    // ─── Recording ─────────────────────────────────────────────────────────────

    public bool StartRecording(IReadOnlyCollection<int>? excludedVirtualKeys = null)
    {
        lock (_stateGate)
        {
            if (_disposed || _recording || _replaying) return false;
            _recording = true;
        }

        try
        {
            _events = new List<RecordedInputEvent>();
            _excluded = excludedVirtualKeys is null ? new HashSet<int>() : new HashSet<int>(excludedVirtualKeys);
            _lastMoveMs = -MoveThrottleMs;
            Volatile.Write(ref _capturedCount, 0);
            _hookInstallFailed = false;
            _hookReady = new ManualResetEventSlim(false);
            _recordClock = Stopwatch.StartNew();

            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "Automation Input Recorder"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            if (!_hookReady.Wait(TimeSpan.FromSeconds(3)) || _hookInstallFailed)
            {
                _logger.LogError("Input hooks could not be installed — recording aborted.");
                TearDownHookThread();
                lock (_stateGate) { _recording = false; }
                return false;
            }

            TryActivity("Input recording started", "info");
            RaiseStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartRecording failed");
            TearDownHookThread();
            lock (_stateGate) { _recording = false; }
            return false;
        }
    }

    public InputRecording? StopRecording()
    {
        lock (_stateGate)
        {
            if (!_recording) return null;
            _recording = false;
        }

        _recordClock.Stop();
        TearDownHookThread();

        InputRecording recording;
        lock (_eventsLock)
        {
            recording = new InputRecording
            {
                Name = string.Empty,
                CreatedUtc = DateTime.UtcNow,
                DurationMs = _recordClock.ElapsedMilliseconds,
                Events = new List<RecordedInputEvent>(_events)
            };
        }

        TryActivity($"Input recording stopped ({recording.Events.Count} events)", "info");
        RaiseStateChanged();
        return recording;
    }

    private void TearDownHookThread()
    {
        try
        {
            if (_hookThread is { IsAlive: true } && _hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hookThread.Join(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hook thread teardown failed");
        }
        finally
        {
            _hookThread = null;
            _hookThreadId = 0;
            _hookReady?.Dispose();
            _hookReady = null;
        }
    }

    private void HookThreadProc()
    {
        IntPtr keyboardHook = IntPtr.Zero;
        IntPtr mouseHook = IntPtr.Zero;
        try
        {
            _hookThreadId = GetCurrentThreadId();
            _keyboardProc = KeyboardHookProc;
            _mouseProc = MouseHookProc;

            // LL hooks are not injected into other processes; the module handle only anchors the
            // hook, so our own main module is correct here.
            var hMod = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            {
                _logger.LogError("SetWindowsHookEx failed: kb=0x{Kb:X} mouse=0x{Ms:X} err=0x{Err:X}",
                    keyboardHook, mouseHook, Marshal.GetLastWin32Error());
                _hookInstallFailed = true;
                return;
            }

            _hookReady?.Set();

            // LL hooks require a message pump on the installing thread; WM_QUIT (posted by
            // TearDownHookThread) makes GetMessage return 0 and ends the loop.
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Input recorder hook thread crashed");
            _hookInstallFailed = true;
        }
        finally
        {
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            _hookReady?.Set();
        }
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _recording)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Skip our own synthesized events and the caller's control hotkeys.
                if ((data.flags & LLKHF_INJECTED) == 0 && !_excluded.Contains((int)data.vkCode))
                {
                    var msg = (int)wParam;
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                        Record(new RecordedInputEvent(_recordClock.ElapsedMilliseconds, RecordedEventType.KeyDown, (int)data.vkCode, 0, 0, 0, MouseButton.Left));
                    else if (msg is WM_KEYUP or WM_SYSKEYUP)
                        Record(new RecordedInputEvent(_recordClock.ElapsedMilliseconds, RecordedEventType.KeyUp, (int)data.vkCode, 0, 0, 0, MouseButton.Left));
                }
            }
            catch { /* never let a hook callback throw back into the OS */ }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _recording)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((data.flags & LLMHF_INJECTED) == 0)
                {
                    var now = _recordClock.ElapsedMilliseconds;
                    switch ((int)wParam)
                    {
                        case WM_MOUSEMOVE:
                            if (now - _lastMoveMs >= MoveThrottleMs)
                            {
                                _lastMoveMs = now;
                                Record(new RecordedInputEvent(now, RecordedEventType.MouseMove, 0, data.pt.X, data.pt.Y, 0, MouseButton.Left));
                            }
                            break;
                        case WM_LBUTTONDOWN:
                            Record(new RecordedInputEvent(now, RecordedEventType.MouseDown, 0, data.pt.X, data.pt.Y, 0, MouseButton.Left));
                            break;
                        case WM_LBUTTONUP:
                            Record(new RecordedInputEvent(now, RecordedEventType.MouseUp, 0, data.pt.X, data.pt.Y, 0, MouseButton.Left));
                            break;
                        case WM_RBUTTONDOWN:
                            Record(new RecordedInputEvent(now, RecordedEventType.MouseDown, 0, data.pt.X, data.pt.Y, 0, MouseButton.Right));
                            break;
                        case WM_RBUTTONUP:
                            Record(new RecordedInputEvent(now, RecordedEventType.MouseUp, 0, data.pt.X, data.pt.Y, 0, MouseButton.Right));
                            break;
                        case WM_MBUTTONDOWN:
                            Record(new RecordedInputEvent(now, RecordedEventType.MouseDown, 0, data.pt.X, data.pt.Y, 0, MouseButton.Middle));
                            break;
                        case WM_MBUTTONUP:
                            Record(new RecordedInputEvent(now, RecordedEventType.MouseUp, 0, data.pt.X, data.pt.Y, 0, MouseButton.Middle));
                            break;
                        case WM_MOUSEWHEEL:
                            var delta = (short)((data.mouseData >> 16) & 0xFFFF) / WHEEL_DELTA;
                            if (delta != 0)
                                Record(new RecordedInputEvent(now, RecordedEventType.Scroll, 0, data.pt.X, data.pt.Y, delta, MouseButton.Left));
                            break;
                    }
                }
            }
            catch { /* never let a hook callback throw back into the OS */ }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Record(RecordedInputEvent evt)
    {
        int count;
        lock (_eventsLock)
        {
            _events.Add(evt);
            count = _events.Count;
        }
        Volatile.Write(ref _capturedCount, count);
        try { EventCaptured?.Invoke(count); }
        catch { /* subscriber errors must not break the hook */ }
    }

    // ─── Replay ────────────────────────────────────────────────────────────────

    public async Task<bool> ReplayAsync(InputRecording recording, double speed = 1.0, CancellationToken ct = default)
    {
        if (recording?.Events is null || recording.Events.Count == 0) return false;

        CancellationTokenSource cts;
        lock (_stateGate)
        {
            if (_disposed || _recording || _replaying) return false;
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _replayCts = cts;
            _replaying = true;
        }
        RaiseStateChanged();

        speed = double.IsFinite(speed) ? Math.Clamp(speed, 0.1, 10.0) : 1.0;
        var token = cts.Token;
        var downKeys = new HashSet<int>();
        var downButtons = new HashSet<MouseButton>();
        var displayName = string.IsNullOrWhiteSpace(recording.Name) ? "recording" : $"'{recording.Name}'";

        try
        {
            var clock = Stopwatch.StartNew();
            foreach (var e in recording.Events)
            {
                token.ThrowIfCancellationRequested();

                var targetMs = (long)(e.TimestampMs / speed);
                var wait = targetMs - clock.ElapsedMilliseconds;
                if (wait > 0)
                    await Task.Delay((int)Math.Min(wait, int.MaxValue), token);

                switch (e.Type)
                {
                    case RecordedEventType.KeyDown:
                        _sim.KeyDown(e.VirtualKey);
                        downKeys.Add(e.VirtualKey);
                        break;
                    case RecordedEventType.KeyUp:
                        _sim.KeyUp(e.VirtualKey);
                        downKeys.Remove(e.VirtualKey);
                        break;
                    case RecordedEventType.MouseMove:
                        _sim.MoveTo(e.X, e.Y);
                        break;
                    case RecordedEventType.MouseDown:
                        _sim.MoveTo(e.X, e.Y);
                        _sim.MouseDown(e.Button);
                        downButtons.Add(e.Button);
                        break;
                    case RecordedEventType.MouseUp:
                        _sim.MoveTo(e.X, e.Y);
                        _sim.MouseUp(e.Button);
                        downButtons.Remove(e.Button);
                        break;
                    case RecordedEventType.Scroll:
                        _sim.Scroll(e.ScrollDelta);
                        break;
                }
            }

            TryActivity($"Replay of {displayName} completed", "success");
            return true;
        }
        catch (OperationCanceledException)
        {
            TryActivity($"Replay of {displayName} stopped", "warning");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replay failed");
            return false;
        }
        finally
        {
            // A panic-stop mid-replay must not leave keys or buttons stuck down.
            foreach (var vk in downKeys)
            {
                try { _sim.KeyUp(vk); } catch { /* best-effort */ }
            }
            foreach (var b in downButtons)
            {
                try { _sim.MouseUp(b); } catch { /* best-effort */ }
            }

            lock (_stateGate)
            {
                _replaying = false;
                _replayCts = null;
            }
            cts.Dispose();
            RaiseStateChanged();
        }
    }

    public void StopReplay()
    {
        CancellationTokenSource? cts;
        lock (_stateGate)
        {
            cts = _replayCts;
        }
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* replay finished concurrently */ }
    }

    // ─── Persistence ───────────────────────────────────────────────────────────

    public IReadOnlyList<string> ListRecordings()
    {
        try
        {
            if (!Directory.Exists(MacrosFolder)) return Array.Empty<string>();
            return Directory.GetFiles(MacrosFolder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list recordings");
            return Array.Empty<string>();
        }
    }

    public async Task<bool> SaveRecordingAsync(InputRecording recording)
    {
        if (recording is null || string.IsNullOrWhiteSpace(recording.Name)) return false;
        try
        {
            recording.Name = SanitizeName(recording.Name);
            Directory.CreateDirectory(MacrosFolder);
            var path = PathFor(recording.Name);
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(recording, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, path, overwrite: true);
            TryActivity($"Recording '{recording.Name}' saved", "success");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save recording '{Name}'", recording.Name);
            return false;
        }
    }

    public async Task<InputRecording?> LoadRecordingAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var path = PathFor(SanitizeName(name));
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<InputRecording>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recording '{Name}'", name);
            return null;
        }
    }

    public bool DeleteRecording(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            var path = PathFor(SanitizeName(name));
            if (!File.Exists(path)) return false;
            File.Delete(path);
            TryActivity($"Recording '{name}' deleted", "info");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete recording '{Name}'", name);
            return false;
        }
    }

    public bool RenameRecording(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
        try
        {
            var oldPath = PathFor(SanitizeName(oldName));
            var cleanNew = SanitizeName(newName);
            var newPath = PathFor(cleanNew);
            if (!File.Exists(oldPath) || File.Exists(newPath)) return false;

            // Rewrite the stored name so the file content stays consistent with the file name.
            var json = File.ReadAllText(oldPath);
            var recording = JsonSerializer.Deserialize<InputRecording>(json);
            if (recording is null) return false;
            recording.Name = cleanNew;
            File.WriteAllText(newPath, JsonSerializer.Serialize(recording, new JsonSerializerOptions { WriteIndented = true }));
            File.Delete(oldPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename recording '{Old}' to '{New}'", oldName, newName);
            return false;
        }
    }

    private static string PathFor(string name) => Path.Combine(MacrosFolder, name + ".json");

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    // ─── Misc ──────────────────────────────────────────────────────────────────

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch { /* subscriber errors are not ours */ }
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
        StopReplay();
        lock (_stateGate) { _recording = false; }
        TearDownHookThread();
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const uint WM_QUIT = 0x0012;

    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;
    private const int WHEEL_DELTA = 120;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVEPOINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public NATIVEPOINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NATIVEPOINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
