using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The Turret Filler sends the transfer key at whatever the cursor is hovering, so every one of
/// its refusals is a press that did not go into the world instead. What it can check is thin —
/// a calibrated rectangle is open, and pixels inside it moved between two presses — and the two
/// modes read that same signal differently: Fill stops at the first press the game swallowed,
/// Even split gives one press back to lag first. Both are pinned here, along with the maths under
/// them, because "it kept pressing into a full turret" and "it stopped after one" are the two
/// complaints this script can produce.
/// </summary>
public sealed class TurretFillerTests
{
    private const string RegionKey = "turretfill-region";
    private const int VkTransfer = 'T';
    private const int VkShift = 0x10;

    // ─── Refusals ──────────────────────────────────────────────────────────────

    /// <summary>Inherited from the base: nothing to match against is nothing to act on.</summary>
    [Fact]
    public void WithoutARegionAndAReferenceItRefusesToStart()
    {
        var (script, _, _, _, _) = Filler(withRegion: false, withReference: false);

        Assert.False(script.Start());
        Assert.False(script.IsRunning);

        script.Dispose();
    }

    [Fact]
    public void WithTheFilterOnAndNoPointsCapturedItRefusesToStart()
    {
        var (script, calibration, _, _, toasts) = Filler();
        script.UseFilter = true;

        Assert.False(script.Start());

        // One point is not both: the run clicks the search box and then the slot.
        calibration.SetPoint(TurretFillerScript.SearchPointName, new Point(400, 200));
        Assert.False(script.Start());

        calibration.SetPoint(TurretFillerScript.SlotPointName, new Point(420, 260));
        Assert.True(script.Start());

        Assert.Contains(toasts.Toasts, t => t.Message.Contains("Filter is on", StringComparison.Ordinal));

        script.Dispose();
    }

    /// <summary>
    /// Started from the tile with the game behind the panel, the transfer key would land in
    /// whatever window is in front. This one is hotkey-driven by nature, so refusing is the
    /// honest answer rather than a limitation.
    /// </summary>
    [Fact]
    public void WithArkBehindThePanelItRefusesToStart()
    {
        var (script, _, _, _, _) = Filler(gameIsForeground: false);

        Assert.False(script.Start());
        Assert.False(script.IsRunning);

        script.Dispose();
    }

    // ─── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The only thing between a mistimed hotkey and a transfer key going into the world. The
    /// region is calibrated, the inventory is simply not open.
    /// </summary>
    [Fact]
    public async Task WithTheRegionNotMatchingNoKeyIsSent()
    {
        var (script, _, sampler, input, _) = Filler();
        sampler.TargetVisible = false;

        Assert.True(script.Start());
        await Finished(script);

        Assert.Empty(input.Events);

        script.Dispose();
    }

    [Fact]
    public async Task EvenSplitGivesOnePressBackToLagAndThenStops()
    {
        var (script, _, _, input, _) = Filler();
        script.Mode = TurretFillMode.EvenSplit;
        script.PressesPerTurret = 10;

        Assert.True(script.Start());
        await Finished(script);

        Assert.Equal(2, Presses(input));

        script.Dispose();
    }

    [Fact]
    public async Task FillStopsAtTheFirstPressThatMovedNothing()
    {
        var (script, _, _, input, _) = Filler();
        script.Mode = TurretFillMode.Fill;
        script.PressesPerTurret = 10;

        Assert.True(script.Start());
        await Finished(script);

        Assert.Equal(1, Presses(input));

        script.Dispose();
    }

    /// <summary>
    /// The press cap holds in both modes, and a press that moved something is an effect — which
    /// is what the page reads to tell a working run from a silent one.
    /// </summary>
    [Fact]
    public async Task PressesThatMoveSomethingAreCountedAndCappedAtTheSetting()
    {
        var (script, _, sampler, input, _) = Filler();
        sampler.CapturesDiffer = true;
        script.Mode = TurretFillMode.Fill;
        script.PressesPerTurret = 3;

        Assert.True(script.Start());
        await Finished(script);

        Assert.Equal(3, Presses(input));
        Assert.Equal(3, script.EffectCount);

        script.Dispose();
    }

    /// <summary>Half a stack is Shift held across the press, not a key of its own.</summary>
    [Fact]
    public async Task HalfAStackHoldsShiftAroundThePressAndReleasesIt()
    {
        var (script, _, _, input, _) = Filler();
        script.Amount = TurretFillAmount.HalfStack;

        Assert.True(script.Start());
        await Finished(script);

        var sent = input.Events.Where(e => e is not SimulatedInput.Delay).ToArray();
        Assert.Equal(new SimulatedInput.KeyDown(VkShift), sent[0]);
        Assert.Equal(new SimulatedInput.KeyPress(VkTransfer, 40), sent[1]);
        Assert.Equal(new SimulatedInput.KeyUp(VkShift), sent[2]);
        Assert.Empty(input.HeldKeys);

        script.Dispose();
    }

