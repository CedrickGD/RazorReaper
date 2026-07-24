using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Speeds up bed/teleporter travel: with the teleport menu open (its search box focused), pressing
/// Start types the configured destination name and confirms, so you jump without hunting the list.
/// One-shot per start. Pure keyboard via <see cref="IInputSimulator"/>.
/// </summary>
public sealed class FastTpScript : AutomationScriptBase
{
    private const string Key = "fasttp";
    private const int EnterVk = 0x0D;
    private readonly IInputSimulator _input;

    /// <summary>Destination name typed into the teleport search field.</summary>
    public string Destination { get; set; } = "";

    /// <summary>Press Enter after typing to confirm the first match.</summary>
    public bool ConfirmWithEnter { get; set; } = true;

    public FastTpScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<FastTpScript> logger)
        : base(Key, "Fast TP", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override bool CanStart(out string? reason)
    {
        if (string.IsNullOrWhiteSpace(Destination))
        {
            reason = "Set a destination name first.";
            return false;
        }
        if (!Foreground.IsGameForeground())
        {
            reason = "Open the teleporter/bed menu in ARK first, then start.";
            return false;
        }
        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        await _input.TypeTextAsync(Destination, ct: ct);
        await _input.DelayAsync(200, ct: ct);
        if (ConfirmWithEnter)
            await _input.KeyPressAsync(EnterVk, ct: ct);
        // one-shot: returns → back to Off
    }

    public void SaveSettings()
    {
        Destination = Destination?.Trim() ?? "";
        try
        {
            Preferences.Set($"{Key}.destination", Destination);
            Preferences.Set($"{Key}.confirm", ConfirmWithEnter);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Fast TP SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            Destination = Preferences.Get($"{Key}.destination", "");
            ConfirmWithEnter = Preferences.Get($"{Key}.confirm", true);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Fast TP LoadSettings failed"); }
    }
}
