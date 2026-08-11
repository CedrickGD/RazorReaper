#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;
#endif

namespace RazorReaper.Services.Media;

/// <summary>
/// Brings RazorReaper's own window to the front.
///
/// Needed before opening a file dialog: MAUI resolves the dialog's owner from the *active*
/// window, and while ARK holds exclusive fullscreen this app is not active, so there is no
/// valid HWND and IFileDialog::Show fails with COMException 0x80004005 — which surfaced as
/// "Could not open the file picker" whenever the game was running.
///
/// Only ever touches this app's window. Nothing else on the desktop is minimised, hidden or
/// re-ordered to make room for it.
/// </summary>
public static class WindowActivator
{
#if WINDOWS
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Restores from minimised or hidden, unlike SW_SHOW which leaves both alone.</summary>
    private const int SW_RESTORE = 9;
#endif

    /// <summary>
    /// Best effort by design: if the window cannot be raised the caller should still try the
    /// dialog, because on a normal desktop it works regardless.
    /// </summary>
    public static void BringToFront()
    {
#if WINDOWS
        try
        {
            // Fully qualified: Window is ambiguous here between Microsoft.Maui.Controls.Window
            // and Microsoft.UI.Xaml.Window, and both are in scope.
            var window = Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault();
            if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;

            var hwnd = WindowNative.GetWindowHandle(native);
            if (hwnd == IntPtr.Zero) return;

            // Only when it is actually minimised. SW_RESTORE on a maximised window is a
            // restore in the literal sense — pressing "Choose file" dropped a full-screen
            // window back to its small size every single time.
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Purely an aid to the dialog below; never let it break the interaction.
        }
#endif
    }
}
