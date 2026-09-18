using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>How the crafting loop drives the stations.</summary>
public enum CraftingMode
{
    /// <summary>You walk to each station and open it; the script detects it and crafts, without moving you.</summary>
    Watcher,
    /// <summary>The script walks the row of stations itself and repeats in a loop.</summary>
    Walk
}

/// <summary>
/// Native crafting automation for Fabricator / Chemistry Bench / Tek Replicator / Tek Crafter.
/// <see cref="CraftingMode.Watcher"/> waits until the calibrated station inventory is detected and
/// then crafts (no character movement); <see cref="CraftingMode.Walk"/> walks the station row itself.
/// Every wait gets <see cref="PingCompensationMs"/> added, so a laggy server doesn't desync the
/// sequence — the known weak spot of the tool this mirrors. Pure input + screen capture.
/// </summary>
public sealed class CraftingScript : CalibratableScriptBase
{
    private const string Key = "crafting";
    private readonly IInputSimulator _input;

    public CraftingMode Mode { get; set; } = CraftingMode.Watcher;

    /// <summary>Key that starts a craft inside the open station (ARK default: E).</summary>
    public string CraftKey { get; set; } = "E";

    /// <summary>Key that opens/closes the station (ARK default: F).</summary>
    public string AccessKey { get; set; } = "F";

    /// <summary>Movement key used in Walk mode (ARK default: W).</summary>
    public string ForwardKey { get; set; } = "W";

    /// <summary>Craft presses per station.</summary>
    public int CraftPresses { get; set; } = 3;

    /// <summary>Extra milliseconds added to every wait to absorb server lag.</summary>
    public int PingCompensationMs { get; set; } = 0;

    /// <summary>Similarity % at which the station inventory counts as open (Watcher mode).</summary>
    public double MatchThresholdPercent { get; set; } = 90;

    /// <summary>Milliseconds to hold forward between stations (Walk mode).</summary>
    public int WalkMs { get; set; } = 900;

    /// <summary>Milliseconds between scans (Watcher mode).</summary>
    public int ScanIntervalMs { get; set; } = 400;

