using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// "Rebind keys in ARK, press Rescan" has to work both ways, and keep working after the player
/// changes some other setting. Tried offline on master cdd05ad it did neither: Fed Suit stayed on
/// F/T because every settings save had written the keys as the player's own, and undoing the
/// rebind left Crafting and Turret Manager on the custom key until the app restarted, because
/// Rescan re-read the file and never told a script.
///
/// A fake install in a temp directory and a fake preference store: never the machine's real
/// Input.ini or preferences. Shares the <c>ArkKeyDefaults</c> collection, whose statics it swaps.
/// </summary>
[Collection("ArkKeyDefaults")]
public sealed class ArkKeyFollowTests : IDisposable
{
    private readonly string _arkRoot = Path.Combine(Path.GetTempPath(), "rr-arkfollow-" + Guid.NewGuid().ToString("N"));
    private readonly FakePreferencesStore _prefs = new();
    private readonly ArkKeyBindingService _scan;
    private readonly RazorReaper.Services.IPreferencesStore _realPrefs = ArkKeyDefaults.Prefs;

    public ArkKeyFollowTests()
    {
        // The install's factory layout, deliberately not the stock table for Show My Inventory, so
        // "back to ARK's default" has to mean this file was read.
        WriteFile(DefaultInputIni,
            "+ActionMappings=(ActionName=\"AccessInventory\",Key=F,bShift=False)",
            "+ActionMappings=(ActionName=\"TransferItem\",Key=T,bShift=False)",
            "+ActionMappings=(ActionName=\"ShowMyInventory\",Key=O,bShift=False)");

        _scan = new ArkKeyBindingService(new FakeArkPathProvider(_arkRoot), NullLogger<ArkKeyBindingService>.Instance);
        ArkKeyDefaults.ResolveService = () => _scan;
        ArkKeyDefaults.Prefs = _prefs;
    }

    public void Dispose()
    {
        ArkKeyDefaults.ResolveService = ArkKeyDefaults.DefaultResolver;
        ArkKeyDefaults.Prefs = _realPrefs;
        try { if (Directory.Exists(_arkRoot)) Directory.Delete(_arkRoot, recursive: true); }
        catch { /* a leftover temp directory is not a test failure */ }
    }

    [Fact]
    public void ARebindReachesTheScriptsOnRescanAndUndoingItGoesBackToArksDefault()
    {
        using var fed = Macro();
        using var afk = AntiAfk();
        Assert.Equal(("F", "T", "O"), (fed.Settings.OpenKey, fed.Settings.TransferKey, afk.InventoryKey));

        Rebind();
        Rescan(fed, afk);
        Assert.Equal(("G", "Y", "K"), (fed.Settings.OpenKey, fed.Settings.TransferKey, afk.InventoryKey));

        // The player puts ARK back. The install's DefaultInput.ini answers, not the key held before.
        File.Delete(InputIni);
        Rescan(fed, afk);
        Assert.Equal(("F", "T", "O"), (fed.Settings.OpenKey, fed.Settings.TransferKey, afk.InventoryKey));
    }

    [Fact]
    public void AKeyThePlayerTypedSurvivesBothRescansAndARestart()
    {
        using var fed = Macro();
        using var afk = AntiAfk();
        Configure(fed, s => s.OpenKey = "P");
        afk.InventoryKey = "P";

        Rebind();
        Rescan(fed, afk);
        Assert.Equal(("P", "Y", "P"), (fed.Settings.OpenKey, fed.Settings.TransferKey, afk.InventoryKey));

        File.Delete(InputIni);
        Rescan(fed, afk);
        Assert.Equal(("P", "T", "P"), (fed.Settings.OpenKey, fed.Settings.TransferKey, afk.InventoryKey));

        using var restartedAfk = AntiAfk();
        using var restartedFed = Macro();
        Assert.Equal("P", restartedFed.Settings.OpenKey);
        Assert.Equal("P", restartedAfk.InventoryKey);
    }

