using System.Text;
using System.Text.Json;
using RazorReaper;
using RazorReaper.Diagnostics;

namespace RazorReaper.UnitTests.Telemetry;

/// <summary>
/// The app_error row both fault observers now share, held against the ingest contract in
/// RR-Admin-Panel/shared/telemetry-contract.ts: MAX_METRICS_KEYS = 64, MAX_METRICS_BYTES = 8192,
/// and every key the client adds must be additive and optional. Nothing existing may be renamed,
/// dropped, or given a new value the panel's filters do not already handle.
/// </summary>
public sealed class BackgroundFaultMetricsTests
{
    // Mirrored from shared/telemetry-contract.ts. If these ever change there, this test is the
    // place that notices.
    private const int MaxMetricsKeys = 64;
    private const int MaxMetricsBytes = 8 * 1024;

    /// <summary>
    /// What TelemetryService.BuildBaseMetricsAsync merges in underneath: install/session
    /// identity, platform, device, geo and the schema tag. Generous on purpose — the point is
    /// that the fault keys leave room, not that the base set is pinned here.
    /// </summary>
    private const int BaseMetricKeyBudget = 30;

    [Fact]
    public void AnUnobservedFaultRowKeepsExactlyTheKeysItAlreadyHadPlusFaultSource()
    {
        var metrics = App.BuildBackgroundFaultMetrics(Report(BackgroundFaultSource.UnobservedTask));

        Assert.Equal(
            [
                "base_exception_type",
                "error_code",
                "error_kind",
                "exception_type",
                "fault_source",
                "leaf_exception_count",
                "occurrences",
                "report_kind",
                "suppressed_aborted_io",
                "top_frame",
                "top_frames"
            ],
            metrics.Keys.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal("unobserved_task", metrics["fault_source"]);

        // The render-only keys are omitted, not sent null, so this row is what it always was.
        Assert.DoesNotContain("render_owner", metrics.Keys);
    }

    [Fact]
    public void ARenderDispatchRowAddsOnlyTheThreeRenderKeys()
    {
        var metrics = App.BuildBackgroundFaultMetrics(Report(BackgroundFaultSource.RenderDispatch));

        Assert.Equal("render_dispatch", metrics["fault_source"]);
        Assert.Equal("Home", metrics["render_owner"]);
        Assert.Equal("OnTick", metrics["render_origin"]);
        Assert.Equal(true, metrics["render_stopped"]);
    }

    [Fact]
    public void BothRowsKeepErrorKindBackgroundSoNoPanelFilterMoves()
    {
        // Every KPI in the panel filters on error_kind != 'background' and the background-fault
        // aggregate matches it exactly. A render fault is still a background fault; fault_source
        // is what separates the two populations.
        foreach (var source in Enum.GetValues<BackgroundFaultSource>())
        {
            var metrics = App.BuildBackgroundFaultMetrics(Report(source));

            Assert.Equal("background", metrics["error_kind"]);
            Assert.Equal(AppErrorCodes.UnobservedTaskException, metrics["error_code"]);
        }
    }

    [Fact]
    public void StaysWellInsideTheIngestCaps()
    {
        foreach (var source in Enum.GetValues<BackgroundFaultSource>())
        {
            var metrics = App.BuildBackgroundFaultMetrics(Report(source, longest: true));
            var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(metrics));

            Assert.True(
                metrics.Count + BaseMetricKeyBudget <= MaxMetricsKeys,
                $"{source}: {metrics.Count} fault keys plus the base set must fit in {MaxMetricsKeys}.");
            Assert.True(bytes * 4 < MaxMetricsBytes, $"{source}: {bytes} bytes is not well inside {MaxMetricsBytes}.");
        }
    }

    [Fact]
    public void EveryValueIsAScalarTheIngestCanStore()
    {
        foreach (var source in Enum.GetValues<BackgroundFaultSource>())
        {
            Assert.All(
                App.BuildBackgroundFaultMetrics(Report(source)).Values,
                value => Assert.True(
                    value is null or string or bool or int or long,
                    $"{value?.GetType().Name} is not a scalar the ingest stores."));
        }
    }

    private static BackgroundFaultReport Report(BackgroundFaultSource source, bool longest = false)
    {
        var render = source == BackgroundFaultSource.RenderDispatch;
        var frame = longest ? new string('F', 120) : "Home.OnTick (Home.razor:780)";

        return new BackgroundFaultReport(
            BackgroundFaultReportKind.First,
            source,
            typeof(AggregateException).FullName,
            typeof(NullReferenceException).FullName,
            frame,
            longest ? string.Join(" > ", Enumerable.Repeat(frame, 4)) : frame,
            LeafExceptionCount: 1,
            Occurrences: 36_456,
            SuppressedAbortedIo: 12,
            Message: longest ? new string('m', 500) : "Object reference not set to an instance of an object.",
            Owner: render ? "Home" : null,
            Origin: render ? "OnTick" : null,
            RenderStopped: render);
    }
}
