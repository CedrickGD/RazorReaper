using Microsoft.Extensions.Logging;
using RazorReaper.Services.Localization;

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

    /// <summary>
    /// The watcher's trigger count as this script last saw it. <see cref="IAutoAntidoteService.Changed"/>
    /// carries no reason — it fires for a state change, a counter and every scan's live match
    /// number alike — so this counter is what says a burst actually went out rather than that a
    /// scan merely updated a percentage. The watcher's own counter never resets, so the run
    /// seeds itself from wherever it stands.
    /// </summary>
    private int _lastTriggerCount;

    public AutoAntidoteScript(
        IAutoAntidoteService service,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<AutoAntidoteScript> logger)
        : base(Key, "Auto Antidote", string.Empty, foreground, hotkeys, notifications, activity, localizer, logger)
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
            reason = Localizer.T("scripts.cannotstart.iconregion");
            return false;
        }

        if (!_service.HasReference)
        {
            reason = Localizer.T("scripts.cannotstart.iconreference");
            return false;
        }

        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _lastTriggerCount, _service.TriggerCount);

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
        // Exchange rather than read-then-write: this event arrives on the watcher's scan
        // thread, and two of them overlapping must not report the same burst twice.
        var triggers = _service.TriggerCount;
        if (Interlocked.Exchange(ref _lastTriggerCount, triggers) != triggers && IsRunning)
            ReportEffect();

        if (_service.State == AutoAntidoteState.Off && IsRunning)
        {
            Stop();
            return;
        }

        RaiseChanged();
    }

    protected override void OnStopped() => _service.Stop();
}
