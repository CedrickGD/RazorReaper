using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Extensions.Logging;
// Disambiguate from Microsoft.Maui.* implicit usings.
using Bitmap = System.Drawing.Bitmap;
using Brush = System.Drawing.SolidBrush;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Graphics = System.Drawing.Graphics;
using GraphicsUnit = System.Drawing.GraphicsUnit;
using Pen = System.Drawing.Pen;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using StringAlignment = System.Drawing.StringAlignment;
using StringFormat = System.Drawing.StringFormat;
using StringFormatFlags = System.Drawing.StringFormatFlags;
using StringTrimming = System.Drawing.StringTrimming;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// The in-game message banner (<see cref="GameBannerNotifier"/>). A second layered window hosted
/// on the crosshair's STA thread, so it shares the window class, the message loop and the
/// UpdateLayeredWindow path instead of starting a stack of its own. Same styles as the crosshair:
/// topmost, never activated, click-through, no taskbar entry — it must never take focus from ARK.
/// </summary>
internal sealed partial class CrosshairOverlayWindow : IGameBanner
{
    private static readonly Color BannerAccent = Color.FromArgb(139, 92, 246);

    private IntPtr _bannerHwnd = IntPtr.Zero;
    private (string Message, Rectangle Area, int DurationMs)? _pendingBanner;
    private bool _bannerFailureLogged;

    public void ShowBanner(string message, Rectangle area, int durationMs)
    {
        if (_hwnd == IntPtr.Zero)
        {
            LogBannerFailureOnce(null);
            return;
        }
        lock (_stateLock) _pendingBanner = (message, area, durationMs);
        PostMessage(_hwnd, WM_USER_BANNER, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Overlay thread only. A newer banner simply overwrites the one on screen.</summary>
    private void RenderBanner()
    {
        (string Message, Rectangle Area, int DurationMs)? pending;
        lock (_stateLock)
        {
            pending = _pendingBanner;
            _pendingBanner = null;
        }
        if (pending is not { } banner) return;

        try
        {
            if (_bannerHwnd == IntPtr.Zero)
            {
                _bannerHwnd = CreateWindowEx(
                    WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    WindowClassName, "RazorReaper Banner", WS_POPUP,
                    0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (_bannerHwnd == IntPtr.Zero)
                {
                    LogBannerFailureOnce(null);
                    return;
                }
            }

            var scale = Math.Clamp(banner.Area.Height / 1080f, 1f, 2.5f);
            using var bmp = DrawBanner(banner.Message, banner.Area.Width, scale);
            PushBitmapToWindow(
                _bannerHwnd, bmp,
                banner.Area.X + (banner.Area.Width - bmp.Width) / 2,
                banner.Area.Y + (int)(24 * scale));
            ShowWindow(_bannerHwnd, SW_SHOWNOACTIVATE);
            // ARK itself may sit in the topmost band — lift the banner above it without activating.
            SetWindowPos(_bannerHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            // Re-arming the same timer id restarts the countdown for a replacing message.
            SetTimer(_hwnd, BannerTimerId, (uint)Math.Max(1000, banner.DurationMs), IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LogBannerFailureOnce(ex);
        }
    }

    private void HideBanner()
    {
        KillTimer(_hwnd, BannerTimerId);
        if (_bannerHwnd != IntPtr.Zero) ShowWindow(_bannerHwnd, SW_HIDE);
    }

    private void LogBannerFailureOnce(Exception? ex)
    {
        if (_bannerFailureLogged) return;
        _bannerFailureLogged = true;
        _logger.LogWarning(ex, "In-game banner unavailable (Win32 0x{Err:X}); messages stay in the app only",
            System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    /// <summary>Dark plate, accent border, the message centred on at most two lines.</summary>
    private static Bitmap DrawBanner(string message, int areaWidth, float scale)
    {
        var width = (int)Math.Max(120, Math.Min(640 * scale, areaWidth - 32 * scale));
        var pad = 14 * scale;
        var textWidth = width - 2 * pad;

        using var font = new Font("Segoe UI", 16 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisWord,
            FormatFlags = StringFormatFlags.LineLimit
        };

        float lineHeight, measured;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            lineHeight = font.GetHeight(g);
            measured = g.MeasureString(message, font, (int)textWidth, format).Height;
        }
        var textHeight = (measured > lineHeight * 1.5f ? 2 : 1) * lineHeight + 1;
        var height = (int)Math.Ceiling(textHeight + 2 * pad);

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var fill = new Brush(Color.FromArgb(235, 18, 16, 26)))
        using (var border = new Pen(BannerAccent, Math.Max(1f, 2 * scale)))
        using (var text = new Brush(Color.FromArgb(240, 240, 245)))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.FillRectangle(fill, 0, 0, width, height);
            var inset = border.Width / 2;
            g.DrawRectangle(border, inset, inset, width - border.Width, height - border.Width);
            g.DrawString(message, font, text, new RectangleF(pad, pad, textWidth, textHeight), format);
        }
        return bmp;
    }
}
