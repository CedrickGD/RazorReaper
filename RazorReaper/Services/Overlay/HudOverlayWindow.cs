using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
// Disambiguate from Microsoft.Maui.* implicit usings (Color, Font, Point, Size, ...).
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Graphics = System.Drawing.Graphics;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;
using SizeF = System.Drawing.SizeF;
using StringFormat = System.Drawing.StringFormat;

namespace RazorReaper.Services.Overlay;

/// <summary>
/// Dedicated Win32 layered window that draws the in-game HUD. Mirrors the proven crosshair overlay
/// construction (own STA thread + message pump, WS_EX_LAYERED|TRANSPARENT|TOPMOST|TOOLWINDOW|
/// NOACTIVATE, per-pixel alpha via UpdateLayeredWindow) but is a completely separate window class —
/// it never touches the crosshair code.
///
/// The window spans the whole target monitor so the module panel and the notifier alert stack can
/// live in different corners of one surface. Everything not drawn is alpha-0, and per-pixel-alpha
/// layered windows are click-through on transparent pixels; on top of that the window normally
/// carries WS_EX_TRANSPARENT so even drawn pixels never eat input. In move mode WS_EX_TRANSPARENT
/// is dropped: the panel pixels become hit-testable and draggable (manual capture-based drag — the
/// window itself never moves), with a faint dashed outline as the affordance.
///
/// Threading: public members marshal state under a lock and wake the overlay thread with
/// PostMessage(WM_USER_*). All rendering and Win32 calls happen on the overlay thread.
/// </summary>
internal sealed class HudOverlayWindow : IDisposable
{
    private readonly ILogger _logger;
    /// <summary>Fired after a move-mode drag ends: (monitor-relative panel X, Y).</summary>
    private readonly Action<int, int> _onPanelMoved;

    private Thread? _uiThread;
    private readonly ManualResetEventSlim _started = new(false);
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _disposed;

    // ─── Cross-thread state (guarded by _stateLock, applied on the overlay thread) ─────────
    private readonly object _stateLock = new();
    private HudSnapshot? _snapshot;
    private bool _visible;
    private bool _moveMode;
    private HudAnchor _anchor = HudAnchor.TopRight;
    private int _offsetX = 16, _offsetY = 16;
    private int _customX, _customY;
    private double _opacity = 0.95;
    private double _scale = 1.0;
    private string _monitorDevice = "";
    private Color _accent = Color.FromArgb(139, 92, 246);

    // ─── Overlay-thread-only state ─────────────────────────────────────────────────────────
    private Bitmap? _buffer;
    private int _bufW, _bufH;
    private RectangleF _panelRect = RectangleF.Empty; // monitor-relative, for drag hit-testing
    private int _monX, _monY, _monW, _monH;           // last resolved monitor rect
    private bool _dragging;
    private int _dragStartCursorX, _dragStartCursorY;
    private float _dragStartPanelX, _dragStartPanelY;

    private WndProcDelegate? _wndProc;
    private const string WindowClassName = "RazorReaperHudOverlay";
    private bool _classRegistered;

    public HudOverlayWindow(ILogger logger, Action<int, int> onPanelMoved)
    {
        _logger = logger;
        _onPanelMoved = onPanelMoved;
    }

