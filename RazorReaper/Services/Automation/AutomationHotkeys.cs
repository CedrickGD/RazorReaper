using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Global hotkey registry for automation features. Multiple features can hold registrations at
/// once; each registration maps one key combination to one callback. Hotkeys fire even while the
/// game has focus (Win32 <c>RegisterHotKey</c> is system-wide), which is what makes hard-stop
/// hotkeys reliable. Callbacks are invoked on the thread pool — never block the message loop.
/// </summary>
public interface IAutomationHotkeyService : IDisposable
{
    /// <summary>
    /// Registers a system-wide hotkey. Returns a positive registration id on success, or 0 when
    /// registration failed (e.g. the combination is already taken by another app).
    /// </summary>
    /// <param name="virtualKey">Win32 virtual-key code (must be &gt; 0).</param>
    /// <param name="ctrl">Require Ctrl.</param>
    /// <param name="alt">Require Alt.</param>
    /// <param name="shift">Require Shift.</param>
    /// <param name="callback">Invoked (on the thread pool) each time the hotkey fires.</param>
    int RegisterHotkey(int virtualKey, bool ctrl, bool alt, bool shift, Action callback);

    /// <summary>Unregisters a previously registered hotkey. Safe to call with an unknown id.</summary>
    void UnregisterHotkey(int registrationId);

    /// <summary>True while the given registration id maps to an active hotkey.</summary>
    bool IsRegistered(int registrationId);

    /// <summary>Unregisters every hotkey owned by this service.</summary>
    void UnregisterAll();
}

/// <summary>
/// Mirrors the crosshair overlay's hotkey plumbing (dedicated STA thread + message-only window;
/// Win32 registration marshalled onto the pump thread via PostMessage) but generalized to many
/// concurrent id→callback registrations. The crosshair mechanism itself is single-slot and
/// private to its overlay window, so it is mirrored here rather than wrapped.
/// </summary>
public sealed class AutomationHotkeyService : IAutomationHotkeyService
{
    private const int HotkeyDebounceMs = 150;

    private readonly ILogger<AutomationHotkeyService> _logger;
    private readonly object _startLock = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ConcurrentDictionary<int, Action> _callbacks = new();
    private readonly ConcurrentDictionary<int, int> _registeredKeys = new();
    private readonly ConcurrentDictionary<int, long> _lastFired = new();
    private readonly ConcurrentQueue<HotkeyOp> _ops = new();

    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private WndProcDelegate? _wndProc;
    private int _nextId; // ids start at 1 — RegisterHotKey ids just need to be unique per window
    private volatile bool _disposed;

    // Owned exclusively by the hotkey thread — tracks which ids are Win32-registered so
    // WM_DESTROY can release them all.
    private readonly HashSet<int> _registeredIds = new();

    public AutomationHotkeyService(ILogger<AutomationHotkeyService> logger)
    {
        _logger = logger;
    }

    public int RegisterHotkey(int virtualKey, bool ctrl, bool alt, bool shift, Action callback)
    {
        if (_disposed || virtualKey <= 0 || callback is null) return 0;

        try
        {
            EnsureThread();
            if (_hwnd == IntPtr.Zero) return 0;

            var id = Interlocked.Increment(ref _nextId);
            _callbacks[id] = callback;
            _registeredKeys[id] = virtualKey;

            var op = new HotkeyOp
            {
                IsRegister = true,
                Id = id,
                Vk = (uint)virtualKey,
                Mods = BuildModFlags(ctrl, alt, shift)
            };
            _ops.Enqueue(op);
            PostMessage(_hwnd, WM_USER_APPLY, IntPtr.Zero, IntPtr.Zero);

            var ok = false;
            try { ok = op.Done.Task.Wait(TimeSpan.FromSeconds(2)) && op.Done.Task.Result; }
            catch { /* wait/task faults treated as failure below */ }

            if (!ok)
            {
                _callbacks.TryRemove(id, out _);
                return 0;
            }
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RegisterHotkey failed for vk=0x{Vk:X}", virtualKey);
            return 0;
        }
    }

    public void UnregisterHotkey(int registrationId)
    {
        if (registrationId <= 0) return;
        // Remove the callback first so the hotkey can't fire again even before the Win32
        // unregistration lands on the pump thread.
        _callbacks.TryRemove(registrationId, out _);
        _registeredKeys.TryRemove(registrationId, out _);
        _lastFired.TryRemove(registrationId, out _);
        if (_hwnd == IntPtr.Zero) return;

        _ops.Enqueue(new HotkeyOp { IsRegister = false, Id = registrationId });
        PostMessage(_hwnd, WM_USER_APPLY, IntPtr.Zero, IntPtr.Zero);
    }

