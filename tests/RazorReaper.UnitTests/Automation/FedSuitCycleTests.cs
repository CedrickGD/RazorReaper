using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
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

    /// <summary>
    /// The timings the first in-game run was lost on. At the frame rate an open ARK inventory
    /// actually runs at — 30 to 40, not 60 — a 35 ms hover and a 40 ms press delay put the key
    /// in before the game had registered the slot under the cursor, and the legs stayed on.
    /// </summary>
    [Fact]
    public async Task EverySlotGetsALongEnoughHoverAndPressDelayForThirtyFps()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine);

        var steps = await StartedSteps(macro, engine);
        var first = steps.FindIndex(s => s.Type == MacroStepType.MoveTo);

        Assert.Equal(100, steps[first - 1].DelayMs); // the tab settle, right before the slots
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(80, steps[first + (i * 4) + 1].DelayMs); // hover settle
            Assert.Equal(70, steps[first + (i * 4) + 3].DelayMs); // press delay
        }

        macro.Stop();
    }

    /// <summary>
    /// An open key that changed nothing on screen means no inventory opened — out of reach, a
    /// lagging server, or a key that found nothing to open. Everything after this step is clicks
    /// and cursor jumps that land in gameplay: the camera swings to the sky and the character
    /// punches. So the run ends here, before the tab click.
    /// </summary>
    [Fact]
    public async Task AnOpenKeyThatChangedNothingStopsTheCycleBeforeTheTabClick()
    {
        var engine = new FakeMacroEngine();
        var notifications = new RecordingNotificationService();
        // The fake's captures are all zeroes unless it is told otherwise: a screen the key did not change.
        using var macro = Macro(engine, notifications, new FakeScreenSampler());

        await OpenAndTab(macro, engine);

        await WaitUntil(() => !macro.IsRunning);
        Assert.Contains(notifications.Toasts, t => t.Level == "warning" && t.Message.Contains("did not open"));
    }

    [Fact]
    public async Task AnInventoryThatReallyOpenedIsLeftAlone()
    {
        var engine = new FakeMacroEngine();
        using var macro = Macro(engine, sampler: new FakeScreenSampler { CapturesDiffer = true });

        await OpenAndTab(macro, engine);

        Assert.True(macro.IsRunning);
        macro.Stop();
    }

    /// <summary>
    /// The half of that guard which lives in the engine: a subscriber that stops from the step
    /// callback must keep that step from running. Without it the check above would stop a cycle
    /// whose tab click had already been fired into the game.
    /// </summary>
    [Fact]
    public async Task AStepTheSubscriberStoppedAtIsNeverExecuted()
    {
        var input = new RecordingInputSimulator();
        var engine = new MacroEngine(
            input,
            new NoProcesses(),
            Options.Create(new AppConfiguration()),
            new RecordingActivityService(),
            new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
            NullLogger<MacroEngine>.Instance);

        var runner = engine.GetRunner("stop-from-callback");
        runner.StepStarted += (step, _) => { if (step == 1) runner.Stop(); };

        var ran = await runner.RunAsync(new MacroSequence
        {
            Steps = [MacroStep.KeyPress(0x54), MacroStep.ClickAt(10, 20)],
            RepeatCount = 1
        });

        Assert.False(ran);
        Assert.Contains(input.Events, e => e is SimulatedInput.KeyPress);
        Assert.DoesNotContain(input.Events, e => e is SimulatedInput.Click);
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

    /// <summary>
    /// Plays the two steps the "did it open?" guard hangs off: the open key press, and the tab
    /// click it has to decide about before the click is sent.
    /// </summary>
    private static async Task OpenAndTab(FedSuitMacro macro, FakeMacroEngine engine)
    {
        var steps = await StartedSteps(macro, engine);
        var runner = engine.Runner("fed-suit");

        runner.FireStep(steps.FindIndex(s => s.Type == MacroStepType.KeyPress), 1);
        runner.FireStep(steps.FindIndex(s => s.Type == MacroStepType.ClickAt), 1);
    }

    /// <summary>The engine only looks at processes for a focus step, and these sequences have none.</summary>
    private sealed class NoProcesses : IProcessService
    {
        public System.Diagnostics.Process[] GetProcessesByName(string processName) => [];

        public bool IsProcessRunning(string processName) => false;

        public string? GetExecutablePath(System.Diagnostics.Process process) => null;

        public System.Diagnostics.Process? Start(string filePath) => null;

        public void Kill(System.Diagnostics.Process process) { }
    }

    private static FedSuitMacro Macro(
        IMacroEngine engine,
        INotificationService? notifications = null,
        IScreenSampler? sampler = null)
        => new(
            engine,
            new FakeGameDisplayService(),
            new FakeArkPathProvider(),
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
