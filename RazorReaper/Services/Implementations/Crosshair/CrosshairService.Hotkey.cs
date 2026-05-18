namespace RazorReaper.Services.Implementations;

/// <summary>
/// Global hotkey wiring. The Win32 registration itself lives on the overlay window thread
/// (see <see cref="CrosshairOverlayWindow.RegisterHotkey"/>); this partial owns the user-facing
/// API and the toggle-with-notification handler the overlay invokes when the hotkey fires.
/// </summary>
public partial class CrosshairService
{
    private void OnHotkeyToggle()
    {
        ToggleOverlay();
        try
        {
            if (_overlayActive)
                _notifications.ShowInfo("Crosshair overlay enabled.");
            else
                _notifications.ShowInfo("Crosshair overlay disabled.");
        }
        catch { /* notifications can fail in odd shutdown paths */ }
    }

    public void SetHotkey(string displayLabel, int virtualKey, bool ctrl, bool alt, bool shift)
    {
        _hotkeyLabel = displayLabel;
        _hotkeyVk = virtualKey;
        _hotkeyCtrl = ctrl;
        _hotkeyAlt = alt;
        _hotkeyShift = shift;

        _overlay.UnregisterHotkey();
        if (virtualKey > 0)
            _overlay.RegisterHotkey(virtualKey, ctrl, alt, shift);

        _ = PersistSettingsAsync();
        Changed?.Invoke();
    }

    public (string Label, int VirtualKey, bool Ctrl, bool Alt, bool Shift) GetHotkey()
        => (_hotkeyLabel, _hotkeyVk, _hotkeyCtrl, _hotkeyAlt, _hotkeyShift);
}
