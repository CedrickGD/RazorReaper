using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// System tray icon: register/unregister via Shell_NotifyIcon, handle clicks via the shared
/// WndProc, and host the right-click popup menu. Tray ownership lives here because the icon
/// is parented to the overlay's hwnd, which means tray callbacks pass through the same
/// message loop the renderer uses.
///
/// This menu is outside the Blazor window and never sees its repaint, so language is handled
/// by rebuilding rather than by re-rendering. Every item is read from the dictionary while the
/// menu is being built, and the menu is built fresh on each right-click — which leaves exactly
/// one string that outlives a language switch, the tray tooltip, and that one is re-set on the
/// overlay's own thread when the switch happens.
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
                szTip = _localizer.T("tray.tooltip")
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

    /// <summary>
    /// Re-sends the tooltip after a language switch. Called on the overlay's UI thread, through
    /// WM_USER_TRAY_RETIP, because the icon belongs to that thread's window.
    /// </summary>
    private void UpdateTrayTooltip()
    {
        if (!_trayRegistered) return;
        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayIconUID,
                uFlags = NIF_TIP,
                szTip = _localizer.T("tray.tooltip")
            };

            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update tray tooltip");
        }
    }

    /// <summary>Wakes the overlay thread to re-read the tooltip. Safe from any thread.</summary>
    public void RefreshTrayTooltip()
    {
        if (_hwnd == IntPtr.Zero) return;
        PostMessage(_hwnd, WM_USER_TRAY_RETIP, IntPtr.Zero, IntPtr.Zero);
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
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                SafeInvoke(_onTrayShowApp, "tray left-click → show app");
                break;
            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
                ShowTrayMenu();
                break;
        }
    }

    private static bool s_darkMenusEnabled;

    private static void TryEnableDarkMenus()
    {
        if (s_darkMenusEnabled) return;
        try
        {
            // uxtheme ordinal 135 = SetPreferredAppMode. AllowDark (1) makes Win32 popup
            // menus follow the system dark-mode setting — so on Win11 dark mode the tray
            // menu picks up the native dark/rounded look instead of the legacy white menu.
            SetPreferredAppMode(1);
            FlushMenuThemes();
        }
        catch
        {
            // Pre-Win10 1903 or future API change — fall back to default system styling.
        }
        // Set unconditionally so we don't keep retrying on older OS versions.
        s_darkMenusEnabled = true;
    }

    private void ShowTrayMenu()
    {
        TryEnableDarkMenus();

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var overlayActive = false;
        try { overlayActive = _isOverlayActive(); } catch { }

        string? updateLabel = null;
        try { updateLabel = _updateReadyLabel(); } catch { }

        // Only while an installer is actually staged. Top of the menu because it is the one
        // item here that is time-limited; everything below it is always available.
        // "&&" is not a typo: AppendMenu reads a single & as the mnemonic prefix, so the item
        // would otherwise read "Restart _update".
        if (!string.IsNullOrWhiteSpace(updateLabel))
        {
            AppendMenu(menu, MF_STRING, CmdApplyUpdate, Mnemonic(_localizer.T("tray.update", updateLabel)));
            AppendMenu(menu, MF_SEPARATOR, 0, null);
        }

        AppendMenu(menu, MF_STRING, CmdOpenApp, Mnemonic(_localizer.T("tray.open")));
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | (overlayActive ? MF_CHECKED : 0), CmdToggleOverlay,
            Mnemonic(_localizer.T(overlayActive ? "tray.overlay.hide" : "tray.overlay.show")));
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdQuit, Mnemonic(_localizer.T("tray.quit")));

        GetCursorPos(out POINT pt);
        // Windows quirk — TrackPopupMenu won't dismiss correctly without first focusing the owner.
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_LEFTALIGN, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
    }

    /// <summary>
    /// Escapes a menu label for AppendMenu, which reads a single &amp; as the mnemonic prefix and
    /// swallows it. "Restart &amp; update" would otherwise read "Restart _update" — and now that
    /// the labels come from four dictionaries, an ampersand can arrive from any of them.
    /// </summary>
    private static string Mnemonic(string label) => label.Replace("&", "&&", StringComparison.Ordinal);

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
            case CmdApplyUpdate:
                SafeInvoke(_onTrayApplyUpdate, "tray restart & update");
                break;
        }
    }

    private void SafeInvoke(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Tray action threw: {What}", what); }
    }
}
