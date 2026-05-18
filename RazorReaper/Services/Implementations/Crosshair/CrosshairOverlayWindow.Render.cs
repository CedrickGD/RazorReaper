using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RazorReaper.Models;
using Color = System.Drawing.Color;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Frame rendering for the overlay: deciding when to re-render (animation tick), drawing into the
/// pooled bitmap, pushing it to the layered window, and resolving which monitor to display on.
/// Owned by the overlay UI thread — these methods are only safe to call from WndProc dispatch.
/// </summary>
internal sealed partial class CrosshairOverlayWindow
{
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
}
