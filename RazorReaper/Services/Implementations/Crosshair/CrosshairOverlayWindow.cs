using System.Runtime.InteropServices;
using System.Drawing;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
// Disambiguate from Microsoft.Maui.* implicit usings (Color, Image, etc.).
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using Graphics = System.Drawing.Graphics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Owns a single Win32 layered window that draws the active crosshair. Runs its own STA thread
/// with a real message loop so we can host UpdateLayeredWindow + RegisterHotKey cleanly. The page
/// pushes profile updates via <see cref="Update"/> — cross-thread coordination is done by capturing
/// the next state under a lock and waking the UI thread with PostMessage(WM_USER+update).
///
/// Implementation is split across partials so each concern is readable on its own:
///  • <c>CrosshairOverlayWindow.cs</c> — fields, ctor, lifecycle, window creation, WndProc dispatch.
///  • <c>CrosshairOverlayWindow.Render.cs</c> — frame rendering, monitor resolution, animation tick.
///  • <c>CrosshairOverlayWindow.Tray.cs</c> — system tray icon and its right-click menu.
///  • <c>CrosshairOverlayWindow.Hotkey.cs</c> — global hotkey registration plumbing.
///  • <c>CrosshairOverlayWindow.Native.cs</c> — Win32 constants, structs, and P/Invoke signatures.
/// </summary>
internal sealed partial class CrosshairOverlayWindow : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action _onHotkeyToggle;
    private readonly Action _onTrayShowApp;
    private readonly Action _onTrayQuit;
    private readonly Func<bool> _isOverlayActive;

    private Thread? _uiThread;
    private readonly ManualResetEventSlim _started = new(false);
    private IntPtr _hwnd = IntPtr.Zero;
    private uint _uiThreadId;
    private bool _disposed;

    private readonly object _stateLock = new();
    private CrosshairProfile? _pendingProfile;
    private AnimatedImage? _cachedAnimated;
    private string? _cachedImagePath;
    private bool _visible;

    // Pooled render canvas, owned by the overlay UI thread. We re-allocate only when the required
    // canvas size changes — otherwise every Render() would alloc a 1–4 MB LOH bitmap, which at
    // 30 Hz adds up to gigabytes per minute of GC pressure (and was OOM'ing on big crosshairs).
    private Bitmap? _renderBuffer;
    private int _renderBufferSize;
    private MonitorInfo[] _monitors = Array.Empty<MonitorInfo>();

    private DateTime _animationStart = DateTime.UtcNow;
    private uint _hotkeyId;
    private int _registeredHotkeyVk;
    private uint _registeredHotkeyMods;

    private WndProcDelegate? _wndProc;
    private const string WindowClassName = "RazorReaperCrosshairOverlay";
    private bool _classRegistered;

    // Tray icon — registered on the same hwnd so we share the message loop.
    private bool _trayRegistered;
    private IntPtr _trayHIcon = IntPtr.Zero;
    private const uint TrayIconUID = 0xC050;

    public CrosshairOverlayWindow(
        ILogger logger,
        Action onHotkeyToggle,
        Action onTrayShowApp,
        Action onTrayQuit,
        Func<bool> isOverlayActive)
    {
        _logger = logger;
        _onHotkeyToggle = onHotkeyToggle;
        _onTrayShowApp = onTrayShowApp;
        _onTrayQuit = onTrayQuit;
        _isOverlayActive = isOverlayActive;
    }

    public void Start()
    {
        if (_uiThread != null) return;
        _uiThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "Crosshair Overlay UI"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        // WARNING: This blocks the calling thread until the overlay's STA message loop is ready.
        // Currently Start() is invoked from CrosshairService's constructor, which is resolved by
        // DI — the resolution thread depends on the first injector and may be the main app UI
        // thread. Blocking the UI thread here risks a perceptible freeze (rare) or, worst case,
        // deadlock if the message loop needs to interact back with the UI thread before signalling.
        // TODO: revisit — either resolve CrosshairService asynchronously from a known background
        // context, or replace this with an awaited TaskCompletionSource so the caller chooses
        // sync-vs-async. Until then, the bounded wait below at least bounds the freeze window.
        System.Diagnostics.Debug.WriteLine("[CrosshairOverlayWindow] Start(): blocking caller until message loop ready — verify caller is on a background thread.");
        _started.Wait();
    }

    public void Show(CrosshairProfile profile) => Update(profile, visible: true);

    public void Hide()
    {
        lock (_stateLock)
        {
            _visible = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_USER_UPDATE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Update(CrosshairProfile profile, bool visible)
    {
        // Snapshot state synchronously — fast.
        bool pathChanged;
        string? newPath = profile.ImagePath;
        lock (_stateLock)
        {
            _pendingProfile = profile;
            _visible = visible;
            pathChanged = profile.Type == CrosshairType.Image
                && !string.IsNullOrEmpty(newPath)
                && !string.Equals(_cachedImagePath, newPath, StringComparison.OrdinalIgnoreCase);

            // If we're no longer rendering an image, drop the cached frames so we don't hold
            // dozens of MB for nothing.
            if (profile.Type != CrosshairType.Image && _cachedAnimated != null)
            {
                _cachedAnimated.Dispose();
                _cachedAnimated = null;
                _cachedImagePath = null;
            }
        }

        // Wake the renderer right away — slider tweaks (size/rotation/etc) should be smooth
        // even while a fresh GIF is still decoding.
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_USER_UPDATE, IntPtr.Zero, IntPtr.Zero);
        }

        // Heavy work — decoding a 100-frame GIF, even downsampled, is multi-hundred-ms work and
        // would freeze the caller thread (which is whichever thread the page is invoking from).
        // Push it to the threadpool; when ready, swap in and post another render tick.
        if (pathChanged)
        {
            var pathToLoad = newPath!;
            _ = Task.Run(() =>
            {
                AnimatedImage? loaded = null;
                try { loaded = AnimatedImage.Load(pathToLoad); }
                catch (Exception ex) { _logger.LogWarning(ex, "Background image load failed: {Path}", pathToLoad); }

                lock (_stateLock)
                {
                    // The user may have switched to another image while we were loading — only
                    // swap in if our load still matches the currently-requested path.
                    if (_pendingProfile?.Type == CrosshairType.Image
                        && string.Equals(_pendingProfile.ImagePath, pathToLoad, StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedAnimated?.Dispose();
                        _cachedAnimated = loaded;
                        _cachedImagePath = pathToLoad;
                    }
                    else
                    {
                        loaded?.Dispose();
                    }
                }
                if (_hwnd != IntPtr.Zero)
                {
                    PostMessage(_hwnd, WM_USER_UPDATE, IntPtr.Zero, IntPtr.Zero);
                }
            })
            .ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    System.Diagnostics.Debug.WriteLine($"CrosshairOverlay background task failed: {t.Exception.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }
    }

    private AnimatedImage? LoadImageIfChanged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (string.Equals(_cachedImagePath, path, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return AnimatedImage.Load(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load crosshair image {Path}", path);
            return null;
        }
    }

    private void RunMessageLoop()
    {
        try
        {
            _uiThreadId = GetCurrentThreadId();
            // Per-monitor DPI awareness so window coordinates are correct on mixed-DPI multi-
            // monitor setups (otherwise the layered window can land off-centre on a non-primary
            // monitor with a different scale factor). Must run on this thread before any window
            // is created from it. Older Windows builds lack the API — swallow and continue.
            try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* pre-Win10 1607 — overlay still works, just without per-monitor DPI */ }
            EnsureClassRegistered();

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                WindowClassName,
                "RazorReaper Crosshair",
                WS_POPUP,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _logger.LogError("CreateWindowEx failed for crosshair overlay: Win32 0x{Err:X}", Marshal.GetLastWin32Error());
                _started.Set();
                return;
            }

            // Don't ShowWindow yet — first paint happens on first WM_USER_UPDATE.
            // Animation tick runs at ~30Hz when something is animated — plenty smooth for a
            // crosshair, and halves the GDI+ render load vs 60Hz.
            var timerId = (IntPtr)1;
            SetTimer(_hwnd, timerId, 33, IntPtr.Zero);

            RegisterTrayIcon();

            _started.Set();

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Crosshair overlay message loop crashed");
            _started.Set();
        }
    }

    private void EnsureClassRegistered()
    {
        if (_classRegistered) return;
        _wndProc = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = IntPtr.Zero
        };

        var atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // ERROR_CLASS_ALREADY_EXISTS is fine — happens if Start() is somehow called twice.
            if (err != 1410)
            {
                _logger.LogError("RegisterClassEx failed: 0x{Err:X}", err);
            }
        }
        _classRegistered = true;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_USER_UPDATE:
                Render();
                return IntPtr.Zero;
            case WM_TIMER:
                MaybeAnimate();
                return IntPtr.Zero;
            case WM_HOTKEY:
                if (ShouldDebounceHotkey()) return IntPtr.Zero;
                try { _onHotkeyToggle(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Crosshair hotkey callback threw"); }
                return IntPtr.Zero;
            case WM_USER_HOTKEY_REGISTER:
                DoHotkeyRegister((int)wParam, (uint)lParam);
                return IntPtr.Zero;
            case WM_USER_HOTKEY_UNREGISTER:
                DoHotkeyUnregister();
                return IntPtr.Zero;
            case WM_USER_TRAY:
                HandleTrayMessage(lParam);
                return IntPtr.Zero;
            case WM_COMMAND:
                HandleMenuCommand(LowWord(wParam));
                return IntPtr.Zero;
            case WM_DESTROY:
                DoHotkeyUnregister();
                UnregisterTrayIcon();
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static int LowWord(IntPtr p) => unchecked((short)(long)p);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }

        _uiThread?.Join(TimeSpan.FromSeconds(2));
        _cachedAnimated?.Dispose();
        _renderBuffer?.Dispose();
        _started.Dispose();
    }
}
