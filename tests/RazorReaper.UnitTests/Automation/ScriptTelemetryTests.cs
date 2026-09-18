using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// Before this, <c>TrackEventAsync</c> appeared zero times under Services/Automation: seventeen
/// scripts, a shared scaffold that knew exactly when each run began and ended, and not one row
/// about any of it. The panel could see that the app was open and nothing about whether the
/// feature people install it for does anything.
///
/// Three events, and the third is the interesting one. A run that ends having produced no
/// effects at all is the shape of every "it does nothing" report, and it is invisible in a start
/// and a stop that look exactly like a healthy pair.
/// </summary>
public sealed class ScriptTelemetryTests
{
    [Fact]
    public async Task AStartedRunSaysWhichScriptAndHowItWasStarted()
    {
        using var script = new TelemetryScript(gameIsForeground: true) { Premium = true };

        Assert.True(script.Start());
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Start).Count == 1);

        var start = script.Sink.Named(ScriptTelemetryEvents.Start).Single();
        Assert.Equal("test-telemetry", start.Metrics["script"]);
        Assert.Equal(true, start.Metrics["premium"]);
        Assert.Equal(false, start.Metrics["hotkey"]);

        script.Stop();
    }

    /// <summary>
    /// The hotkey is the whole reason these scripts exist — you cannot click a page while the
    /// game has focus — so which of the two ways people actually start a run is worth a flag.
    /// </summary>
    [Fact]
    public async Task AHotkeyStartSaysItWasAHotkey()
    {
        using var script = new TelemetryScript(gameIsForeground: true);

        script.Toggle();
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Start).Count == 1);

        var start = script.Sink.Named(ScriptTelemetryEvents.Start).Single();
        Assert.Equal(true, start.Metrics["hotkey"]);
        Assert.Equal(false, start.Metrics["premium"]);

        script.Stop();
    }

    [Fact]
    public async Task ARunThatDidSomethingStopsWithItsEffectsAndNoNoop()
    {
        using var script = new TelemetryScript(gameIsForeground: true);

        Assert.True(script.Start());
        await WaitUntil(() => script.EffectCount >= 3);
        script.Stop();
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Stop).Count == 1);

        var stop = script.Sink.Named(ScriptTelemetryEvents.Stop).Single();
        Assert.Equal("test-telemetry", stop.Metrics["script"]);
        Assert.Equal(false, stop.Metrics["noop"]);
        Assert.Equal("user", stop.Metrics["reason"]);
        Assert.True(Convert.ToInt32(stop.Metrics["effects"]) >= 3);
        Assert.True(Convert.ToInt32(stop.Metrics["duration_s"]) >= 0);

        // Give the loop's own unwind every chance to add a second row.
        await Task.Delay(150);
        Assert.Empty(script.Sink.Named(ScriptTelemetryEvents.Noop));
    }

    /// <summary>
    /// The gate stayed shut for the whole run: a start, a stop, and nothing in between. That is
    /// the pair that used to look healthy.
    /// </summary>
    [Fact]
    public async Task ARunThatDidNothingSaysSoTwice()
    {
        using var script = new TelemetryScript(gameIsForeground: false);

        Assert.True(script.Start());
        await WaitUntil(() => script.Gate.TimesAsked >= 5);
        script.Stop();
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Noop).Count == 1);

        var stop = script.Sink.Named(ScriptTelemetryEvents.Stop).Single();
        Assert.Equal(true, stop.Metrics["noop"]);
        Assert.Equal(0, Convert.ToInt32(stop.Metrics["effects"]));

        var noop = script.Sink.Named(ScriptTelemetryEvents.Noop).Single();
        Assert.Equal("test-telemetry", noop.Metrics["script"]);
        Assert.Equal("user", noop.Metrics["reason"]);
    }

    /// <summary>
    /// A stop and the loop's own unwind are two ends of one run. Counted twice, every duration
    /// on the panel doubles and every run appears as two.
    /// </summary>
    [Fact]
    public async Task ARunIsReportedOnceEvenThoughTwoPathsEndIt()
    {
        using var script = new TelemetryScript(gameIsForeground: true);

        Assert.True(script.Start());
        await WaitUntil(() => script.EffectCount >= 2);
        script.Stop();
        await Task.Delay(250);

        Assert.Single(script.Sink.Named(ScriptTelemetryEvents.Start));
        Assert.Single(script.Sink.Named(ScriptTelemetryEvents.Stop));
    }

    /// <summary>
    /// A one-shot that runs its course is not a crash and not a quota wall — it is the start
    /// the user asked for, finishing.
    /// </summary>
    [Fact]
    public async Task AOneShotThatFinishesStopsAsAUserRun()
    {
        using var script = new TelemetryScript(gameIsForeground: true, oneShot: true);

        Assert.True(script.Start());
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Stop).Count == 1);

        var stop = script.Sink.Named(ScriptTelemetryEvents.Stop).Single();
        Assert.Equal("user", stop.Metrics["reason"]);
        Assert.Equal(false, stop.Metrics["noop"]);
    }

    /// <summary>A run the loop threw out of is an error, not a stop somebody asked for.</summary>
    [Fact]
    public async Task ARunThatThrewStopsAsAnError()
    {
        using var script = new TelemetryScript(gameIsForeground: true, throws: true);

        Assert.True(script.Start());
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Stop).Count == 1);

        var stop = script.Sink.Named(ScriptTelemetryEvents.Stop).Single();
        Assert.Equal("error", stop.Metrics["reason"]);
        Assert.Equal(true, stop.Metrics["noop"]);

        var noop = script.Sink.Named(ScriptTelemetryEvents.Noop).Single();
        Assert.Equal("error", noop.Metrics["reason"]);
    }

    /// <summary>
    /// "People start scripts and stop them straight away" and "people run out of free runs" are
    /// opposite conclusions, and before the reason existed they were the same row.
    /// </summary>
    [Fact]
    public async Task ARunTheQuotaEndedSaysQuota()
    {
        using var script = new TelemetryScript(gameIsForeground: true, vision: false);
        script.ResolveUsageGate = () => new ExhaustedUsageGate();

        Assert.True(script.Start());
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Stop).Count == 1);

        var stop = script.Sink.Named(ScriptTelemetryEvents.Stop).Single();
        Assert.Equal("quota", stop.Metrics["reason"]);
    }

    /// <summary>Nothing in a payload can identify anybody — every value is the app's own.</summary>
    [Fact]
    public async Task NoPayloadCarriesAnythingButTheAppsOwnValues()
    {
        using var script = new TelemetryScript(gameIsForeground: true);

        Assert.True(script.Start());
        await WaitUntil(() => script.EffectCount >= 2);
        script.Stop();
        await WaitUntil(() => script.Sink.Named(ScriptTelemetryEvents.Stop).Count == 1);

        var allowed = new[] { "script", "premium", "hotkey", "duration_s", "effects", "noop", "reason" };

        foreach (var sent in script.Sink.Events)
        {
            Assert.Null(sent.Message);
            Assert.All(sent.Metrics.Keys, key => Assert.Contains(key, allowed));
            Assert.Equal("test-telemetry", sent.Metrics["script"]);
        }
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

    /// <summary>Writes down what was sent instead of putting it on a wire.</summary>
    private sealed class RecordingTelemetryService : ITelemetryService
    {
        public sealed record Sent(string Name, string? Message, IReadOnlyDictionary<string, object?> Metrics);

        private readonly ConcurrentQueue<Sent> _events = new();

        public IReadOnlyList<Sent> Events => _events.ToArray();

        public IReadOnlyList<Sent> Named(string name)
            => _events.Where(e => e.Name == name).ToArray();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TrackEventAsync(
            string eventName,
            TelemetryEventStatus status = TelemetryEventStatus.Ok,
            string? message = null,
            IReadOnlyDictionary<string, object?>? metrics = null,
            CancellationToken cancellationToken = default)
        {
            _events.Enqueue(new Sent(
                eventName,
                message,
                metrics ?? new Dictionary<string, object?>()));

            return Task.CompletedTask;
        }
    }

    /// <summary>A gate with nothing left in the month.</summary>
    private sealed class ExhaustedUsageGate : IUsageGateService
    {
        public event Action? OnUsageChanged { add { } remove { } }

        public Task<UsageGateResult> TryConsumeAsync(string feature)
            => Task.FromResult(new UsageGateResult(false, false, 0, 5));

        public Task<IReadOnlyDictionary<string, FeatureUsage>?> GetStatusAsync()
            => Task.FromResult<IReadOnlyDictionary<string, FeatureUsage>?>(null);
    }

    private sealed class TelemetryScript : AutomationScriptBase
    {
        private readonly bool _oneShot;
        private readonly bool _throws;
        private readonly bool _vision;

        public TelemetryScript(
            bool gameIsForeground,
            bool oneShot = false,
            bool throws = false,
            bool vision = true)
            : this(new FakeForegroundGate(gameIsForeground), new RecordingTelemetryService(), oneShot, throws, vision)
        {
        }

        private TelemetryScript(
            FakeForegroundGate gate,
            RecordingTelemetryService sink,
            bool oneShot,
            bool throws,
            bool vision)
            : base("test-telemetry", "Telemetry", string.Empty,
                   gate,
                   new NullAutomationHotkeyService(),
                   new RecordingNotificationService(),
                   new RecordingActivityService(),
                   new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
                   NullLogger.Instance)
        {
            Gate = gate;
            Sink = sink;
            _oneShot = oneShot;
            _throws = throws;
            _vision = vision;

            // Outside a MAUI application there is no service provider to find either of these
            // in, so without the overrides every assertion below would pass on an empty sink.
            ResolveTelemetry = () => sink;
            ResolvePremium = () => Premium;
        }

        public FakeForegroundGate Gate { get; }

        public RecordingTelemetryService Sink { get; }

        public bool Premium { get; set; }

        public override bool UsesVision => _vision;

        protected override async Task RunAsync(CancellationToken ct)
        {
            if (_throws) throw new InvalidOperationException("the tick blew up");

            if (_oneShot)
            {
                ReportEffect();
                return;
            }

            await RunLoopAsync(10, _ =>
            {
                ReportEffect();
                return Task.CompletedTask;
            }, foregroundOnly: true, ct);
        }
    }
}
