using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>
/// One attached display, in virtual-desktop pixels. <c>DeviceName</c> is the Windows device path
/// (<c>\\.\DISPLAY2</c>), which is the one identifier DXGI outputs and GDI monitor info agree on —
/// the friendly name Windows Settings shows is not exposed by either.
/// </summary>
public sealed record AttachedDisplay(string DeviceName, Rectangle Bounds, bool IsPrimary)
{
    /// <summary>The number in "Monitor 2", or 0 when the device name says nothing.</summary>
    public int Index => MonitorSelection.IndexFromDeviceName(DeviceName);

    /// <summary>"2560x1440" — the same shape as the calibration store's resolution key.</summary>
    public string ResolutionKey => $"{Bounds.Width}x{Bounds.Height}";
}

/// <summary>
/// Which display a capture belongs to. Pure, so it can be tested against a made-up display list:
/// the alternative is a test that only passes on the machine it was written on.
///
/// Desktop Duplication was pinned to DXGI output 0 for its whole life, which is the primary
/// monitor and not necessarily the one ARK is on. With the game on the second screen every grab
/// described the wrong screen: the calibrated region fell outside the duplicated output, the
/// capture was refused, and the GDI fallback — which cannot see a fullscreen game at all — took
/// over without saying so.
/// </summary>
public static class MonitorSelection
{
    /// <summary>
    /// The display <paramref name="target"/> belongs to: the one it overlaps most, the primary
    /// when it overlaps none (a window dragged off-screen, or an empty rectangle because ARK is
    /// not running), and null only when there are no displays at all.
    ///
    /// Overlap area rather than the window's centre: a game window straddling two monitors is
    /// scanned out by whichever one holds most of it, and that is also the one being looked at.
    /// </summary>
    public static AttachedDisplay? Choose(IReadOnlyList<AttachedDisplay> monitors, Rectangle target)
    {
        if (monitors is null || monitors.Count == 0) return null;

        AttachedDisplay? best = null;
        long bestArea = 0;

        if (target.Width > 0 && target.Height > 0)
        {
            foreach (var monitor in monitors)
            {
                var overlap = Rectangle.Intersect(monitor.Bounds, target);
                if (overlap.Width <= 0 || overlap.Height <= 0) continue;

                var area = (long)overlap.Width * overlap.Height;
                if (area <= bestArea) continue;
                bestArea = area;
                best = monitor;
            }
        }

        return best ?? Primary(monitors);
    }

    /// <summary>The primary display, or the first one when nothing claims to be primary.</summary>
    public static AttachedDisplay? Primary(IReadOnlyList<AttachedDisplay> monitors)
    {
        if (monitors is null || monitors.Count == 0) return null;
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>
    /// The trailing number of a device name (<c>\\.\DISPLAY2</c> gives 2), or 0 when there is
    /// none. This is what the user is shown, because a device path in a sentence about a HUD
    /// region is not a sentence anybody reads.
    /// </summary>
    public static int IndexFromDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return 0;

        var end = deviceName.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(deviceName[start - 1])) start--;
        if (start == end) return 0;

        return int.TryParse(deviceName.AsSpan(start, end - start), out var index) ? index : 0;
    }
}

/// <summary>
/// Where ARK is, in monitors rather than in windows. Everything that captures or calibrates the
/// game's HUD needs the same answer, and getting it wrong is invisible: the numbers all look
/// plausible, they just describe another screen.
/// </summary>
public interface IGameDisplayService
{
    /// <summary>Every attached display, in virtual-desktop pixels.</summary>
    IReadOnlyList<AttachedDisplay> Monitors { get; }

    /// <summary>
    /// The display ARK is on, or the primary one when the game is not running (or has no window
    /// yet). Null only when Windows reports no displays at all.
    /// </summary>
    AttachedDisplay? GameMonitor { get; }

    /// <summary>ARK's window rectangle in virtual-desktop pixels, or empty when there is none.</summary>
    Rectangle GameWindowBounds { get; }
}

