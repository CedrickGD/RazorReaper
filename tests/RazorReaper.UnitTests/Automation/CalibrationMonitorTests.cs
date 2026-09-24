using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// A reference snapshot is a bitmap of one rectangle on one monitor, and until now nothing
/// recorded which monitor. Move ARK to the other screen, or change that screen's resolution, and
/// the comparison carries on against pixels that have nothing to do with what is on screen —
/// without failing. It produces a number, the number is noise, and noise clears a low threshold
/// as readily as a real match does.
///
/// Every display here is invented. The two-monitor case cannot be exercised on a build agent with
/// one screen, and one screen is exactly the setup that never had the problem.
/// </summary>
public sealed class CalibrationMonitorTests
{
    private const string RegionKey = "takeall-region";

    private static readonly AttachedDisplay MonitorOne =
        new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    private static readonly AttachedDisplay MonitorTwo =
        new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 2560, 1440), IsPrimary: false);

    // ─── The comparison itself ─────────────────────────────────────────────────

    [Fact]
    public void ADifferentMonitorIsAMismatch()
    {
        Assert.True(new CalibrationMonitorInfo(2, "2560x1440", 1, "1920x1080").Mismatch);
    }

    /// <summary>
    /// Same screen, different mode. A stretched-res preset or a borderless switch does this
    /// without the game ever leaving the monitor, and the stored bitmap is just as unusable.
    /// </summary>
    [Fact]
    public void TheSameMonitorAtADifferentResolutionIsAlsoAMismatch()
    {
        Assert.True(new CalibrationMonitorInfo(1, "1920x1080", 1, "1728x1080").Mismatch);
    }

    [Fact]
    public void TheSameMonitorAtTheSameResolutionIsNot()
    {
        Assert.False(new CalibrationMonitorInfo(2, "2560x1440", 2, "2560x1440").Mismatch);
    }

    // ─── What a script does with it ────────────────────────────────────────────

    [Fact]
    public void AReferenceFromAnotherScreenStopsTheScriptMatchingAndSaysWhy()
    {
        var (script, calibration, sampler, toasts) = Calibrated();

        // Captured on the second monitor; the game is on the first now.
        calibration.SetRegionMonitor(RegionKey, MonitorTwo.DeviceName, MonitorTwo.ResolutionKey);
        calibration.CurrentGameMonitor = MonitorOne;

        var monitor = ((ICalibratableScript)script).Monitor;
        Assert.NotNull(monitor);
        Assert.True(monitor!.Mismatch);
        Assert.Equal(2, monitor.StoredIndex);
        Assert.Equal("2560x1440", monitor.StoredResolution);
        Assert.Equal(1, monitor.CurrentIndex);
        Assert.Equal("1920x1080", monitor.CurrentResolution);

        // The sampler would happily report a perfect match; the script refuses to ask it.
        sampler.TargetVisible = true;
        Assert.Null(((ICalibratableScript)script).CurrentSimilarityPercent);

        // And the run never begins, because a run that cannot match is worse than a refusal:
        // "running" and silent is the state this whole wave exists to stop producing.
        Assert.False(script.Start());
        Assert.False(script.IsRunning);
        Assert.Contains(toasts.Toasts, t => t.Message.Contains("Monitor 2", StringComparison.Ordinal));

        script.Dispose();
    }

    [Fact]
    public void AReferenceFromThisScreenIsComparedAsNormal()
    {
        var (script, calibration, sampler, _) = Calibrated();

        calibration.SetRegionMonitor(RegionKey, MonitorOne.DeviceName, MonitorOne.ResolutionKey);
        calibration.CurrentGameMonitor = MonitorOne;
        sampler.TargetVisible = true;

        Assert.False(((ICalibratableScript)script).Monitor!.Mismatch);
        Assert.Equal(100, ((ICalibratableScript)script).CurrentSimilarityPercent);
        Assert.True(script.Start());

        script.Stop();
        script.Dispose();
    }

    /// <summary>
    /// An entry written before the stamp existed carries no display at all. That reads as "we do
    /// not know", never as a mismatch — refusing to compare a calibration that works today
    /// because it predates a field would break every user who had one.
    /// </summary>
    [Fact]
    public void AnEntryWithNoDisplayRecordedIsNotTreatedAsAMismatch()
    {
        var (script, calibration, sampler, _) = Calibrated();

        calibration.CurrentGameMonitor = MonitorTwo;   // and no stamp on the region
        sampler.TargetVisible = true;

        Assert.Null(((ICalibratableScript)script).Monitor);
        Assert.Equal(100, ((ICalibratableScript)script).CurrentSimilarityPercent);
        Assert.True(script.Start());

        script.Stop();
        script.Dispose();
    }

    /// <summary>
    /// ARK is not running, so there is no telling which display it will come up on. The capture
    /// path falls back to the primary because it has to point somewhere; this must not, or a
    /// reference taken on the second monitor would refuse to start every time the panel is opened
    /// before the game is.
    /// </summary>
    [Fact]
    public void WithArkNotRunningNothingIsCalledAMismatch()
    {
        var (script, calibration, sampler, _) = Calibrated();

        calibration.SetRegionMonitor(RegionKey, MonitorTwo.DeviceName, MonitorTwo.ResolutionKey);
        calibration.CurrentGameMonitor = null;
        sampler.TargetVisible = true;

        Assert.Null(((ICalibratableScript)script).Monitor);
        Assert.Equal(100, ((ICalibratableScript)script).CurrentSimilarityPercent);
        Assert.True(script.Start());

        script.Stop();
        script.Dispose();
    }

    /// <summary>
    /// The snapshot is what gets compared from here on, so taking one records the screen it came
    /// off — the region may well have been calibrated on a different one.
    /// </summary>
    [Fact]
    public void CapturingAReferenceRecordsTheDisplayItCameOff()
    {
        var (script, calibration, sampler, _) = Calibrated(withReference: false);
        sampler.Screen = Textured;

        calibration.CurrentGameMonitor = MonitorTwo;
        Assert.True(((ICalibratableScript)script).CaptureReference());

        var stored = calibration.GetRegion(RegionKey);
        Assert.Equal(MonitorTwo.DeviceName, stored!.MonitorDeviceName);
        Assert.Equal("2560x1440", stored.MonitorResolution);

        // Which is immediately enough to catch the game having moved back.
        calibration.CurrentGameMonitor = MonitorOne;
        Assert.True(((ICalibratableScript)script).Monitor!.Mismatch);

        script.Dispose();
    }

    /// <summary>
    /// Nothing to compare is null, not zero. A 0% would read as "the target is definitely not
    /// there", which is a claim a failed capture has no right to make.
    /// </summary>
    [Fact]
    public void WithNothingToCompareTheSimilarityIsNullRatherThanZero()
    {
        var (withoutReference, _, _, _) = Calibrated(withReference: false);
        Assert.Null(((ICalibratableScript)withoutReference).CurrentSimilarityPercent);
        withoutReference.Dispose();

        var (script, _, sampler, _) = Calibrated();
        sampler.SimilarityUnavailable = true;
        Assert.Null(((ICalibratableScript)script).CurrentSimilarityPercent);
        script.Dispose();
    }

    /// <summary>
    /// A blank frame — what a capture path hands back when it cannot see a fullscreen game — would
    /// match every later blank frame at 100 %. It is refused, with the reason, and nothing is stored.
    /// </summary>
    [Fact]
    public void ABlankCaptureIsNotTakenAsTheReference()
    {
        var (script, _, sampler, toasts) = Calibrated(withReference: false);

        Assert.False(((ICalibratableScript)script).CaptureReference());
        Assert.False(sampler.HasReference(RegionKey));
        Assert.Contains(toasts.Toasts, t => t.Level == "warning" && t.Message.Contains("blank", StringComparison.Ordinal));

        script.Dispose();
    }

    // ─── Harness ───────────────────────────────────────────────────────────────

    /// <summary>Stripes: something a reference can match on, unlike the fake's default all-black frame.</summary>
    private static ScreenCapture Textured(Rectangle region)
    {
        var bgra = new byte[region.Width * region.Height * 4];
        for (var p = 0; p < region.Width * region.Height; p++)
        {
            var level = (byte)(p % region.Width / 4 % 2 == 0 ? 220 : 30);
            bgra[p * 4] = bgra[p * 4 + 1] = bgra[p * 4 + 2] = level;
        }
        return new ScreenCapture(region.Width, region.Height, bgra);
    }

    /// <summary>
    /// Take All stands in for the five scripts on this base: the monitor stamp, the mismatch and
    /// the live similarity all live in <see cref="CalibratableScriptBase"/>, so one of them
    /// exercises the code all five share.
    /// </summary>
    private static (TakeAllScript Script, FakeCalibrationService Calibration, FakeScreenSampler Sampler, RecordingNotificationService Toasts)
        Calibrated(bool withReference = true)
    {
        var sampler = new FakeScreenSampler();
        var calibration = new FakeCalibrationService();
        var toasts = new RecordingNotificationService();

        calibration.SetRegion(RegionKey, Rectangle.FromLTRB(2000, 100, 2200, 160));
        if (withReference) sampler.References.Add(RegionKey);

        var script = new TakeAllScript(
            new RecordingInputSimulator(),
            sampler,
            calibration,
            new FakeForegroundGate(gameIsForeground: true),
            new NullAutomationHotkeyService(),
            toasts,
            new RecordingActivityService(),
            new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
            NullLogger<TakeAllScript>.Instance);

        return (script, calibration, sampler, toasts);
    }
}
