using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Owns the Auto Clicker's system-wide start/stop hotkey.
///
/// The binding used to be a 50 ms <c>GetAsyncKeyState</c> poll living inside the Auto Clicker
/// page, on a cancellation token the component disposed — so the hotkey quietly died the moment
/// you navigated anywhere else, and it needed a 2 s cooldown to debounce a polled edge. This
/// binder registers the key once, for the lifetime of the app, through the same Win32
/// <c>RegisterHotKey</c> plumbing the 16 automation scripts use, so it behaves like every other
/// binding in the app and costs nothing while idle.
///
/// Registration is a real system-wide claim on the key: it no longer reaches other applications
/// while RazorReaper runs. That is the same trade every script hotkey already makes, and it is
/// what makes a stop key dependable while the game holds focus.
/// </summary>
public interface IAutoClickerHotkeyBinder
{
    /// <summary>Raised (on the thread pool) each time the bound key is pressed.</summary>
    event Action? Toggled;

    /// <summary>True while the key is claimed successfully.</summary>
    bool IsBound { get; }

    /// <summary>Re-reads <see cref="AutoClickerHotkey"/> and re-registers. Safe to call repeatedly.</summary>
    void Rebind();

    /// <summary>
    /// Releases the key while the user is recording a new one, so the keystroke being captured
    /// cannot also fire the toggle. Re-registers when set back to false.
    /// </summary>
    bool IsSuspended { get; set; }
}

public sealed class AutoClickerHotkeyBinder : IAutoClickerHotkeyBinder, IDisposable
{
    private readonly IAutomationHotkeyService _hotkeys;
    private readonly INotificationService _notifications;
    private readonly IAutoClickerRuntime _runtime;
    private readonly ILogger<AutoClickerHotkeyBinder> _logger;
    private readonly object _gate = new();

    private int _registrationId;
    private bool _suspended;
    private bool _disposed;

    public event Action? Toggled;

    public AutoClickerHotkeyBinder(
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IAutoClickerRuntime runtime,
        ILogger<AutoClickerHotkeyBinder> logger)
    {
        _hotkeys = hotkeys;
        _notifications = notifications;
        _runtime = runtime;
        _logger = logger;

        // A binding edited on the hotkeys page has to take effect without a restart.
        AutoClickerHotkey.Changed += Rebind;
        Rebind();
    }

    public bool IsBound
    {
        get { lock (_gate) return _registrationId != 0; }
    }

    public bool IsSuspended
    {
        get { lock (_gate) return _suspended; }
        set
        {
            lock (_gate)
            {
                if (_suspended == value) return;
                _suspended = value;
            }

            Rebind();
        }
    }

    public void Rebind()
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_registrationId != 0)
            {
                _hotkeys.UnregisterHotkey(_registrationId);
                _registrationId = 0;
            }

            if (_suspended) return;

            var vk = AutoClickerHotkey.Code;
            if (vk <= 0)
            {
                _logger.LogWarning("Auto Clicker hotkey '{Display}' has no virtual-key code — not bound", AutoClickerHotkey.Display);
                return;
            }

            // The Auto Clicker's key is a bare key, never a chord — no modifiers to pass on.
            _registrationId = _hotkeys.RegisterHotkey(vk, ctrl: false, alt: false, shift: false, callback: RaiseToggled);

            if (_registrationId == 0)
            {
                _logger.LogWarning("Could not register Auto Clicker hotkey {Display} (vk=0x{Vk:X2}) — already held by another app", AutoClickerHotkey.Display, vk);
                _notifications.ShowWarning($"Could not register {AutoClickerHotkey.Display} for the Auto Clicker — it may be in use by another app.");
            }
            else
            {
                _logger.LogDebug("Auto Clicker hotkey bound: {Display} (vk=0x{Vk:X2})", AutoClickerHotkey.Display, vk);
            }
        }
    }

    private void RaiseToggled()
    {
        try
        {
            // Toggle the runtime, not a page: the whole point is that this works with the Auto
            // Clicker page closed, or with no window focused at all.
            _ = _runtime.ToggleAsync();
            Toggled?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber throwing must not kill the hotkey pump.
            _logger.LogError(ex, "Auto Clicker hotkey subscriber threw");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AutoClickerHotkey.Changed -= Rebind;

        lock (_gate)
        {
            if (_registrationId != 0)
            {
                _hotkeys.UnregisterHotkey(_registrationId);
                _registrationId = 0;
            }
        }

        Toggled = null;
    }
}
