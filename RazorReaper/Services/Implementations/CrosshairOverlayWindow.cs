using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
/// pushes profile updates via Update(...) — cross-thread coordination is done by capturing the
/// next state under a lock and waking the UI thread with PostMessage(WM_USER+update).
/// </summary>
internal sealed class CrosshairOverlayWindow : IDisposable
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
        _started.Wait();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        lock (_stateLock)
        {
            if (_monitors.Length == 0)
            {
                _monitors = EnumerateMonitors();
            }
            return _monitors;
        }
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
            Task.Run(() =>
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
            });
        }
    }

    public void RegisterHotkey(int virtualKey, bool ctrl, bool alt, bool shift)
    {
        if (_hwnd == IntPtr.Zero) return;
        // Run on UI thread so RegisterHotKey is owned by the same thread that pumps the message loop.
        PostMessage(_hwnd, WM_USER_HOTKEY_REGISTER, (IntPtr)virtualKey, (IntPtr)(BuildModFlags(ctrl, alt, shift)));
    }

    public void UnregisterHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        PostMessage(_hwnd, WM_USER_HOTKEY_UNREGISTER, IntPtr.Zero, IntPtr.Zero);
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

    // ─── Tray icon ─────────────────────────────────────────────────────────────

    private void RegisterTrayIcon()
    {
        if (_trayRegistered) return;
        try
        {
            _trayHIcon = LoadTrayIcon();

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayIconUID,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_USER_TRAY,
                hIcon = _trayHIcon,
                szTip = "Razor Reaper — crosshair overlay"
            };

            if (!Shell_NotifyIcon(NIM_ADD, ref nid))
            {
                _logger.LogWarning("Shell_NotifyIcon(NIM_ADD) failed: 0x{Err:X}", Marshal.GetLastWin32Error());
                return;
            }

            // NOTIFYICON_VERSION_4 gives us packed lParam (mouse_msg | icon_id) and packed wParam (x|y).
            nid.uTimeoutOrVersion = 4;
            Shell_NotifyIcon(NIM_SETVERSION, ref nid);

            _trayRegistered = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register tray icon");
        }
    }

    private void UnregisterTrayIcon()
    {
        if (!_trayRegistered) return;
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconUID,
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        if (_trayHIcon != IntPtr.Zero)
        {
            DestroyIcon(_trayHIcon);
            _trayHIcon = IntPtr.Zero;
        }
        _trayRegistered = false;
    }

    private static IntPtr LoadTrayIcon()
    {
        // Prefer ExtractIconEx on the running .exe (gives us a 16x16 tray-sized icon for free).
        // Falls back to LoadIcon(IDI_APPLICATION) if the exe path can't be resolved.
        try
        {
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                ExtractIconEx(exe, 0, out IntPtr _, out IntPtr smallIcon, 1);
                if (smallIcon != IntPtr.Zero) return smallIcon;
            }
        }
        catch { /* fall through */ }
        return LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
    }

    private void HandleTrayMessage(IntPtr lParam)
    {
        // With NOTIFYICON_VERSION_4 the low word of lParam is the mouse-event message.
        var mouseMsg = (uint)LowWord(lParam);
        switch (mouseMsg)
        {
            case WM_LBUTTONDBLCLK:
                SafeInvoke(_onTrayShowApp, "tray double-click → show app");
                break;
            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                ShowTrayMenu();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var overlayActive = false;
        try { overlayActive = _isOverlayActive(); } catch { }

        AppendMenu(menu, MF_STRING | (overlayActive ? MF_CHECKED : 0), CmdToggleOverlay, overlayActive ? "Hide overlay" : "Show overlay");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdOpenApp, "Open Razor Reaper");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdQuit, "Quit");

        GetCursorPos(out POINT pt);
        // Windows quirk — TrackPopupMenu won't dismiss correctly without first focusing the owner.
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
    }

    private void HandleMenuCommand(int id)
    {
        switch (id)
        {
            case CmdToggleOverlay:
                SafeInvoke(_onHotkeyToggle, "tray toggle overlay");
                break;
            case CmdOpenApp:
                SafeInvoke(_onTrayShowApp, "tray open app");
                break;
            case CmdQuit:
                SafeInvoke(_onTrayQuit, "tray quit");
                break;
        }
    }

    private void SafeInvoke(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Tray action threw: {What}", what); }
    }

    private static int LowWord(IntPtr p) => unchecked((short)(long)p);

    private void DoHotkeyRegister(int vk, uint mods)
    {
        DoHotkeyUnregister();
        if (vk == 0) return;
        _hotkeyId = 0xC051; // arbitrary, just needs to be unique to this window
        if (!RegisterHotKey(_hwnd, (int)_hotkeyId, mods, (uint)vk))
        {
            _logger.LogWarning("RegisterHotKey failed: vk=0x{Vk:X} mods=0x{Mods:X} err=0x{Err:X}", vk, mods, Marshal.GetLastWin32Error());
            _hotkeyId = 0;
            return;
        }
        _registeredHotkeyVk = vk;
        _registeredHotkeyMods = mods;
    }

    private void DoHotkeyUnregister()
    {
        if (_hotkeyId == 0) return;
        UnregisterHotKey(_hwnd, (int)_hotkeyId);
        _hotkeyId = 0;
        _registeredHotkeyVk = 0;
        _registeredHotkeyMods = 0;
    }

    private void MaybeAnimate()
    {
        // Cheapest check — if nothing animated and not visible, skip the redraw.
        CrosshairProfile? p;
        bool visible;
        bool hasAnimatedImage;
        lock (_stateLock)
        {
            p = _pendingProfile;
            visible = _visible;
            hasAnimatedImage = _cachedAnimated?.IsAnimated == true && p?.Type == CrosshairType.Image;
        }
        if (!visible || p == null) return;
        if (p.Animation == CrosshairAnimation.None && !p.Rainbow && !hasAnimatedImage) return;
        Render();
    }

    private void Render()
    {
        CrosshairProfile? profile;
        AnimatedImage? animated;
        bool visible;
        lock (_stateLock)
        {
            profile = _pendingProfile;
            animated = _cachedAnimated;
            visible = _visible;
        }

        if (profile == null || !visible)
        {
            ShowWindow(_hwnd, SW_HIDE);
            return;
        }

        // Resolve monitor rect each render — handles display config changes for free.
        var monitor = ResolveMonitor(profile.MonitorDeviceName);

        var phase = ComputePhase(profile);
        var imageFrame = animated?.FrameAt(_animationStart);
        // Never let the overlay exceed the active monitor's smaller dimension — that's the
        // physical ceiling the user actually wants ("not bigger than one monitor").
        var monitorBound = Math.Min(monitor.Width, monitor.Height);
        var canvasSize = CrosshairRenderer.ComputeCanvasSize(profile, imageFrame, monitorBound);

        // Pool: re-allocate only when the canvas size changes.
        if (_renderBuffer == null || _renderBufferSize != canvasSize)
        {
            _renderBuffer?.Dispose();
            _renderBuffer = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppPArgb);
            _renderBufferSize = canvasSize;
        }
        var bmp = _renderBuffer;
        CrosshairRenderer.RenderInto(bmp, profile, phase, imageFrame);

        var screenX = monitor.X + monitor.Width / 2 - bmp.Width / 2 + profile.OffsetX;
        var screenY = monitor.Y + monitor.Height / 2 - bmp.Height / 2 + profile.OffsetY;

        PushBitmapToWindow(bmp, screenX, screenY);

        if (!IsWindowVisible(_hwnd))
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }
    }

    private MonitorInfo ResolveMonitor(string deviceName)
    {
        var monitors = EnumerateMonitors();
        lock (_stateLock) _monitors = monitors;

        if (!string.IsNullOrEmpty(deviceName))
        {
            var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        return primary ?? monitors.FirstOrDefault()
            ?? new MonitorInfo("\\\\.\\DISPLAY1", "Primary", 0, 0, 1920, 1080, true);
    }

    private double ComputePhase(CrosshairProfile profile)
    {
        // Period scales with AnimationSpeed: 10 = 0.5s/cycle, 1 = 5s/cycle.
        var speed = Math.Clamp(profile.AnimationSpeed, 1, 10);
        var period = 5.0 / speed;
        if (profile.Animation == CrosshairAnimation.None && !profile.Rainbow) return 0.0;

        var seconds = (DateTime.UtcNow - _animationStart).TotalSeconds;
        return (seconds / period) % 1.0;
    }

    private void PushBitmapToWindow(Bitmap bmp, int screenX, int screenY)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var pointSrc = new POINT { X = 0, Y = 0 };
            var pointDst = new POINT { X = screenX, Y = screenY };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(
                _hwnd, screenDc, ref pointDst, ref size,
                memDc, ref pointSrc, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static MonitorInfo[] EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>()
            };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var name = info.szDevice ?? "";
                list.Add(new MonitorInfo(
                    DeviceName: name,
                    FriendlyName: $"{name.TrimStart('\\', '.')}  ({info.rcMonitor.Right - info.rcMonitor.Left}×{info.rcMonitor.Bottom - info.rcMonitor.Top})",
                    X: info.rcMonitor.Left,
                    Y: info.rcMonitor.Top,
                    Width: info.rcMonitor.Right - info.rcMonitor.Left,
                    Height: info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list.ToArray();
    }

    private static uint BuildModFlags(bool ctrl, bool alt, bool shift)
    {
        uint mods = MOD_NOREPEAT;
        if (ctrl) mods |= MOD_CONTROL;
        if (alt) mods |= MOD_ALT;
        if (shift) mods |= MOD_SHIFT;
        return mods;
    }

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

    // ─── Win32 interop ────────────────────────────────────────────────────────────

    private const int WM_DESTROY = 0x0002;
    private const int WM_TIMER = 0x0113;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_COMMAND = 0x0111;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_USER_UPDATE = 0x0400 + 1;
    private const int WM_USER_HOTKEY_REGISTER = 0x0400 + 2;
    private const int WM_USER_HOTKEY_UNREGISTER = 0x0400 + 3;
    private const int WM_USER_TRAY = 0x0400 + 10;

    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIM_SETVERSION = 0x04;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_CHECKED = 0x0008;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int CmdToggleOverlay = 1001;
    private const int CmdOpenApp = 1002;
    private const int CmdQuit = 1003;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x00000002;

    private const uint MOD_NOREPEAT = 0x4000;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private const uint MONITORINFOF_PRIMARY = 1;

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
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

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

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
