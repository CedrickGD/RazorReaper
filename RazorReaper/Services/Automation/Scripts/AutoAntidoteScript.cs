using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Watches a calibrated icon and fires a hotbar key when it appears, disappears, or its timer runs
/// low — the antidote/brew reflex, automated.
///
/// The watcher itself lives in <see cref="IAutoAntidoteService"/>, which predates the script
/// catalogue and carries its own scan loop, OCR timer reading and calibration. This wrapper puts
/// it in the Scripts list so it starts, stops and binds a hotkey exactly like every other script,
/// rather than being a page you have to go and find. The service no longer registers a hotkey of
/// its own — this script is the single owner.
/// </summary>
public sealed class AutoAntidoteScript : AutomationScriptBase
{
    private const string Key = "antidote";

    private readonly IAutoAntidoteService _service;

    public AutoAntidoteScript(
        IAutoAntidoteService service,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<AutoAntidoteScript> logger)
        : base(Key, "Auto Antidote", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _service = service;

        // The watcher can also stop itself (a one-shot mode, a failed scan). Mirror that so the
        // Scripts list never shows a green dot for something that already finished.
        _service.Changed += OnServiceChanged;
    }

    /// <summary>The underlying watcher, for the settings and calibration UI.</summary>
    public IAutoAntidoteService Service => _service;

    protected override bool CanStart(out string? reason)
    {
        if (!_service.HasRegion)
        {
            reason = "Calibrate the icon region first.";
            return false;
        }

        if (!_service.HasReference)
        {
            reason = "Capture a reference with the icon visible first.";
            return false;
        }

        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (!_service.Start())
        {
            // Start() re-checks its own prerequisites and reports why; nothing to add here.
            return;
        }

        try
        {
            // The service owns the scan loop. Hold the script open until it is toggled off, then
            // let the finally below take the watcher down with it.
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        finally
        {
            _service.Stop();
        }
    }

    private void OnServiceChanged()
    {
        if (_service.State == AutoAntidoteState.Off && IsRunning)
        {
            Stop();
            return;
        }

        RaiseChanged();
    }

    protected override void OnStopped() => _service.Stop();
}
