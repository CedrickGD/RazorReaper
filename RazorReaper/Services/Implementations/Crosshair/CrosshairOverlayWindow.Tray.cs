using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// System tray icon: register/unregister via Shell_NotifyIcon, handle clicks via the shared
/// WndProc, and host the right-click popup menu. Tray ownership lives here because the icon
/// is parented to the overlay's hwnd, which means tray callbacks pass through the same
/// message loop the renderer uses.
/// </summary>
internal sealed partial class CrosshairOverlayWindow
{
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
}
