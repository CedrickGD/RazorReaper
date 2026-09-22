using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The catalogue presented sixteen scripts as sixteen equals. Five of them send their sequence on
/// fixed delays and never look at what came back: Astro, Turret Manager, Auto Download, Fast TP
/// and Crafting in Walk mode. They work when the game and the server keep up, and when those lag
/// they send exactly the same keys, change nothing, and report a clean run. From the page there
/// was no way to tell which kind of script you had picked until you had watched one fail.
/// Turret Manager has since learned to read the turret it fills, and stays marked until that
/// reading is proven on a real turret.
///
/// Both halves are pinned: which scripts declare it, and that the page actually renders the chip
/// and the line. A flag nothing draws is the same silence as no flag at all.
/// </summary>
public sealed class ScriptCatalogueHonestyTests
{
    [Fact]
    public void TheFiveScriptsThatCannotVerifyTheirOwnWorkSaySo()
    {
        using var astro = Astro();
        using var turret = Turret();
        using var download = Download();
        using var fastTp = FastTp();

        Assert.True(astro.IsExperimental);
        Assert.True(turret.IsExperimental);
        Assert.True(download.IsExperimental);
        Assert.True(fastTp.IsExperimental);
    }

    /// <summary>
    /// Crafting is the one that is both. Watcher waits until it can see the station inventory
    /// before it crafts; Walk holds forward for a fixed number of milliseconds and presses where
    /// it hopes the next station is. Marking the whole script would be as wrong as marking none
    /// of it, so the flag follows the mode.
    /// </summary>
    [Fact]
    public void CraftingIsExperimentalOnlyInWalkMode()
    {
        using var crafting = Crafting();

        crafting.Mode = CraftingMode.Watcher;
        Assert.False(crafting.IsExperimental);

        crafting.Mode = CraftingMode.Walk;
        Assert.True(crafting.IsExperimental);
    }

    /// <summary>
    /// And the ones that do verify are not tarred with it. Take All clicks only while it can see
    /// the button; a chip on that would make the word mean nothing.
    /// </summary>
    [Fact]
    public void AScriptThatWatchesTheScreenIsNotMarkedExperimental()
    {
        using var takeAll = TakeAll();

        Assert.False(takeAll.IsExperimental);
    }

