using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The scan reads the keys the player actually bound, and keeps reading them.
///
/// A script whose key is wrong is the worst failure this app has: it starts, it reports running,
/// it presses something every tick and nothing happens — which looks exactly like a script that
/// works on a laggy server. Two ways to end up there were still open. Fed Suit hard-coded F and T
/// and never asked the scan at all, so rebinding Access Inventory turned the transmitter loop into
/// a loop that opened nothing. And the scan itself ran once, in the constructor, so rebinding a key
/// in ARK and alt-tabbing back left every script on the previous layout until the app was restarted.
///
/// Real files in a real temp directory rather than a file-system abstraction: the thing under test
/// is "does it notice the file changed", and a fake whose timestamps we write ourselves would be
/// testing the fake.
/// </summary>
public sealed class ArkKeyScanTests : IDisposable
{
    private readonly string _arkRoot = Path.Combine(
        Path.GetTempPath(), "rr-arkkeys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_arkRoot)) Directory.Delete(_arkRoot, recursive: true); }
        catch { /* a leftover temp directory is not a test failure */ }
    }

    // ─── The file is read, and re-read ─────────────────────────────────────────

    [Fact]
    public void APlayersOwnBindingBeatsArksFactoryOne()
    {
        WriteInputIni("ActionMappings=(ActionName=\"AccessInventory\",Key=Y,bShift=False)");
        var service = Service();

        Assert.Equal("Y", service.Resolve(ArkActions.AccessInventory, "F"));
        Assert.True(service.HasPlayerBindings);
    }

    [Fact]
    public void ARebindWhileTheAppIsOpenIsPickedUpOnTheNextStaleCheck()
    {
        WriteInputIni("ActionMappings=(ActionName=\"TransferItem\",Key=G,bShift=False)");
        var service = Service();
        Assert.Equal("G", service.Resolve(ArkActions.TransferItem, "T"));

        // The player alt-tabs into ARK, moves Transfer Item, and comes back.
        WriteInputIni("ActionMappings=(ActionName=\"TransferItem\",Key=H,bShift=False)");
        service.RefreshIfStale();

        Assert.Equal("H", service.Resolve(ArkActions.TransferItem, "T"));
    }

    [Fact]
    public void AFileWhoseTimestampDidNotMoveIsNotParsedAgainUntilSomebodyAsks()
    {
        WriteInputIni("ActionMappings=(ActionName=\"TransferItem\",Key=G,bShift=False)");
        var service = Service();
        Assert.Equal("G", service.Resolve(ArkActions.TransferItem, "T"));

        // Rewritten content, timestamp pinned back to what it was: this is the case the cheap
        // check is allowed to miss, and pinning it is the only way to hold it still.
        var stamp = File.GetLastWriteTimeUtc(InputIniPath);
        File.WriteAllLines(InputIniPath, ["ActionMappings=(ActionName=\"TransferItem\",Key=H,bShift=False)"]);
        File.SetLastWriteTimeUtc(InputIniPath, stamp);

        service.RefreshIfStale();
        Assert.Equal("G", service.Resolve(ArkActions.TransferItem, "T"));

        // The Rescan button does not consult the timestamp, which is the whole point of it.
        service.Refresh();
        Assert.Equal("H", service.Resolve(ArkActions.TransferItem, "T"));
    }

    [Fact]
    public void AnInstallThatAppearsAfterStartupIsFoundWithoutARestart()
    {
        // The app opened before ARK was installed (or before the path was set), so the first scan
        // found nothing at all.
        var paths = new FakeArkPathProvider(arkRoot: null);
        var service = new ArkKeyBindingService(paths, NullLogger<ArkKeyBindingService>.Instance);
        Assert.False(service.Status.InputIniFound);

        WriteInputIni("ActionMappings=(ActionName=\"ShowMyInventory\",Key=Y,bShift=False)");
        paths.ArkRoot = _arkRoot;
        service.RefreshIfStale();

        Assert.Equal("Y", service.Resolve(ArkActions.ShowMyInventory, "I"));
        Assert.True(service.Status.InputIniFound);
    }

