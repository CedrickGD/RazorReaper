using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Global hotkey registration. The public <see cref="RegisterHotkey"/> / <see cref="UnregisterHotkey"/>
/// methods can be called from any thread — they wake the overlay's UI thread via PostMessage so
/// the actual <c>RegisterHotKey</c> Win32 call runs where the message loop can receive WM_HOTKEY.
/// </summary>
internal sealed partial class CrosshairOverlayWindow
{
    // Auto-repeat (or a rapid double-tap) on the toggle hotkey would otherwise flicker the
    // overlay on/off several times per frame. 200 ms is below human-perceptible repeat lag but
    // well clear of OS key-repeat intervals (typical 30 ms..50 ms).
    private DateTime _lastHotkeyTimeUtc = DateTime.MinValue;
    private const int HotkeyDebounceMs = 200;

    /// <summary>True if the WM_HOTKEY just observed is within the debounce window of the last one.</summary>
    internal bool ShouldDebounceHotkey()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHotkeyTimeUtc).TotalMilliseconds < HotkeyDebounceMs) return true;
        _lastHotkeyTimeUtc = now;
        return false;
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

    private static uint BuildModFlags(bool ctrl, bool alt, bool shift)
    {
        uint mods = MOD_NOREPEAT;
        if (ctrl) mods |= MOD_CONTROL;
        if (alt) mods |= MOD_ALT;
        if (shift) mods |= MOD_SHIFT;
        return mods;
    }
}