/// <summary>Default <see cref="IGameDisplayService"/> implementation.</summary>
public sealed class GameDisplayService : IGameDisplayService
{
    private readonly IForegroundGate _foreground;
    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly ILogger<GameDisplayService> _logger;

    private readonly object _gate = new();
    private Rectangle _cachedBounds = Rectangle.Empty;
    private long _cachedAt = long.MinValue;

    /// <summary>
    /// Long enough that a 10 ms scan loop never enumerates processes, short enough that dragging
    /// the game to the other monitor is followed before the next calibration check.
    /// </summary>
    private const int CacheMs = 750;

    public GameDisplayService(
        IForegroundGate foreground,
        IProcessService process,
        IOptions<AppConfiguration> config,
        ILogger<GameDisplayService> logger)
    {
        _foreground = foreground;
        _process = process;
        _config = config;
        _logger = logger;
    }

    public IReadOnlyList<AttachedDisplay> Monitors => Enumerate();

    public AttachedDisplay? GameMonitor => MonitorSelection.Choose(Enumerate(), GameWindowBounds);

    public Rectangle GameWindowBounds
    {
        get
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;

                // The sentinel is compared, never subtracted from: `now - long.MinValue`
                // overflows negative and the refresh never runs. ForegroundGate carries the
                // same note, because that exact arithmetic shut every script's gate once.
                if (_cachedAt != long.MinValue && now - _cachedAt <= CacheMs) return _cachedBounds;

                _cachedAt = now;
                _cachedBounds = ReadGameWindowBounds();
                return _cachedBounds;
            }
        }
    }

    private Rectangle ReadGameWindowBounds()
    {
        try
        {
            // The cheap path first: when ARK has focus its window is the foreground one, and the
            // gate already knows that without enumerating anything.
            if (_foreground.IsGameForeground())
            {
                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && TryGetWindowBounds(fg, out var focused)) return focused;
            }

            var processes = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
            try
            {
                foreach (var process in processes)
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero && TryGetWindowBounds(hwnd, out var bounds)) return bounds;
                }
            }
            finally
            {
                foreach (var process in processes) process?.Dispose();
            }
        }
        catch (Exception ex)
        {
            // BattlEye strips handle rights while the game sits on a protected server, so a
            // window lookup coming back empty is routine rather than exceptional.
            _logger.LogDebug(ex, "Could not read ARK's window bounds — falling back to the primary display");
        }

        return Rectangle.Empty;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (!GetWindowRect(hwnd, out var rect)) return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        bounds = new Rectangle(rect.Left, rect.Top, width, height);
        return true;
    }

    /// <summary>
    /// Enumerated on every call rather than cached: unplugging a monitor, changing a resolution
    /// and rotating a display all move these rectangles, and a stale list is exactly the
    /// wrong-screen bug this type exists to stop.
    /// </summary>
    private IReadOnlyList<AttachedDisplay> Enumerate()
    {
        var monitors = new List<AttachedDisplay>();

        // Physical pixels, not the ones a scaled display reports to a DPI-unaware thread:
        // capture regions and calibration points are physical, so these have to be too.
        IntPtr oldCtx = IntPtr.Zero;
        var ctxSet = false;
        try
        {
            oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            ctxSet = oldCtx != IntPtr.Zero;
        }
        catch { /* pre-Win10 1607 — the process default context still gives usable numbers */ }

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                try
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        monitors.Add(new AttachedDisplay(
                            info.szDevice ?? string.Empty,
                            Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                            (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                    }
                }
                catch (Exception ex)
                {
                    // An exception crossing back into Win32 from a callback tears the process down.
                    _logger.LogDebug(ex, "Skipping a display that could not be described");
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Display enumeration failed — capture falls back to the primary screen");
        }
        finally
        {
            if (ctxSet)
            {
                try { SetThreadDpiAwarenessContext(oldCtx); }
                catch { /* restore is best-effort */ }
            }
        }

        return monitors;
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    private const int MONITORINFOF_PRIMARY = 1;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVERECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public NATIVERECT rcMonitor;
        public NATIVERECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcClip, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NATIVERECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