    public bool IsRegistered(int registrationId) => _callbacks.ContainsKey(registrationId);

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys.ToArray())
            UnregisterHotkey(id);
    }

    // ─── Thread / window lifecycle ─────────────────────────────────────────────

    private void EnsureThread()
    {
        if (_thread != null)
        {
            _started.Wait(TimeSpan.FromSeconds(3));
            return;
        }
        lock (_startLock)
        {
            if (_thread != null)
            {
                // Started by a racing caller — fall through to the wait below.
            }
            else
            {
                _thread = new Thread(RunMessageLoop)
                {
                    IsBackground = true,
                    Name = "Automation Hotkeys"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        _started.Wait(TimeSpan.FromSeconds(3));
    }

    private void RunMessageLoop()
    {
        try
        {
            _wndProc = WndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = WindowClassName
            };
            var atom = RegisterClassEx(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    _logger.LogError("RegisterClassEx failed for hotkey window: 0x{Err:X}", err);
                    _started.Set();
                    return;
                }
            }

            // Message-only window (parent HWND_MESSAGE) — invisible, no paint, still receives
            // WM_HOTKEY and posted messages.
            _hwnd = CreateWindowEx(
                0, WindowClassName, "RazorReaper Automation Hotkeys", 0,
                0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _logger.LogError("CreateWindowEx failed for hotkey window: 0x{Err:X}", Marshal.GetLastWin32Error());
                _started.Set();
                return;
            }

            _started.Set();

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automation hotkey message loop crashed");
            _started.Set();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                HandleHotkey((int)wParam);
                return IntPtr.Zero;
            case WM_USER_APPLY:
                DrainOps(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                foreach (var id in _registeredIds)
                    UnregisterHotKey(hwnd, id);
                _registeredIds.Clear();
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void HandleHotkey(int id)
    {
        if (!_callbacks.TryGetValue(id, out var callback)) return;

        // Never fire on input we produced ourselves. A running script holding or tapping a key
        // would otherwise toggle whatever hotkey sits on that key — and the script it starts can
        // do the same again, cascading.
        if (_registeredKeys.TryGetValue(id, out var vk) && SynthesizedInput.IsActive(vk))
        {
            _logger.LogDebug("Hotkey id={Id} ignored: vk=0x{Vk:X2} is currently synthesized by us", id, vk);
            return;
        }

        // Debounce accidental double-taps; MOD_NOREPEAT already suppresses auto-repeat.
        var now = Environment.TickCount64;
        var last = _lastFired.GetOrAdd(id, 0);
        if (now - last < HotkeyDebounceMs) return;
        _lastFired[id] = now;

        _ = Task.Run(() =>
        {
            try { callback(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Automation hotkey callback threw (id={Id})", id); }
        });
    }

    private void DrainOps(IntPtr hwnd)
    {
        while (_ops.TryDequeue(out var op))
        {
            try
            {
                if (op.IsRegister)
                {
                    var ok = RegisterHotKey(hwnd, op.Id, op.Mods, op.Vk);
                    if (ok) _registeredIds.Add(op.Id);
                    else _logger.LogWarning("RegisterHotKey failed: vk=0x{Vk:X} mods=0x{Mods:X} err=0x{Err:X}",
                        op.Vk, op.Mods, Marshal.GetLastWin32Error());
                    op.Done.TrySetResult(ok);
                }
                else
                {
                    if (_registeredIds.Remove(op.Id))
                        UnregisterHotKey(hwnd, op.Id);
                    op.Done.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hotkey op failed (id={Id})", op.Id);
                op.Done.TrySetResult(false);
            }
        }
    }

    private static uint BuildModFlags(bool ctrl, bool alt, bool shift)
    {
        var mods = MOD_NOREPEAT;
        if (ctrl) mods |= MOD_CONTROL;
        if (alt) mods |= MOD_ALT;
        if (shift) mods |= MOD_SHIFT;
        return mods;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _callbacks.Clear();
        _registeredKeys.Clear();
        _lastFired.Clear();

        if (_hwnd != IntPtr.Zero)
        {
            // DefWindowProc(WM_CLOSE) → DestroyWindow → WM_DESTROY (unregisters all + quits loop).
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private sealed class HotkeyOp
    {
        public bool IsRegister;
        public int Id;
        public uint Vk;
        public uint Mods;
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    private const string WindowClassName = "RazorReaperAutomationHotkeys";
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_USER_APPLY = 0x0400 + 21;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPTStr)] public string lpszClassName;
        public IntPtr hIconSm;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVEPOINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