    [Fact]
    public void TheScriptsPageDrawsTheChipAndTheLineBehindIt()
    {
        var page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Components", "Pages", "Scripts.razor"));

        Assert.Contains("_selected.IsExperimental", page, StringComparison.Ordinal);
        Assert.Contains("scripts-exp-chip", page, StringComparison.Ordinal);
        Assert.Contains(@"Localizer.T(""scripts.experimental"")", page, StringComparison.Ordinal);

        // The sentence, not only the tooltip: a caveat nobody hovers is a caveat nobody reads.
        Assert.Contains("script-experimental-line", page, StringComparison.Ordinal);
        Assert.Contains(@"Localizer.T(""scripts.experimental.note"")", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared calibration card has to carry three things the five scripts on
    /// <see cref="CalibratableScriptBase"/> went without: the live similarity the threshold is
    /// set against, the display the reference belongs to, and the threshold itself, which stays.
    /// </summary>
    [Fact]
    public void TheSharedCalibrationCardCarriesTheLiveMatchAndTheDisplayLine()
    {
        var page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Components", "Pages", "Scripts.razor"));

        Assert.Contains("cal.CurrentSimilarityPercent", page, StringComparison.Ordinal);
        Assert.Contains(@"Localizer.T(""scripts.cal.livematch"")", page, StringComparison.Ordinal);

        Assert.Contains("MonitorLine(cal.Monitor)", page, StringComparison.Ordinal);
        Assert.Contains(@"Localizer.T(""scripts.cal.monitor.title"")", page, StringComparison.Ordinal);
        Assert.Contains("scripts-cal-mismatch", page, StringComparison.Ordinal);

        // Still there. The number beside it is what makes it settable rather than guessable —
        // it was never meant to replace it.
        Assert.Contains(@"Localizer.T(""scripts.field.matchthreshold"")", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same honesty, one card down. Those three rows are only true of a script that compares
    /// a captured snapshot, and two of the seven on this base never do: Armor Swap reads its
    /// region as text, and Dino Ready only clicks the middle of it.
    ///
    /// Armor Swap said so from the start. Dino Ready inherited the default and got the whole
    /// workflow — a capture button that changed nothing it does, and worse, a live-match
    /// percentage scored against a snapshot no run of it ever reads and a mismatch banner over a
    /// calibration that moving screens cannot invalidate. Only Dino Ready is built here: Armor
    /// Swap needs an OCR engine to construct, and its flag is not the one that regressed.
    /// </summary>
    [Fact]
    public void AScriptThatNeverComparesASnapshotIsNotOfferedOne()
    {
        using var dino = DinoReady();
        using var takeAll = TakeAll();

        Assert.False(((ICalibratableScript)dino).UsesReference);

        // And the ones that do compare keep all of it — a flag that said "no" everywhere would
        // buy the honesty by deleting the feature.
        Assert.True(((ICalibratableScript)takeAll).UsesReference);
    }

    /// <summary>
    /// And the page has to honour the flag for the rows that answer "is the snapshot matching?",
    /// not only for the buttons that capture one. They sit inside the gate; a percentage and a
    /// monitor banner outside it would be the same lie rendered one indent to the left.
    /// </summary>
    [Fact]
    public void TheLiveMatchAndDisplayRowsSitBehindTheReferenceFlag()
    {
        var page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Components", "Pages", "Scripts.razor"));

        var gate = page.IndexOf("cal.UsesReference", StringComparison.Ordinal);
        Assert.True(gate >= 0, "the calibration card no longer asks whether the script uses a reference");

        Assert.True(page.IndexOf("cal.CurrentSimilarityPercent", StringComparison.Ordinal) > gate,
            "the live-match row is drawn for scripts that never match anything");
        Assert.True(page.IndexOf("MonitorLine(cal.Monitor)", StringComparison.Ordinal) > gate,
            "the monitor row is drawn for scripts with no snapshot to invalidate");
    }

    /// <summary>The mismatch row needs a style, or it renders as an ordinary row saying nothing.</summary>
    [Fact]
    public void TheChipAndTheMismatchRowAreStyled()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages", "scripts-styles.css"));

        Assert.Contains(".scripts-exp-chip", css, StringComparison.Ordinal);
        Assert.Contains(".script-experimental-line", css, StringComparison.Ordinal);
        Assert.Contains("scripts-cal-mismatch", css, StringComparison.Ordinal);
    }

    // ─── Harness ───────────────────────────────────────────────────────────────

    private static ILocalizer English()
        => new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

    private static AstroScript Astro() => new(
        new RecordingInputSimulator(), Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<AstroScript>.Instance);

    private static AutoDownloadScript Download() => new(
        new RecordingInputSimulator(), Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<AutoDownloadScript>.Instance);

    private static FastTpScript FastTp() => new(
        new RecordingInputSimulator(), Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<FastTpScript>.Instance);

    private static TurretManagerScript Turret() => new(
        new RecordingInputSimulator(), new FakeScreenSampler(), new FakeGameDisplayService(), new FakeArkPathProvider(),
        Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<TurretManagerScript>.Instance);

    private static CraftingScript Crafting() => new(
        new RecordingInputSimulator(), new FakeScreenSampler(), new FakeCalibrationService(),
        Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<CraftingScript>.Instance);

    private static TakeAllScript TakeAll() => new(
        new RecordingInputSimulator(), new FakeScreenSampler(), new FakeCalibrationService(),
        Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<TakeAllScript>.Instance);

    private static DinoReadyScript DinoReady() => new(
        new RecordingInputSimulator(), new FakeScreenSampler(), new FakeCalibrationService(),
        Gate(), Hotkeys(), Toasts(), Activity(), English(),
        NullLogger<DinoReadyScript>.Instance);

    private static FakeForegroundGate Gate() => new(gameIsForeground: false);

    private static NullAutomationHotkeyService Hotkeys() => new();

    private static RecordingNotificationService Toasts() => new();

    private static RecordingActivityService Activity() => new();

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