    /// <summary>
    /// What the stale check may cost. It runs on every script start with the global hotkey waiting
    /// on it, and finding the install is two registry reads, a parse of libraryfolders.vdf and a
    /// stat per Steam library — once, not once per start.
    /// </summary>
    [Fact]
    public void TheStaleCheckDoesNotHuntForTheInstallAgainWhileTheFileIsWhereItWas()
    {
        WriteInputIni("ActionMappings=(ActionName=\"TransferItem\",Key=G,bShift=False)");
        var paths = new FakeArkPathProvider(_arkRoot);
        var service = new ArkKeyBindingService(paths, NullLogger<ArkKeyBindingService>.Instance);
        Assert.Equal(1, paths.Lookups);

        service.RefreshIfStale();
        service.RefreshIfStale();
        Assert.Equal(1, paths.Lookups);

        // A rebind still lands — same path, new timestamp, and the file is read again.
        WriteInputIni("ActionMappings=(ActionName=\"TransferItem\",Key=H,bShift=False)");
        service.RefreshIfStale();
        Assert.Equal("H", service.Resolve(ArkActions.TransferItem, "T"));
        Assert.Equal(1, paths.Lookups);

        // Rescan looks again: where ARK is installed is exactly what that button can change.
        service.Refresh();
        Assert.Equal(2, paths.Lookups);
    }

    // ─── Nothing to read ───────────────────────────────────────────────────────

    [Fact]
    public void AMissingFileLeavesEveryActionOnArksStockKey()
    {
        Directory.CreateDirectory(_arkRoot);
        var service = Service();

        foreach (var (action, stock) in ArkKeyBindingParser.StockBindings)
        {
            Assert.Equal(stock, service.Resolve(action, "SHOULD-NOT-BE-USED"));
        }

        Assert.False(service.HasPlayerBindings);
        Assert.Equal(ArkKeyBindingStatus.NotFound, service.Status);
    }

    [Fact]
    public void NoArkInstallAtAllStillAnswersEveryLookup()
    {
        var service = new ArkKeyBindingService(
            new FakeArkPathProvider(arkRoot: null), NullLogger<ArkKeyBindingService>.Instance);

        Assert.Equal(ArkKeyBindingParser.StockBindings[ArkActions.MoveForward],
            service.Resolve(ArkActions.MoveForward, "W"));

        // An action nobody has a factory value for falls through to the caller's own default.
        Assert.Equal("C", service.Resolve("YutyRoarThatDoesNotExist", "C"));
        Assert.False(service.Status.InputIniFound);
    }

    [Fact]
    public void AFileThatVanishesFallsBackInsteadOfKeepingStaleKeys()
    {
        WriteInputIni("ActionMappings=(ActionName=\"AccessInventory\",Key=Y,bShift=False)");
        var service = Service();
        Assert.Equal("Y", service.Resolve(ArkActions.AccessInventory, "F"));

        File.Delete(InputIniPath);
        service.RefreshIfStale();

        Assert.Equal(ArkKeyBindingParser.StockBindings[ArkActions.AccessInventory],
            service.Resolve(ArkActions.AccessInventory, "F"));
    }

    // ─── What the page says ────────────────────────────────────────────────────

    [Fact]
    public void TheStatusCountsOnlyTheActionsTheScriptsActuallyPress()
    {
        WriteInputIni(
            // Two moved off their factory key…
            "ActionMappings=(ActionName=\"AccessInventory\",Key=Y,bShift=False)",
            "ActionMappings=(ActionName=\"TransferItem\",Key=G,bShift=False)",
            // …one written out but left where ARK put it…
            "ActionMappings=(ActionName=\"Use\",Key=" + ArkKeyBindingParser.StockBindings[ArkActions.Use] + ",bShift=False)",
            // …and two no script ever presses. Craft All is the trap: rebinding ARK's own craft
            // hotkey is an ordinary thing to do and has nothing to do with any script, so counting
            // it would tell a player their scripts follow a binding of theirs when none of them do.
            "ActionMappings=(ActionName=\"CraftAll\",Key=Z,bShift=False)",
            "ActionMappings=(ActionName=\"Crouch\",Key=LeftAlt,bShift=False)");

        var status = Service().Status;

        Assert.True(status.InputIniFound);
        Assert.Equal(2, status.CustomBindingCount);
    }

    // ─── A start that arrives twice ────────────────────────────────────────────