    // ─── Lifecycle ─────────────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_uiThread != null) return;
        _uiThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "HUD Overlay UI"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        // Bounded wait until the message loop is ready (same pattern as the crosshair overlay).
        _started.Wait(TimeSpan.FromSeconds(3));
    }

    public void Show()
    {
        lock (_stateLock) _visible = true;
        PostUpdate();
    }

    public void Hide()
    {
        lock (_stateLock) _visible = false;
        PostUpdate();
    }

    /// <summary>Replace the frame data and repaint. Called ~2×/sec by the service.</summary>
    public void Render(HudSnapshot snapshot)
    {
        lock (_stateLock) _snapshot = snapshot;
        PostUpdate();
    }

    public void SetMoveMode(bool enabled)
    {
        lock (_stateLock) _moveMode = enabled;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_USER_STYLE, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetOpacity(double opacity)
    {
        lock (_stateLock) _opacity = Math.Clamp(opacity, 0.2, 1.0);
        PostUpdate();
    }

    public void SetScale(double scale)
    {
        lock (_stateLock) _scale = Math.Clamp(scale, 0.5, 2.0);
        PostUpdate();
    }

    public void SetAnchor(HudAnchor anchor, int offsetX, int offsetY, int customX, int customY)
    {
        lock (_stateLock)
        {
            _anchor = anchor;
            _offsetX = offsetX;
            _offsetY = offsetY;
            _customX = customX;
            _customY = customY;
        }
        PostUpdate();
    }

    public void SetMonitor(string deviceName)
    {
        lock (_stateLock) _monitorDevice = deviceName ?? "";
        PostUpdate();
    }

    public void SetAccent(byte r, byte g, byte b)
    {
        lock (_stateLock) _accent = Color.FromArgb(r, g, b);
        PostUpdate();
    }

    private void PostUpdate()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_USER_UPDATE, IntPtr.Zero, IntPtr.Zero);
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
        _buffer?.Dispose();
        _buffer = null;
        _started.Dispose();
    }

    // ─── Message loop / window plumbing (overlay thread) ───────────────────────────────────

    private void RunMessageLoop()
    {
        try
        {
            // Per-monitor DPI awareness so coordinates are physical pixels on mixed-DPI setups.
            try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* pre-Win10 1607 — still works, without per-monitor DPI */ }

            EnsureClassRegistered();

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                WindowClassName,
                "RazorReaper HUD",
                WS_POPUP,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _logger.LogError("CreateWindowEx failed for HUD overlay: Win32 0x{Err:X}", Marshal.GetLastWin32Error());
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
            _logger.LogError(ex, "HUD overlay message loop crashed");
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
            if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                _logger.LogError("RegisterClassEx failed for HUD overlay: 0x{Err:X}", err);
            }
        }
        _classRegistered = true;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_USER_UPDATE:
                RenderCore();
                return IntPtr.Zero;
            case WM_USER_STYLE:
                ApplyClickThroughStyle();
                return IntPtr.Zero;
            case WM_SETCURSOR:
                bool move;
                lock (_stateLock) move = _moveMode;
                if (move)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, IDC_SIZEALL));
                    return (IntPtr)1;
                }
                return DefWindowProc(hwnd, msg, wParam, lParam);
            case WM_LBUTTONDOWN:
                OnMouseDown();
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                OnMouseMove();
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                OnMouseUp();
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                _dragging = false;
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>Toggle WS_EX_TRANSPARENT to match move mode, then repaint (outline on/off).</summary>
    private void ApplyClickThroughStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool move;
        lock (_stateLock) move = _moveMode;

        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        if (move) ex &= ~(long)WS_EX_TRANSPARENT;
        else ex |= WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        if (!move) _dragging = false;
        RenderCore();
    }

    // ─── Move-mode drag (overlay thread) ───────────────────────────────────────────────────

    private void OnMouseDown()
    {
        bool move;
        lock (_stateLock) move = _moveMode;
        if (!move || _panelRect.IsEmpty) return;
        if (!GetCursorPos(out POINT cur)) return;

        var relX = cur.X - _monX;
        var relY = cur.Y - _monY;
        var hit = _panelRect;
        hit.Inflate(6f, 6f);
        if (!hit.Contains(relX, relY)) return;

        _dragging = true;
        _dragStartCursorX = cur.X;
        _dragStartCursorY = cur.Y;
        _dragStartPanelX = _panelRect.X;
        _dragStartPanelY = _panelRect.Y;
        SetCapture(_hwnd);
    }

    private void OnMouseMove()
    {
        if (!_dragging) return;
        if (!GetCursorPos(out POINT cur)) return;

        var newX = _dragStartPanelX + (cur.X - _dragStartCursorX);
        var newY = _dragStartPanelY + (cur.Y - _dragStartCursorY);
        newX = Math.Clamp(newX, 0f, Math.Max(0f, _monW - _panelRect.Width));
        newY = Math.Clamp(newY, 0f, Math.Max(0f, _monH - _panelRect.Height));

        lock (_stateLock)
        {
            _anchor = HudAnchor.Custom;
            _customX = (int)newX;
            _customY = (int)newY;
        }
        RenderCore();
    }

    private void OnMouseUp()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseCapture();

        int x, y;
        lock (_stateLock)
        {
            x = _customX;
            y = _customY;
        }
        try { _onPanelMoved(x, y); }
        catch (Exception ex) { _logger.LogWarning(ex, "HUD panel-moved callback threw"); }
    }

    // ─── Rendering (overlay thread) ────────────────────────────────────────────────────────

    private void RenderCore()
    {
        HudSnapshot? snap;
        bool visible, moveMode;
        HudAnchor anchor;
        int offX, offY, custX, custY;
        double opacity, scale;
        string device;
        Color accent;
        lock (_stateLock)
        {
            snap = _snapshot;
            visible = _visible;
            moveMode = _moveMode;
            anchor = _anchor;
            offX = _offsetX; offY = _offsetY;
            custX = _customX; custY = _customY;
            opacity = _opacity;
            scale = _scale;
            device = _monitorDevice;
            accent = _accent;
        }

        if (_hwnd == IntPtr.Zero) return;
        if (!visible || snap == null)
        {
            ShowWindow(_hwnd, SW_HIDE);
            return;
        }

        var mon = ResolveMonitor(device);
        _monX = mon.X; _monY = mon.Y; _monW = mon.Width; _monH = mon.Height;

        // Pooled full-monitor canvas; realloc only when the monitor size changes.
        if (_buffer == null || _bufW != mon.Width || _bufH != mon.Height)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppPArgb);
            _bufW = mon.Width;
            _bufH = mon.Height;
        }

        // Effective scale = user scale × monitor DPI factor (physical pixels, per-monitor aware).
        var s = (float)(scale * GetDpiFactor());

        using (var g = Graphics.FromImage(_buffer))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            DrawHud(g, snap, mon, s, anchor, offX, offY, custX, custY, accent, moveMode);
        }

        PushToWindow(_buffer, mon.X, mon.Y, (byte)Math.Clamp((int)(opacity * 255), 51, 255));

        if (!IsWindowVisible(_hwnd))
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }
    }

    private double GetDpiFactor()
    {
        try
        {
            var dpi = GetDpiForWindow(_hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch (EntryPointNotFoundException) { /* pre-Win10 1607 */ }
        return 1.0;
    }

    private void DrawHud(
        Graphics g, HudSnapshot snap, MonitorInfo mon, float s,
        HudAnchor anchor, int offX, int offY, int custX, int custY,
        Color accent, bool moveMode)
    {
        var modules = snap.Modules
            .Where(m => m.Enabled && m.Id != HudModuleKind.Notifier)
            .OrderBy(m => m.Order)
            .ToList();
        var alertsEnabled = snap.Modules.Any(m => m.Id == HudModuleKind.Notifier && m.Enabled);

        _panelRect = RectangleF.Empty;
        if (modules.Count > 0)
        {
            _panelRect = snap.Compact
                ? DrawCompactPanel(g, snap, modules, mon, s, anchor, offX, offY, custX, custY, accent)
                : DrawFullPanel(g, snap, modules, mon, s, anchor, offX, offY, custX, custY, accent);
        }

        if (moveMode && !_panelRect.IsEmpty)
        {
            DrawMoveOutline(g, _panelRect, s, accent);
        }

        if (alertsEnabled && snap.Alerts.Count > 0)
        {
            DrawAlerts(g, snap, mon, s, anchor, accent, _panelRect);
        }
    }

    // ─── Panel styling constants (shared by panel + alerts) ────────────────────────────────

    private static readonly Color PanelBg = Color.FromArgb(206, 17, 17, 22);
    private static readonly Color PanelShadow = Color.FromArgb(60, 0, 0, 0);
    private static readonly Color PanelBorder = Color.FromArgb(28, 255, 255, 255);
    private static readonly Color TextValue = Color.FromArgb(235, 255, 255, 255);
    private static readonly Color TextLabel = Color.FromArgb(150, 255, 255, 255);
    private static readonly Color TextMuted = Color.FromArgb(160, 255, 255, 255);
    private static readonly Color StatusGreen = Color.FromArgb(34, 197, 94);
    private static readonly Color StatusOrange = Color.FromArgb(249, 115, 22);
    private static readonly Color StatusRed = Color.FromArgb(239, 68, 68);

    private static StringFormat MakeLineFormat() => new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter
    };

    private RectangleF PlacePanel(
        SizeF size, MonitorInfo mon, float s,
        HudAnchor anchor, int offX, int offY, int custX, int custY)
    {
        float mx = offX * s, my = offY * s;
        float x, y;
        switch (anchor)
        {
            case HudAnchor.TopLeft: x = mx; y = my; break;
            case HudAnchor.TopRight: x = mon.Width - mx - size.Width; y = my; break;
            case HudAnchor.BottomLeft: x = mx; y = mon.Height - my - size.Height; break;
            case HudAnchor.BottomRight: x = mon.Width - mx - size.Width; y = mon.Height - my - size.Height; break;
            default: x = custX; y = custY; break;
        }
        x = Math.Clamp(x, 0f, Math.Max(0f, mon.Width - size.Width));
        y = Math.Clamp(y, 0f, Math.Max(0f, mon.Height - size.Height));
        return new RectangleF(x, y, size.Width, size.Height);
    }

    private void DrawPanelChrome(Graphics g, RectangleF rect, float s, Color accent)
    {
        var radius = 10f * s;

        // Soft shadow for legibility over bright scenes.
        var shadowRect = rect;
        shadowRect.Offset(0f, 2f * s);
        shadowRect.Inflate(1.5f * s, 1.5f * s);
        using (var shadowPath = RoundedPath(shadowRect, radius + 1.5f * s))
        using (var shadowBrush = new SolidBrush(PanelShadow))
        {
            g.FillPath(shadowBrush, shadowPath);
        }

        using (var path = RoundedPath(rect, radius))
        {
            using (var bg = new SolidBrush(PanelBg)) g.FillPath(bg, path);
            using (var border = new Pen(PanelBorder, 1f)) g.DrawPath(border, path);
        }

        // Thin accent left edge, inset past the corner rounding.
        using (var accentBrush = new SolidBrush(Color.FromArgb(210, accent)))
        {
            g.FillRectangle(accentBrush, rect.X, rect.Y + radius * 0.7f, 3f, rect.Height - radius * 1.4f);
        }
    }

    private RectangleF DrawFullPanel(
        Graphics g, HudSnapshot snap, List<HudModule> modules, MonitorInfo mon, float s,
        HudAnchor anchor, int offX, int offY, int custX, int custY, Color accent)
    {
        using var labelFont = new Font("Segoe UI", 10f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 15f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = MakeLineFormat();

        float padX = 14f * s, padY = 11f * s, barW = 3f;
        float labelGap = 2f * s, rowGap = 9f * s, subGap = 2f * s;
        float minW = 170f * s, maxW = 340f * s;
        float maxTextW = maxW - padX * 2f - barW;

        // Build rows: (label, value, valueColor, subSegments[(text, color)])
        var rows = new List<(string Label, string Value, Color ValueColor, (string Text, Color Color)[] Sub)>();
        foreach (var m in modules)
        {
            switch (m.Id)
            {
                case HudModuleKind.Clock:
                    rows.Add((m.Title.ToUpperInvariant(), snap.TimeText, TextValue, Array.Empty<(string, Color)>()));
                    break;
                case HudModuleKind.SessionTimer:
                    rows.Add((m.Title.ToUpperInvariant(), snap.SessionText, TextValue, Array.Empty<(string, Color)>()));
                    break;
                case HudModuleKind.ServerInfo:
                {
                    var name = string.IsNullOrWhiteSpace(snap.Server.Name) ? "No server set" : snap.Server.Name!;
                    var nameColor = string.IsNullOrWhiteSpace(snap.Server.Name) ? TextMuted : TextValue;
                    var sub = BuildServerSubSegments(snap.Server);
                    rows.Add((m.Title.ToUpperInvariant(), name, nameColor, sub));
                    break;
                }
                case HudModuleKind.ToolStatus:
                {
                    var active = !string.IsNullOrWhiteSpace(snap.ActiveTool);
                    rows.Add((m.Title.ToUpperInvariant(),
                        active ? snap.ActiveTool! : "Idle",
                        active ? Color.FromArgb(235, accent) : TextMuted,
                        Array.Empty<(string, Color)>()));
                    break;
                }
            }
        }
        if (rows.Count == 0) return RectangleF.Empty;

        // Measure.
        float labelH = labelFont.GetHeight(g);
        float valueH = valueFont.GetHeight(g);
        float subH = subFont.GetHeight(g);
        float contentW = 0f, contentH = 0f;
        foreach (var row in rows)
        {
            var lw = g.MeasureString(row.Label, labelFont, (int)maxTextW, fmt).Width;
            var vw = g.MeasureString(row.Value, valueFont, (int)maxTextW, fmt).Width;
            contentW = Math.Max(contentW, Math.Max(lw, vw));
            var rowH = labelH + labelGap + valueH;
            if (row.Sub.Length > 0)
            {
                float sw = 0f;
                foreach (var seg in row.Sub) sw += g.MeasureString(seg.Text, subFont, (int)maxTextW, fmt).Width;
                contentW = Math.Max(contentW, sw);
                rowH += subGap + subH;
            }
            contentH += rowH;
        }
        contentH += rowGap * (rows.Count - 1);

        var panelW = Math.Clamp(contentW + padX * 2f + barW, minW, maxW);
        var panelH = contentH + padY * 2f;
        var rect = PlacePanel(new SizeF(panelW, panelH), mon, s, anchor, offX, offY, custX, custY);

        DrawPanelChrome(g, rect, s, accent);

        // Draw rows.
        float tx = rect.X + barW + padX - 3f; // bar sits inside left padding visually
        float textW = rect.Width - (barW + padX * 2f - 3f) - padX;
        float y = rect.Y + padY;
        using var labelBrush = new SolidBrush(TextLabel);
        foreach (var row in rows)
        {
            g.DrawString(row.Label, labelFont, labelBrush, new RectangleF(tx, y, textW, labelH + 1f), fmt);
            y += labelH + labelGap;
            using (var valueBrush = new SolidBrush(row.ValueColor))
            {
                g.DrawString(row.Value, valueFont, valueBrush, new RectangleF(tx, y, textW, valueH + 1f), fmt);
            }
            y += valueH;
            if (row.Sub.Length > 0)
            {
                y += subGap;
                float sx = tx;
                foreach (var seg in row.Sub)
                {
                    using var segBrush = new SolidBrush(seg.Color);
                    g.DrawString(seg.Text, subFont, segBrush, new PointF(sx, y), fmt);
                    sx += g.MeasureString(seg.Text, subFont, (int)maxTextW, fmt).Width;
                    if (sx > tx + textW) break;
                }
                y += subH;
            }
            y += rowGap;
        }

        return rect;
    }

    private (string Text, Color Color)[] BuildServerSubSegments(HudServerInfo server)
    {
        var segs = new List<(string, Color)>();
        if (server.Players.HasValue)
        {
            var players = server.MaxPlayers.HasValue
                ? $"{server.Players}/{server.MaxPlayers} players"
                : $"{server.Players} players";
            segs.Add((players, TextMuted));
        }
        if (server.PingMs.HasValue)
        {
            if (segs.Count > 0) segs.Add(("  ·  ", TextMuted));
            var ping = server.PingMs.Value;
            var color = ping < 80 ? StatusGreen : ping < 150 ? StatusOrange : StatusRed;
            segs.Add(($"{ping} ms", Color.FromArgb(220, color)));
        }
        return segs.ToArray();
    }

    private RectangleF DrawCompactPanel(
        Graphics g, HudSnapshot snap, List<HudModule> modules, MonitorInfo mon, float s,
        HudAnchor anchor, int offX, int offY, int custX, int custY, Color accent)
    {
        using var font = new Font("Segoe UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = MakeLineFormat();

        var parts = new List<string>();
        foreach (var m in modules)
        {
            switch (m.Id)
            {
                case HudModuleKind.Clock:
                    parts.Add(snap.TimeText);
                    break;
                case HudModuleKind.SessionTimer:
                    parts.Add(snap.SessionText);
                    break;
                case HudModuleKind.ServerInfo:
                    if (!string.IsNullOrWhiteSpace(snap.Server.Name))
                    {
                        var text = snap.Server.Name!;
                        if (snap.Server.Players.HasValue && snap.Server.MaxPlayers.HasValue)
                            text += $" {snap.Server.Players}/{snap.Server.MaxPlayers}";
                        parts.Add(text);
                    }
                    break;
                case HudModuleKind.ToolStatus:
                    if (!string.IsNullOrWhiteSpace(snap.ActiveTool)) parts.Add(snap.ActiveTool!);
                    break;
            }
        }
        if (parts.Count == 0) return RectangleF.Empty;

        var line = string.Join("  ·  ", parts);
        float padX = 12f * s, padY = 7f * s, barW = 3f;
        float maxW = 560f * s;
        float maxTextW = maxW - padX * 2f - barW;
        var textSize = g.MeasureString(line, font, (int)maxTextW, fmt);
        var panelW = Math.Min(textSize.Width + padX * 2f + barW + 2f, maxW);
        var panelH = font.GetHeight(g) + padY * 2f;

        var rect = PlacePanel(new SizeF(panelW, panelH), mon, s, anchor, offX, offY, custX, custY);
        DrawPanelChrome(g, rect, s, accent);

        using var brush = new SolidBrush(TextValue);
        g.DrawString(line, font, brush,
            new RectangleF(rect.X + barW + padX - 3f, rect.Y + padY, rect.Width - padX * 2f - barW, panelH), fmt);

        return rect;
    }

    private void DrawMoveOutline(Graphics g, RectangleF panel, float s, Color accent)
    {
        var outline = panel;
        outline.Inflate(5f * s, 5f * s);
        using var pen = new Pen(Color.FromArgb(160, accent), 1f)
        {
            DashStyle = DashStyle.Dash
        };
        using var path = RoundedPath(outline, 12f * s);
        g.DrawPath(pen, path);
    }

    private void DrawAlerts(
        Graphics g, HudSnapshot snap, MonitorInfo mon, float s,
        HudAnchor panelAnchor, Color accent, RectangleF panelRect)
    {
        using var font = new Font("Segoe UI", 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = MakeLineFormat();

        var corner = snap.AlertCorner == HudAnchor.Custom ? HudAnchor.BottomRight : snap.AlertCorner;
        bool top = corner is HudAnchor.TopLeft or HudAnchor.TopRight;
        bool left = corner is HudAnchor.TopLeft or HudAnchor.BottomLeft;

        float margin = 16f * s, gap = 6f * s;
        float padX = 10f * s, padY = 7f * s, barW = 3f;
        float maxW = 340f * s;
        float lineH = font.GetHeight(g);
        float rowH = lineH + padY * 2f;

        // If the alert stack shares the panel's corner, start past the panel so they never overlap.
        float startY;
        if (top)
        {
            startY = margin;
            if (!panelRect.IsEmpty && corner == panelAnchor) startY = panelRect.Bottom + 10f * s;
        }
        else
        {
            startY = mon.Height - margin - rowH;
            if (!panelRect.IsEmpty && corner == panelAnchor) startY = panelRect.Top - 10f * s - rowH;
        }

        float y = startY;
        foreach (var alert in snap.Alerts) // newest first, stacking away from the corner
        {
            if (y < 0 || y + rowH > mon.Height) break;

            var maxTextW = maxW - padX * 2f - barW;
            var textW = Math.Min(g.MeasureString(alert.Text, font, (int)maxTextW, fmt).Width + 2f, maxTextW);
            var w = textW + padX * 2f + barW;
            var x = left ? margin : mon.Width - margin - w;
            var rect = new RectangleF(x, y, w, rowH);
            var radius = 7f * s;

            var shadowRect = rect;
            shadowRect.Offset(0f, 1.5f * s);
            using (var shadowPath = RoundedPath(shadowRect, radius))
            using (var shadowBrush = new SolidBrush(PanelShadow))
            {
                g.FillPath(shadowBrush, shadowPath);
            }
            using (var path = RoundedPath(rect, radius))
            {
                using (var bg = new SolidBrush(PanelBg)) g.FillPath(bg, path);
                using (var border = new Pen(PanelBorder, 1f)) g.DrawPath(border, path);
            }

            var severityColor = alert.Severity switch
            {
                HudAlertSeverity.Success => StatusGreen,
                HudAlertSeverity.Warning => StatusOrange,
                HudAlertSeverity.Error => StatusRed,
                _ => accent
            };
            using (var bar = new SolidBrush(Color.FromArgb(210, severityColor)))
            {
                g.FillRectangle(bar, rect.X, rect.Y + radius * 0.7f, barW, rect.Height - radius * 1.4f);
            }
            using (var brush = new SolidBrush(TextValue))
            {
                g.DrawString(alert.Text, font, brush,
                    new RectangleF(rect.X + barW + padX - 2f, rect.Y + padY, maxTextW, lineH + 1f), fmt);
            }

            y += top ? rowH + gap : -(rowH + gap);
        }
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    // ─── Monitor resolution ────────────────────────────────────────────────────────────────

    private MonitorInfo ResolveMonitor(string deviceName)
    {
        var monitors = EnumerateMonitors();
        if (!string.IsNullOrEmpty(deviceName))
        {
            var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        return primary ?? monitors.FirstOrDefault()
            ?? new MonitorInfo("\\\\.\\DISPLAY1", "Primary", 0, 0, 1920, 1080, true);
    }

    internal static MonitorInfo[] EnumerateMonitors()
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

    // ─── Layered-window push ───────────────────────────────────────────────────────────────

    private void PushToWindow(Bitmap bmp, int screenX, int screenY, byte globalAlpha)
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
                SourceConstantAlpha = globalAlpha,
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

    // ─── Win32 interop ─────────────────────────────────────────────────────────────────────

    private const int WM_DESTROY = 0x0002;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_CAPTURECHANGED = 0x0215;
    private const int WM_USER_UPDATE = 0x0400 + 1;
    private const int WM_USER_STYLE = 0x0400 + 2;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int GWL_EXSTYLE = -20;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x00000002;

    private const uint MONITORINFOF_PRIMARY = 1;
    private static readonly IntPtr IDC_SIZEALL = new IntPtr(32646);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

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

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