    [Fact]
    public void TypingArksOwnKeyOrClearingTheFieldGoesBackToFollowing()
    {
        using var afk = AntiAfk();
        afk.InventoryKey = "P";
        Assert.True(_prefs.ContainsKey("antiafk.invkey"));

        afk.InventoryKey = "o";
        Assert.False(_prefs.ContainsKey("antiafk.invkey"));

        afk.InventoryKey = "P";
        afk.InventoryKey = "  ";
        Assert.Equal("O", afk.InventoryKey);
        Assert.False(_prefs.ContainsKey("antiafk.invkey"));
    }

    [Fact]
    public void SavingAnUnrelatedSettingDoesNotPinTheKeys()
    {
        using var fed = Macro();
        using var afk = AntiAfk();

        Configure(fed, s => s.Runs = 5);
        Configure(fed, s => s.LagBufferMs = 120);
        afk.IntervalSeconds = 120;
        afk.SaveSettings();

        Assert.False(_prefs.ContainsKey("fedsuit.openkey"));
        Assert.False(_prefs.ContainsKey("fedsuit.transferkey"));
        Assert.False(_prefs.ContainsKey("antiafk.invkey"));

        Rebind();
        Rescan(fed, afk);
        Assert.Equal(("G", "Y", "K"), (fed.Settings.OpenKey, fed.Settings.TransferKey, afk.InventoryKey));
        Assert.Equal(5, fed.Settings.Runs);
    }

    /// <summary>The owner's store as cdd05ad left it: F and T written by a Runs change, never typed.</summary>
    [Fact]
    public void AKeyPinnedAtArksOwnBindingIsDroppedOnLoadSoItFollowsAgain()
    {
        _prefs.Seed("fedsuit.openkey", "F");
        _prefs.Seed("fedsuit.transferkey", "T");
        _prefs.Seed("antiafk.invkey", "P");

        using var fed = Macro();
        using var afk = AntiAfk();

        Assert.False(_prefs.ContainsKey("fedsuit.openkey"));
        Assert.False(_prefs.ContainsKey("fedsuit.transferkey"));
        // One that differs from ARK's is a choice, and stays.
        Assert.Equal("P", afk.InventoryKey);

        Rebind();
        Rescan(fed, afk);
        Assert.Equal(("G", "Y"), (fed.Settings.OpenKey, fed.Settings.TransferKey));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private string InputIni => Path.Combine(_arkRoot, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "Input.ini");

    private string DefaultInputIni => Path.Combine(_arkRoot, "ShooterGame", "Config", "DefaultInput.ini");

    private void Rebind() => WriteFile(InputIni,
        "ActionMappings=(ActionName=\"AccessInventory\",Key=G,bShift=False)",
        "ActionMappings=(ActionName=\"TransferItem\",Key=Y,bShift=False)",
        "ActionMappings=(ActionName=\"ShowMyInventory\",Key=K,bShift=False)");

    /// <summary>What the Scripts page's Rescan does: a forced scan, then every script re-asks it.</summary>
    private void Rescan(FedSuitMacro fed, AutomationScriptBase script)
    {
        _scan.Refresh();
        fed.FollowArkKeys();
        script.FollowArkKeys();
    }

    private static void WriteFile(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[] { "[/Script/Engine.InputSettings]" }.Concat(lines));
    }

    private static void Configure(FedSuitMacro macro, Action<FedSuitSettings> edit)
    {
        var settings = macro.Settings;
        edit(settings);
        macro.UpdateSettings(settings);
    }

    private static Localizer English() => new(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

    private static FedSuitMacro Macro() => new(
        new FakeMacroEngine(),
        new FakeGameDisplayService(),
        new FakeArkPathProvider(),
        new FakeScreenSampler(),
        new RecordingInputSimulator(),
        new FakeForegroundGate(gameIsForeground: true),
        new RecordingNotificationService(),
        new RecordingActivityService(),
        new CountingUsageGateService(),
        English(),
        NullLogger<FedSuitMacro>.Instance);

    private static AntiAfkScript AntiAfk() => new(
        new RecordingInputSimulator(),
        new FakeForegroundGate(gameIsForeground: true),
        new NullAutomationHotkeyService(),
        new RecordingNotificationService(),
        new RecordingActivityService(),
        English(),
        NullLogger<AntiAfkScript>.Instance);
}
