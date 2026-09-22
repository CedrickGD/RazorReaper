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
    /// The whole point, end to end: a 100 ms loop whose tick costs 80 ms waits out only what is
    /// left of the interval (~20 ms), not another 100 on top of the tick.
    ///
    /// This used to time the gaps between ticks on the wall clock (mean under 140 ms) and failed
    /// about every other full-suite run while a game loaded the PC: each Task.Delay overshoots by
    /// whatever the scheduler adds, and that noise landed in the same number as the bug. So the test
    /// reads the wait the loop asks for instead, and brackets the loop's own stopwatch between two
    /// spans it can time itself:
    ///   - the tick (tick start to tick end) lies inside the loop's timed span, so the loop measured
    ///     at least that much: wait &lt;= interval - tick;
    ///   - the previous wait's end to this wait's call contains the loop's timed span, so the loop
    ///     measured at most that much: wait &gt;= interval - span.
    /// Both hold on any machine at any load; load only moves the bounds. The bug (the full interval
    /// waited after the tick) asks for 100 ms against an upper bound of about 20.
    /// </summary>
    [Fact]
    public async Task ASlowTickDoesNotStretchTheInterval()
    {
        const int intervalMs = 100;
        const int tickCostMs = 80;
        const int waitsToWatch = 6;

        long tickStart = 0, tickEnd = 0, lastWaitEnd = 0;
        var waits = new List<(int AskedMs, long TickMs, long SpanMs)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var script = new PacedScript(intervalMs,
            tick: async ct =>
            {
                tickStart = Stopwatch.GetTimestamp();
                await Task.Delay(tickCostMs, ct);
                tickEnd = Stopwatch.GetTimestamp();
            },
            wait: async (delayMs, ct) =>
            {
                var called = Stopwatch.GetTimestamp();
                // The first wait has no previous wait to bound it from above.
                if (lastWaitEnd != 0 && waits.Count < waitsToWatch)
                {
                    waits.Add((delayMs, WholeMs(tickStart, tickEnd), WholeMs(lastWaitEnd, called)));
                    if (waits.Count == waitsToWatch) done.TrySetResult();
                }
                try { await Task.Delay(delayMs, ct); }
                finally { lastWaitEnd = Stopwatch.GetTimestamp(); }
            });

        Assert.True(script.Start());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        script.Stop();

        foreach (var (askedMs, tickMs, spanMs) in waits)
        {
            Assert.True(askedMs <= Math.Max(1, intervalMs - tickMs),
                $"the loop waited {askedMs} ms after a {tickMs} ms tick — the tick's own cost is being added to the interval");
            Assert.True(askedMs >= Math.Max(1, intervalMs - spanMs),
                $"the loop waited {askedMs} ms when at most {spanMs} ms of the interval had gone — faster than the interval asked for");
        }
    }

    /// <summary>
    /// Whole milliseconds between two timestamps, rounded down the same way the loop's
    /// Stopwatch.ElapsedMilliseconds is, so a longer span never reads as fewer milliseconds.
    /// </summary>
    private static long WholeMs(long from, long to)
        => Stopwatch.GetElapsedTime(from, to).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>A script that is nothing but the shared scan loop, with its wait handed to the test.</summary>
    private sealed class PacedScript : AutomationScriptBase
    {
        private readonly int _intervalMs;
        private readonly Func<CancellationToken, Task> _tick;
        private readonly Func<int, CancellationToken, Task> _wait;

        public PacedScript(int intervalMs, Func<CancellationToken, Task> tick, Func<int, CancellationToken, Task> wait)
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
            _wait = wait;
        }

        protected override Task WaitOutIntervalAsync(int delayMs, CancellationToken ct) => _wait(delayMs, ct);

        /// <summary>Vision, so the shared input quota stays out of a timing test.</summary>
        public override bool UsesVision => true;

        protected override Task RunAsync(CancellationToken ct)
            => RunLoopAsync(_intervalMs, _tick, foregroundOnly: true, ct);
    }
}
