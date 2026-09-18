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
        new RecordingInputSimulator(), new FakeScreenSampler(), new FakeCalibrationService(),
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

    private static FakeForegroundGate Gate() => new(gameIsForeground: false);

    private static NullAutomationHotkeyService Hotkeys() => new();

    private static RecordingNotificationService Toasts() => new();

    private static RecordingActivityService Activity() => new();

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
