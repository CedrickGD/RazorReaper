using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Gamma;

/// <summary>
/// Installs global low-level keyboard + mouse hooks on a dedicated STA message-pumped thread
/// (same plumbing as <c>InputRecorderService</c>). While listening it raises
/// <see cref="TriggerFired"/> for bound triggers and PASSES THE INPUT THROUGH (never swallows
/// it — a deliberate deviation from GammaHotkey; F13–F24 from G HUB have no other effect, and
/// the user asked for passthrough). In capture mode the next key / mouse button is reported
/// once and swallowed (so binding a key doesn't leak into the app). Events are marshaled onto
/// the thread pool so a subscriber can never stall the low-level hook.
/// Ported from GammaHotkey/Services/HookService.cs + InputCapture.cs.
/// </summary>
public sealed class GammaTriggerService : IDisposable
{
    private readonly ILogger<GammaTriggerService>? _logger;

    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;
    private volatile bool _installFailed;
    private volatile bool _disposed;

    private IntPtr _kbHook;
    private IntPtr _mouseHook;
    private GammaNative.HookProc? _kbProc;     // kept alive so the GC can't collect them
    private GammaNative.HookProc? _mouseProc;

    private volatile bool _listening;
    private volatile HashSet<TriggerInput> _bound = new();

    private readonly object _captureLock = new();
    private bool _capturing;                 // guarded by _captureLock
    private Action<TriggerInput>? _onCaptured;
    private Action? _onCaptureCancelled;

    public GammaTriggerService(ILogger<GammaTriggerService>? logger = null) => _logger = logger;

    /// <summary>Raised (on a thread-pool thread) when a bound trigger fires while listening.</summary>
    public event Action<TriggerInput>? TriggerFired;

    public bool IsListening
    {
        get => _listening;
        set => _listening = value;
    }

    /// <summary>True once the hook thread is running with both hooks installed.</summary>
    public bool IsRunning => _thread is { IsAlive: true } && !_installFailed;

    /// <summary>Refreshes the set of triggers that count as "bound" (raise while listening).</summary>
    public void UpdateBindings(IEnumerable<TriggerInput> bound)
    {
        _bound = new HashSet<TriggerInput>(bound.Where(t => !t.IsEmpty));
    }

    /// <summary>Starts the hook thread. Returns false if hooks could not be installed.</summary>
    public bool Start()
    {
        if (_disposed || _thread != null)
            return _thread != null && !_installFailed;

        _installFailed = false;
        _ready = new ManualResetEventSlim(false);
        _thread = new Thread(PumpThread)
        {
            IsBackground = true,
            Name = "RazorReaper.GammaHookPump",
            // Above-normal so the callback keeps beating LowLevelHooksTimeout even when a
            // fullscreen game is hammering the CPU.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(2)) || _installFailed)
        {
            _logger?.LogError("Gamma trigger hooks could not be installed.");
            Stop();
            return false;
        }
        return true;
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread == null)
            return;
        try
        {
            if (_threadId != 0)
                GammaNative.PostThreadMessage(_threadId, GammaNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Gamma hook thread teardown failed");
        }
        finally
        {
            _thread = null;
            _threadId = 0;
            _ready?.Dispose();
            _ready = null;
        }
    }

    /// <summary>Arms one-shot capture of the next key / mouse button (starts the hook thread if needed).</summary>
    public void BeginCapture(Action<TriggerInput> onCaptured, Action onCancelled)
    {
        if (!IsRunning)
            Start();
        lock (_captureLock)
        {
            _onCaptured = onCaptured;
            _onCaptureCancelled = onCancelled;
            _capturing = true;
        }
    }

    public void CancelCapture()
    {
        Action? cancelled = null;
        lock (_captureLock)
        {
            if (_capturing)
            {
                cancelled = _onCaptureCancelled;
                ClearCaptureLocked();
            }
        }
        if (cancelled != null)
            Dispatch(cancelled);
    }

    // ------------------------------------------------------------ pump thread

    private void PumpThread()
    {
        try
        {
            _threadId = GammaNative.GetCurrentThreadId();
            _kbProc = KeyboardProc;
            _mouseProc = MouseProc;
            IntPtr hMod = GammaNative.GetModuleHandle(null);
            _kbHook = GammaNative.SetWindowsHookEx(GammaNative.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _mouseHook = GammaNative.SetWindowsHookEx(GammaNative.WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_kbHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                _logger?.LogError("SetWindowsHookEx failed: kb=0x{Kb:X} mouse=0x{Ms:X} err=0x{Err:X}",
                    _kbHook, _mouseHook, Marshal.GetLastWin32Error());
                _installFailed = true;
                return;
            }
            _ready?.Set();

            while (GammaNative.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                GammaNative.TranslateMessage(ref msg);
                GammaNative.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Gamma hook thread crashed");
            _installFailed = true;
        }
        finally
        {
            if (_kbHook != IntPtr.Zero)
                GammaNative.UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero)
                GammaNative.UnhookWindowsHookEx(_mouseHook);
            _kbHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
            _ready?.Set();
        }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = (int)wParam;
                if (msg == GammaNative.WM_KEYDOWN || msg == GammaNative.WM_SYSKEYDOWN)
                {
                    var k = Marshal.PtrToStructure<GammaNative.KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)k.vkCode;

                    if (TryHandleCaptureKey(vk, out bool swallowCapture))
                    {
                        if (swallowCapture)
                            return (IntPtr)1;
                    }
                    else if (_listening)
                    {
                        var t = TriggerInput.Key(vk);
                        if (_bound.Contains(t))
                            RaiseTrigger(t);   // pass through — do NOT swallow
                    }
                }
            }
            catch { /* never let a hook callback throw back into the OS */ }
        }
        return GammaNative.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = (int)wParam;
                TriggerInput? t = null;