    // ─── The maths under it ────────────────────────────────────────────────────

    [Fact]
    public void TwoIdenticalCapturesScoreZero()
    {
        Assert.Equal(0, TurretFillerScript.ChangedPercent(Frame(40, 40), Frame(40, 40)));
    }

    /// <summary>
    /// A capture that failed, or one taken after the window moved, is not evidence of anything —
    /// least of all of a successful transfer.
    /// </summary>
    [Fact]
    public void CapturesOfDifferentSizesOrNoPixelsAtAllScoreZero()
    {
        Assert.Equal(0, TurretFillerScript.ChangedPercent(Frame(40, 40), Frame(40, 41)));
        Assert.Equal(0, TurretFillerScript.ChangedPercent(Frame(40, 40), new ScreenCapture(0, 0, [])));
        Assert.Equal(0, TurretFillerScript.ChangedPercent(new ScreenCapture(0, 0, []), new ScreenCapture(0, 0, [])));
    }

    /// <summary>
    /// The reason this counts pixels instead of averaging them. One stack leaving a slot is a
    /// small bright patch in a rectangle that is otherwise frame and background: as a share of
    /// pixels it clears the default threshold with room to spare, and as a mean over the whole
    /// rectangle it is far under any tolerance a snapshot comparison would use.
    /// </summary>
    [Fact]
    public void ASmallPatchOfMovedPixelsClearsTheThresholdAMeanWouldNeverReach()
    {
        var (script, _, _, _, _) = Filler();
        var before = Frame(100, 100);
        var after = Frame(100, 100);
        for (var i = 0; i < 40; i++) Array.Fill(after.Bgra, byte.MaxValue, i * 4, 4);

        var changed = TurretFillerScript.ChangedPercent(before, after);
        Assert.Equal(0.4, changed, 3);
        Assert.True(changed >= script.ChangeThresholdPercent,
            $"{changed:0.00}% of the region moved and the default threshold of {script.ChangeThresholdPercent} still missed it");

        var mean = after.Bgra.Select((b, i) => Math.Abs(b - before.Bgra[i])).Average();
        Assert.True(mean < 2, $"a mean of {mean:0.00} would have been written off as a still frame");

        script.Dispose();
    }

    // ─── Settings ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Clamped on the way in as well as on the way out: a preference file edited by hand, or
    /// written by an older build, must not hand the run a 5000-press loop or a threshold no
    /// transfer can clear.
    /// </summary>
    [Fact]
    public void SavingSettingsClampsThemBackIntoRange()
    {
        var (script, _, _, _, _) = Filler();

        script.PressesPerTurret = 999;
        script.PressDelayMs = 5;
        script.MatchThresholdPercent = 120;
        script.ChangeThresholdPercent = 0;
        script.FilterSettleMs = 30;
        script.TransferKey = "  ";
        script.FilterText = new string('x', 60);
        script.SaveSettings();

        Assert.Equal(20, script.PressesPerTurret);
        Assert.Equal(60, script.PressDelayMs);
        Assert.Equal(100, script.MatchThresholdPercent);
        Assert.Equal(0.02, script.ChangeThresholdPercent);
        Assert.Equal(100, script.FilterSettleMs);
        Assert.Equal("T", script.TransferKey);
        Assert.Equal(40, script.FilterText.Length);

        script.Dispose();
    }

    // ─── Harness ───────────────────────────────────────────────────────────────

    private static int Presses(RecordingInputSimulator input)
        => input.Events.Count(e => e is SimulatedInput.KeyPress press && press.VirtualKey == VkTransfer);

    private static ScreenCapture Frame(int width, int height) => new(width, height, new byte[width * height * 4]);

    private static async Task Finished(AutomationScriptBase script)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!script.IsRunning) return;
            await Task.Delay(10);
        }

        Assert.Fail("the one-shot never finished");
    }

    private static (TurretFillerScript Script, FakeCalibrationService Calibration, FakeScreenSampler Sampler,
        RecordingInputSimulator Input, RecordingNotificationService Toasts)
        Filler(bool withRegion = true, bool withReference = true, bool gameIsForeground = true)
    {
        var sampler = new FakeScreenSampler { TargetVisible = true };
        var calibration = new FakeCalibrationService();
        var input = new RecordingInputSimulator();
        var toasts = new RecordingNotificationService();

        if (withRegion) calibration.SetRegion(RegionKey, Rectangle.FromLTRB(600, 300, 900, 500));
        if (withReference) sampler.References.Add(RegionKey);

        var script = new TurretFillerScript(
            input,
            sampler,
            calibration,
            new FakeForegroundGate(gameIsForeground),
            new NullAutomationHotkeyService(),
            toasts,
            new RecordingActivityService(),
            new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
            NullLogger<TurretFillerScript>.Instance);

        return (script, calibration, sampler, input, toasts);
    }
}