    public CraftingScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<CraftingScript> logger)
        : base(Key, "Crafting", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _input = input;
        LoadSettings();
    }

    /// <summary>
    /// Only in Walk mode. Watcher waits until it can see the station inventory before it crafts;
    /// Walk holds forward for a fixed number of milliseconds and presses the access key where it
    /// hopes the next station is. One step out of place and the rest of the row is pressed into
    /// thin air, and nothing in the loop can notice.
    /// </summary>
    public override bool IsExperimental => Mode == CraftingMode.Walk;

    // Watcher needs the calibrated region+reference; Walk drives blind, so it needs no calibration.
    protected override bool CanStart(out string? reason)
    {
        if (Mode == CraftingMode.Watcher) return base.CanStart(out reason);
        reason = null;
        return true;
    }

    private int Pad(int ms) => ms + Math.Clamp(PingCompensationMs, 0, 3000);

    protected override Task RunAsync(CancellationToken ct)
        => Mode == CraftingMode.Watcher ? RunWatcherAsync(ct) : RunWalkAsync(ct);

    private Task RunWatcherAsync(CancellationToken ct) =>
        RunLoopAsync(ScanIntervalMs, async c =>
        {
            if (!IsTargetVisible(MatchThresholdPercent)) return;
            var craftVk = HotkeyParser.TryParseKey(CraftKey, out var k) ? k : 'E';
            for (var i = 0; i < Math.Clamp(CraftPresses, 1, 20); i++)
            {
                await _input.KeyPressAsync(craftVk, ct: c);
                ReportEffect();
                await _input.DelayAsync(Pad(150), ct: c);
            }
        }, foregroundOnly: true, ct);

    private async Task RunWalkAsync(CancellationToken ct)
    {
        var fwdVk = HotkeyParser.TryParseKey(ForwardKey, out var f) ? f : 'W';
        var accessVk = HotkeyParser.TryParseKey(AccessKey, out var a) ? a : 'F';
        var craftVk = HotkeyParser.TryParseKey(CraftKey, out var k) ? k : 'E';

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Foreground.IsGameForeground())
                {
                    // move to the next station
                    HoldKey(_input, fwdVk);
                    await _input.DelayAsync(Math.Clamp(WalkMs, 100, 10000), ct: ct);
                    ReleaseKey(_input, fwdVk);
                    ReportEffect();
                    await _input.DelayAsync(Pad(250), ct: ct);

                    // open, craft, close
                    await _input.KeyPressAsync(accessVk, ct: ct);
                    await _input.DelayAsync(Pad(400), ct: ct);
                    for (var i = 0; i < Math.Clamp(CraftPresses, 1, 20); i++)
                    {
                        await _input.KeyPressAsync(craftVk, ct: ct);
                        ReportEffect();
                        await _input.DelayAsync(Pad(150), ct: ct);
                    }
                    await _input.KeyPressAsync(accessVk, ct: ct);
                    await _input.DelayAsync(Pad(250), ct: ct);
                }
                else
                {
                    await Task.Delay(300, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // A stop lands mid-walk more often than not — the walk leg is the longest one.
            ReleaseHeldKeys();
        }
    }

    public void SaveSettings()
    {
        CraftKey = string.IsNullOrWhiteSpace(CraftKey) ? "E" : CraftKey.Trim();
        AccessKey = string.IsNullOrWhiteSpace(AccessKey) ? "F" : AccessKey.Trim();
        ForwardKey = string.IsNullOrWhiteSpace(ForwardKey) ? "W" : ForwardKey.Trim();
        CraftPresses = Math.Clamp(CraftPresses, 1, 20);
        PingCompensationMs = Math.Clamp(PingCompensationMs, 0, 3000);
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        WalkMs = Math.Clamp(WalkMs, 100, 10000);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 100, 5000);
        try
        {
            Preferences.Set($"{Key}.mode", (int)Mode);
            Preferences.Set($"{Key}.craftkey", CraftKey);
            Preferences.Set($"{Key}.accesskey", AccessKey);
            Preferences.Set($"{Key}.forwardkey", ForwardKey);
            Preferences.Set($"{Key}.presses", CraftPresses);
            Preferences.Set($"{Key}.ping", PingCompensationMs);
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
            Preferences.Set($"{Key}.walkms", WalkMs);
            Preferences.Set($"{Key}.scaninterval", ScanIntervalMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Crafting SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            var mode = Preferences.Get($"{Key}.mode", (int)CraftingMode.Watcher);
            Mode = Enum.IsDefined(typeof(CraftingMode), mode) ? (CraftingMode)mode : CraftingMode.Watcher;
            // "Use" is what actually starts a craft on the highlighted recipe; the access key is
            // the same one that opens any container. Both follow the player's ARK bindings unless
            // they have set their own here.
            CraftKey = Preferences.Get($"{Key}.craftkey", ArkKeyDefaults.For(ArkActions.Use, "E"));
            AccessKey = Preferences.Get($"{Key}.accesskey", ArkKeyDefaults.For(ArkActions.AccessInventory, "F"));
            ForwardKey = Preferences.Get($"{Key}.forwardkey", ArkKeyDefaults.For(ArkActions.MoveForward, "W"));
            CraftPresses = Preferences.Get($"{Key}.presses", 3);
            PingCompensationMs = Preferences.Get($"{Key}.ping", 0);
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
            WalkMs = Preferences.Get($"{Key}.walkms", 900);
            ScanIntervalMs = Preferences.Get($"{Key}.scaninterval", 400);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Crafting LoadSettings failed"); }
        CraftPresses = Math.Clamp(CraftPresses, 1, 20);
        PingCompensationMs = Math.Clamp(PingCompensationMs, 0, 3000);
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        WalkMs = Math.Clamp(WalkMs, 100, 10000);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 100, 5000);
    }
}