                if (msg == GammaNative.WM_MBUTTONDOWN)
                {
                    t = TriggerInput.Mouse(MouseButton.Middle);
                }
                else if (msg == GammaNative.WM_XBUTTONDOWN)
                {
                    var m = Marshal.PtrToStructure<GammaNative.MSLLHOOKSTRUCT>(lParam);
                    int xb = (short)(m.mouseData >> 16);
                    t = xb switch
                    {
                        GammaNative.XBUTTON1 => TriggerInput.Mouse(MouseButton.XButton1),
                        GammaNative.XBUTTON2 => TriggerInput.Mouse(MouseButton.XButton2),
                        _ => null,
                    };
                }

                if (t is { } trigger)
                {
                    if (TryHandleCaptureMouse(trigger, out bool swallowCapture))
                    {
                        if (swallowCapture)
                            return (IntPtr)1;
                    }
                    else if (_listening && _bound.Contains(trigger))
                    {
                        RaiseTrigger(trigger);   // pass through — do NOT swallow
                    }
                }
            }
            catch { /* never let a hook callback throw back into the OS */ }
        }
        return GammaNative.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// Handles a keyboard event while capture is armed, atomically under the lock. Returns true
    /// if we are in capture mode (and sets <paramref name="swallow"/>); false if not capturing,
    /// so the caller runs its normal dispatch path.
    /// </summary>
    private bool TryHandleCaptureKey(int vk, out bool swallow)
    {
        swallow = false;
        Action<TriggerInput>? captured = null;
        Action? cancelled = null;
        lock (_captureLock)
        {
            if (!_capturing)
                return false;

            if (vk == KeyNames.VK_ESCAPE)
            {
                cancelled = _onCaptureCancelled;
                ClearCaptureLocked();
                swallow = true;
            }
            else if (!KeyNames.IsModifierOrLock(vk))
            {
                captured = _onCaptured;
                ClearCaptureLocked();
                swallow = true;
            }
            // else: a lone modifier / lock — stay armed and let it pass through.
        }

        if (captured != null)
        {
            var t = TriggerInput.Key(vk);
            Dispatch(() => captured(t));
        }
        if (cancelled != null)
            Dispatch(cancelled);
        return true;
    }

    private bool TryHandleCaptureMouse(TriggerInput trigger, out bool swallow)
    {
        swallow = false;
        Action<TriggerInput>? captured;
        lock (_captureLock)
        {
            if (!_capturing)
                return false;
            captured = _onCaptured;
            ClearCaptureLocked();
            swallow = true;
        }
        if (captured != null)
            Dispatch(() => captured(trigger));
        return true;
    }

    private void ClearCaptureLocked()
    {
        _capturing = false;
        _onCaptured = null;
        _onCaptureCancelled = null;
    }

    private void RaiseTrigger(TriggerInput t)
    {
        var handler = TriggerFired;
        if (handler != null)
            Dispatch(() => handler(t));
    }

    /// <summary>Runs a callback off the hook thread so a slow subscriber can't trip
    /// LowLevelHooksTimeout. Blazor subscribers marshal to the UI thread themselves.</summary>
    private static void Dispatch(Action action)
        => ThreadPool.QueueUserWorkItem(_ =>
        {
            try { action(); }
            catch { /* subscriber errors are not ours */ }
        });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _listening = false;
        Stop();
    }
}