    /// <summary>
    /// The keys are re-resolved once per run, behind the running check. Both start paths can fire
    /// at the same moment — the global hotkey on the native message-pump thread and the Start
    /// button on the page — and the run loops read those key properties on every tick without a
    /// lock, so a second start allowed through to OnStarting would swap the key under a run that
    /// is already pressing it.
    /// </summary>
    [Fact]
    public void AStartOnAScriptAlreadyRunningDoesNotReResolveItsKeys()
    {
        using var script = new StartCountingScript();

        Assert.True(script.Start());
        Assert.True(script.Start());

        Assert.Equal(1, script.KeyResolves);
        script.Stop();
    }

    // ─── Fed Suit, the macro that used to ignore all of this ───────────────────

    [Fact]
    public void FedSuitOpensAndTransfersWithTheKeysThePlayerBound()
    {
        WriteInputIni(
            "ActionMappings=(ActionName=\"AccessInventory\",Key=Y,bShift=False)",
            "ActionMappings=(ActionName=\"TransferItem\",Key=G,bShift=False)");

        using var scan = UseScan(Service());
        var settings = new FedSuitSettings();

        Assert.Equal("Y", settings.OpenKey);
        Assert.Equal("G", settings.TransferKey);
    }

    [Fact]
    public void FedSuitsExitKeyIsNotScannedBecauseEscapeIsNotAnArkAction()
    {
        // Escape belongs to the engine, not to Input.ini. Writing one in must change nothing.
        WriteInputIni("ActionMappings=(ActionName=\"AccessInventory\",Key=Y,bShift=False)");

        using var scan = UseScan(Service());

        Assert.Equal("Esc", new FedSuitSettings().ExitKey);
    }

    [Fact]
    public void FedSuitFallsBackToItsOwnDefaultsWithNothingToScan()
    {
        using var scan = UseScan(null);
        var settings = new FedSuitSettings();

        Assert.Equal("F", settings.OpenKey);
        Assert.Equal("T", settings.TransferKey);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private string InputIniPath =>
        Path.Combine(_arkRoot, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "Input.ini");

    private void WriteInputIni(params string[] lines)
    {
        var path = InputIniPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // The staleness check compares last-write times, and NTFS timestamps are coarse enough
        // that two writes in the same test can land on the same tick. Stamping it forward makes
        // "the file changed" a fact rather than a race.
        File.WriteAllLines(path, new[] { "[/Script/Engine.InputSettings]" }.Concat(lines));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 10_000)));
    }

    private ArkKeyBindingService Service() =>
        new(new FakeArkPathProvider(_arkRoot), NullLogger<ArkKeyBindingService>.Instance);

    /// <summary>
    /// Points <see cref="ArkKeyDefaults"/> at a service for the length of one test. There is no
    /// MAUI container in here, so without this every default resolves to its hard-coded fallback
    /// and the Fed Suit tests below would pass on a scan that never ran.
    /// </summary>
    private static IDisposable UseScan(IArkKeyBindingService? service)
    {
        ArkKeyDefaults.ResolveService = () => service;
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => ArkKeyDefaults.ResolveService = ArkKeyDefaults.DefaultResolver;
    }

    /// <summary>A script whose only job is to count how often the scaffold re-resolved its keys.</summary>
    private sealed class StartCountingScript : AutomationScriptBase
    {
        public StartCountingScript()
            : base("test-arkkeys-start", "Key Re-resolve", string.Empty,
                   new FakeForegroundGate(gameIsForeground: true),
                   new NullAutomationHotkeyService(),
                   new RecordingNotificationService(),
                   new RecordingActivityService(),
                   new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
                   NullLogger.Instance)
        {
        }

        public int KeyResolves { get; private set; }

        protected override void OnStarting() => KeyResolves++;

        protected override Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>An ARK install wherever the test says it is, including nowhere.</summary>
    private sealed class FakeArkPathProvider(string? arkRoot) : IArkPathProvider
    {
        public string? ArkRoot { get; set; } = arkRoot;

        /// <summary>How often the install was searched for — the expensive half of a scan.</summary>
        public int Lookups { get; private set; }

        public string? FindArkPath()
        {
            Lookups++;
            return ArkRoot;
        }

        public string? GetBaseDeviceProfilesPath() => null;

        public bool IsValidArkPath(string path) => !string.IsNullOrWhiteSpace(path);
    }
}
