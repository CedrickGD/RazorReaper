using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// "Running" was the only thing the Scripts page could say, and it was the one thing that never
/// answered the question. A script whose foreground gate is shut ticks nothing, sends nothing and
/// still shows a green dot — which is indistinguishable from a script that is scanning and
/// finding nothing, and from a script that is simply broken. Every report of "it does nothing"
/// starts in that gap.
///
/// So a run now counts its effects: the moments it actually sent input or issued a command, not
/// the ticks it spent looking. Zero of them after half a minute, with the gate shut, is a
/// specific thing with a one-keystroke fix — and it gets said once.
/// </summary>
public sealed class ScriptEffectTests
{
    [Fact]
    public async Task ARunThatActsCountsWhatItDid()
    {
        using var script = new ActingScript(gameIsForeground: true);

        Assert.Null(script.SinceLastEffect);
        Assert.Equal(0, script.EffectCount);

        Assert.True(script.Start());
        await WaitUntil(() => script.EffectCount >= 3);
        script.Stop();

        Assert.NotNull(script.SinceLastEffect);
        Assert.True(script.SinceLastEffect!.Value < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The state the page had no words for: the loop is alive, the gate is shut, and nothing has
    /// reached the game. The counter has to stay at zero — a tick is not an effect.
    /// </summary>
    [Fact]
    public async Task ARunTheGateHeldShutCountsNothing()
    {
        using var script = new ActingScript(gameIsForeground: false);

        Assert.True(script.Start());
        await WaitUntil(() => script.Gate.TimesAsked >= 5);

        Assert.Equal(0, script.EffectCount);
        Assert.Null(script.SinceLastEffect);

        script.Stop();
    }

    /// <summary>A second run starts from nothing: last run's effects are not this run's.</summary>
    [Fact]
    public async Task ARestartStartsTheCountAgain()
    {
        using var script = new ActingScript(gameIsForeground: true);

        Assert.True(script.Start());
        await WaitUntil(() => script.EffectCount >= 3);
        script.Stop();

        script.Gate.GameIsForeground = false;
        var asked = script.Gate.TimesAsked;

        Assert.True(script.Start());
        await WaitUntil(() => script.Gate.TimesAsked >= asked + 5);

        Assert.Equal(0, script.EffectCount);
        Assert.Null(script.SinceLastEffect);

        script.Stop();
    }

    [Fact]
    public async Task AScriptThatHasNeverActedWithTheGateShutSaysSo()
    {
        using var script = new ActingScript(gameIsForeground: false) { SilentRunWarningMs = 60 };

        Assert.True(script.Start());
        await WaitUntil(() => script.Warnings.Count == 1);

        // Long enough for several more windows to have passed. The state does not change from
        // tick to tick, so repeating the sentence would mean a toast every interval for as long
        // as the window stays where it is.
        await Task.Delay(400);
        Assert.Single(script.Warnings);

        script.Stop();
    }

    /// <summary>The gate is open and input is flowing — there is nothing to warn about.</summary>
    [Fact]
    public async Task AScriptThatIsActingIsNotWarned()
    {
        using var script = new ActingScript(gameIsForeground: true) { SilentRunWarningMs = 60 };

        Assert.True(script.Start());
        await WaitUntil(() => script.EffectCount >= 3);
        await Task.Delay(300);

        Assert.Empty(script.Warnings);

        script.Stop();
    }

    /// <summary>
    /// The gate is open and the script has still done nothing — it is looking and finding
    /// nothing, which is a different sentence and not one to interrupt anybody with.
    /// </summary>
    [Fact]
    public async Task AScriptThatIsLookingAndFindingNothingIsNotWarned()
    {
        using var script = new ActingScript(gameIsForeground: true, act: false) { SilentRunWarningMs = 60 };

        Assert.True(script.Start());
        await WaitUntil(() => script.Gate.TimesAsked >= 5);
        await Task.Delay(300);

        Assert.Equal(0, script.EffectCount);
        Assert.Empty(script.Warnings);

        script.Stop();
    }

    /// <summary>A run that ends inside the window takes its warning with it.</summary>
    [Fact]
    public async Task AScriptStoppedInsideTheWindowSaysNothing()
    {
        using var script = new ActingScript(gameIsForeground: false) { SilentRunWarningMs = 5_000 };

        Assert.True(script.Start());
        script.Stop();
        await Task.Delay(200);

        Assert.Empty(script.Warnings);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("the condition was never met");
    }

    /// <summary>
    /// A script that is nothing but the shared scan loop and one effect per tick — the shape
    /// every input-only script in the catalogue has.
    /// </summary>
    private sealed class ActingScript : AutomationScriptBase
    {
        private readonly RecordingNotificationService _toasts;
        private readonly bool _act;

        public ActingScript(bool gameIsForeground, bool act = true)
            : this(new FakeForegroundGate(gameIsForeground), new RecordingNotificationService(), act)
        {
        }

        private ActingScript(FakeForegroundGate gate, RecordingNotificationService toasts, bool act)
            : base("test-effects", "Effects", string.Empty,
                   gate,
                   new NullAutomationHotkeyService(),
                   toasts,
                   new RecordingActivityService(),
                   new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
                   NullLogger.Instance)
        {
            Gate = gate;
            _toasts = toasts;
            _act = act;
        }

        public FakeForegroundGate Gate { get; }

        /// <summary>
        /// Warnings only. With no hotkey bound and vision set, the silent-run notice is the
        /// only warning this scaffold can raise, so the level is the whole filter and the test
        /// does not depend on the wording of a dictionary entry.
        /// </summary>
        public IReadOnlyList<RecordingNotificationService.Toast> Warnings
            => _toasts.Toasts.Where(t => t.Level == "warning").ToArray();

        /// <summary>Vision, so the shared input quota stays out of a test about effects.</summary>
        public override bool UsesVision => true;

        protected override Task RunAsync(CancellationToken ct)
            => RunLoopAsync(10, _ =>
            {
                if (_act) ReportEffect();
                return Task.CompletedTask;
            }, foregroundOnly: true, ct);
    }
}
