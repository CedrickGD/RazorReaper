using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The scan interval on a script's settings page is a period, and it has to mean that.
///
/// The loop used to wait the whole interval *after* the tick, so the real cadence was interval +
/// however long the tick took. A screen grab and a compare cost tens of milliseconds, which made
/// Noglin's "400 ms" nearer 450 on a fast machine and much worse on a slow one — and the slower
/// the machine, the longer the mind-controlled player waits for their FPS to drop. Nothing
/// reported the drift; the page kept showing the number the user chose.
/// </summary>
public sealed class ScanLoopCadenceTests
{
    [Fact]
    public void ATickThatTookPartOfTheIntervalOnlyWaitsOutTheRest()
    {
        Assert.Equal(20, AutomationScriptBase.RemainingDelayMs(50, 30));
        Assert.Equal(50, AutomationScriptBase.RemainingDelayMs(50, 0));
        Assert.Equal(1, AutomationScriptBase.RemainingDelayMs(50, 49));
    }

    /// <summary>
    /// A tick that overruns yields for a millisecond instead of spinning: falling behind is the
    /// right answer, pinning a core is not.
    /// </summary>
    [Theory]
    [InlineData(50, 50)]
    [InlineData(50, 80)]
    [InlineData(50, 5000)]
    public void ATickThatOverranStillYields(int intervalMs, long elapsedMs)
    {
        Assert.Equal(1, AutomationScriptBase.RemainingDelayMs(intervalMs, elapsedMs));
    }

    [Fact]
    public void TheWaitNeverExceedsTheInterval()
    {
        Assert.Equal(50, AutomationScriptBase.RemainingDelayMs(50, -10));
    }

    /// <summary>
    /// The whole point, end to end: a 100 ms loop whose tick costs 80 ms runs at 100 ms, not 180.
    /// The bound sits between the two so the test says which one happened rather than how fast the
    /// machine is.
    /// </summary>
    [Fact]
    public async Task ASlowTickDoesNotStretchTheInterval()
    {
        const int intervalMs = 100;
        const int tickCostMs = 80;
        const int ticksToWatch = 6;

        var gaps = new List<long>();
        var clock = Stopwatch.StartNew();
        var last = 0L;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var script = new PacedScript(intervalMs, async ct =>
        {
            var now = clock.ElapsedMilliseconds;
            if (last != 0) gaps.Add(now - last);
            last = now;
            if (gaps.Count >= ticksToWatch) done.TrySetResult();
            await Task.Delay(tickCostMs, ct);
        });

        Assert.True(script.Start());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        script.Stop();

        var mean = gaps.Take(ticksToWatch).Average();

        // Fixed: ~100 ms. Broken (interval waited after the tick): ~180 ms.
        Assert.True(mean < 140, $"mean gap was {mean:F0} ms — the tick's own cost is being added to the interval");
        Assert.True(mean >= intervalMs - 20, $"mean gap was {mean:F0} ms — faster than the interval asked for");
    }

    /// <summary>A script that is nothing but the shared scan loop.</summary>
    private sealed class PacedScript : AutomationScriptBase
    {
        private readonly int _intervalMs;
        private readonly Func<CancellationToken, Task> _tick;

        public PacedScript(int intervalMs, Func<CancellationToken, Task> tick)
            : base("test-cadence", "Cadence", string.Empty,
                   new FakeForegroundGate(gameIsForeground: true),
                   new NullAutomationHotkeyService(),
                   new RecordingNotificationService(),
                   new RecordingActivityService(),
                   new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
                   NullLogger.Instance)
        {
            _intervalMs = intervalMs;
            _tick = tick;
        }

        /// <summary>Vision, so the shared input quota stays out of a timing test.</summary>
        public override bool UsesVision => true;

        protected override Task RunAsync(CancellationToken ct)
            => RunLoopAsync(_intervalMs, _tick, foregroundOnly: true, ct);
    }
}
