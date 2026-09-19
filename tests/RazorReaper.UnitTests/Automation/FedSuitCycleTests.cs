using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// What one Fed-Suit cycle is made of.
///
/// The old one pressed the transfer key twenty times wherever the cursor happened to be, with
/// the transmitter's own tab in front — so it moved nothing, forever, while the tile counted
/// cycles. Everything here is about that: the player's tab gets clicked, each worn piece gets
/// the cursor before it gets the key, and a run that moves nothing gives up instead of looping.
///
/// Shares <c>ArkKeyDefaults</c> with <see cref="ArkKeyScanTests"/>: the macro rescans its open
/// and transfer keys through a process-wide static that those tests swap out.
/// </summary>
[Collection("ArkKeyDefaults")]
public sealed class FedSuitCycleTests
{
    private static readonly Rectangle FullHd = new(0, 0, 1920, 1080);

    [Fact]
    public async Task ThePlayerTabIsClickedBeforeAnythingIsTransferred()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine);

        var steps = await StartedSteps(macro, engine);

        // Before the first cursor move, and after the key that opens the transmitter.
        var click = steps.Single(s => s.Type == MacroStepType.ClickAt);
        var tab = ArkInventoryLayout.PlayerTab(FullHd, 1.0);
        Assert.Equal(tab, new Point(click.X, click.Y));
        Assert.True(steps.IndexOf(click) < steps.FindIndex(s => s.Type == MacroStepType.MoveTo));

        macro.Stop();
    }

    [Fact]
    public async Task EveryWornPieceGetsTheCursorAndThenTheTransferKey()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine);

        var steps = await StartedSteps(macro, engine);
        var slots = ArkInventoryLayout.ArmorSlots(FullHd, 1.0);
        var first = steps.FindIndex(s => s.Type == MacroStepType.MoveTo);

        for (var i = 0; i < slots.Length; i++)
        {
            var move = steps[first + (i * 4)];
            Assert.Equal(MacroStepType.MoveTo, move.Type);
            Assert.Equal(slots[i], new Point(move.X, move.Y));

            // Hover settle, then the press, then the per-slot delay.
            Assert.Equal(MacroStepType.Delay, steps[first + (i * 4) + 1].Type);
            Assert.Equal(MacroStepType.KeyPress, steps[first + (i * 4) + 2].Type);
            Assert.Equal(MacroStepType.Delay, steps[first + (i * 4) + 3].Type);
        }

        // Open, five transfers, exit — and nothing aimed at the offhand slot between them.
        Assert.Equal(7, steps.Count(s => s.Type == MacroStepType.KeyPress));
        Assert.DoesNotContain(steps, s => s.Type == MacroStepType.MoveTo && s.Y > slots[2].Y);

        macro.Stop();
    }

    [Fact]
    public async Task RunsIsHowManySuitsItStopsAfter()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine);

        var settings = macro.Settings;
        settings.Runs = 7;
        macro.UpdateSettings(settings);

        await StartedSteps(macro, engine);

        Assert.Equal(7, engine.Runner("fed-suit").LastSequence!.RepeatCount);
        macro.Stop();
    }

    /// <summary>Zero is the default and means the loop runs until somebody stops it.</summary>
    [Fact]
    public async Task ZeroRunsKeepsGoing()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine);

        await StartedSteps(macro, engine);

        Assert.Equal(0, engine.Runner("fed-suit").LastSequence!.RepeatCount);
        macro.Stop();
    }

    [Fact]
    public async Task ThreeCyclesThatMoveNothingStopTheRun()
    {
        var engine = new FakeMacroEngine();
        var notifications = new RecordingNotificationService();
        // The fake's captures are all zeroes unless it is told otherwise: a screen that never moves.
        using var macro = Macro(engine, notifications, new FakeScreenSampler());

        await RunCycles(macro, engine, 3);

        await WaitUntil(() => !macro.IsRunning);
        Assert.Contains(notifications.Toasts, t => t.Level == "warning" && t.Message.Contains("moved nothing"));
    }

    [Fact]
    public async Task ARunWhoseSlotsKeepChangingIsLeftAlone()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine, sampler: new FakeScreenSampler { CapturesDiffer = true });

        await RunCycles(macro, engine, 5);

        Assert.True(macro.IsRunning);
        macro.Stop();
    }

    /// <summary>Starts the macro and returns the steps it handed the runner.</summary>
    private static async Task<List<MacroStep>> StartedSteps(FedSuitMacro macro, FakeMacroEngine engine)
    {
        Assert.True(macro.Start(alreadyMetered: true));
        await WaitUntil(() => engine.Runner("fed-suit").LastSequence is not null);
        return engine.Runner("fed-suit").LastSequence!.Steps;
    }

    /// <summary>
    /// Plays the two steps the slot check hangs off — the first cursor move and the exit press —
    /// for as many cycles as asked. The fake runner presses nothing itself.
    /// </summary>
    private static async Task RunCycles(FedSuitMacro macro, FakeMacroEngine engine, int cycles)
    {
        var steps = await StartedSteps(macro, engine);
        var runner = engine.Runner("fed-suit");
        var firstTransfer = steps.FindIndex(s => s.Type == MacroStepType.MoveTo);

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            runner.FireStep(firstTransfer, cycle);
            runner.FireStep(steps.Count - 1, cycle);
        }
    }

    private static FedSuitMacro Macro(
        IMacroEngine engine,
        INotificationService? notifications = null,
        IScreenSampler? sampler = null)
        => new(
            engine,
            new FakeGameDisplayService(),
            new FakeArkInstall(),
            sampler ?? new FakeScreenSampler(),
            notifications ?? new RecordingNotificationService(),
            new RecordingActivityService(),
            new CountingUsageGateService(),
            new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
            NullLogger<FedSuitMacro>.Instance);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Fed Suit did not reach the expected state within 10 s.");
    }
}
