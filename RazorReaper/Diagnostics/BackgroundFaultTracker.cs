using System.Collections.Concurrent;

namespace RazorReaper.Diagnostics;

internal enum BackgroundFaultReportKind
{
    /// <summary>First sighting of a fault in this session — full fidelity.</summary>
    First,

    /// <summary>Repeats of a fault already reported, folded into <see cref="BackgroundFaultReport.Occurrences"/>.</summary>
    Rollup,

    /// <summary>Nothing was reportable, but faults were suppressed and the count must still ship.</summary>
    Suppressed
}

/// <summary>
/// The component and member a render dispatch came from, already reduced to member names that
/// cannot carry a path, a machine name or a user name. Never built from anything else.
/// </summary>
internal readonly record struct BackgroundFaultDispatcher(string Owner, string Origin);

/// <summary>Which observer caught the fault. Both are RR-E1003; only one used to be reported.</summary>
internal enum BackgroundFaultSource
{
    /// <summary>TaskScheduler.UnobservedTaskException — the finalizer republishing a dropped Task.</summary>
    UnobservedTask,

    /// <summary>
    /// RenderDispatchGate — a faulted render dispatch, caught before the finalizer ever sees it.
    /// These are the same faults, one step earlier; reporting them only to the local log would
    /// make the family read as fixed the way v1.4.10's Discord filter did.
    /// </summary>
    RenderDispatch
}

/// <summary>
/// One RR-E1003 row's worth of facts. <see cref="Occurrences"/> is the number of faults this row
/// stands for, so summing it across rows reproduces the true fault count — the panel must switch
/// from counting rows to summing this field.
/// </summary>
internal sealed record BackgroundFaultReport(
    BackgroundFaultReportKind Kind,
    BackgroundFaultSource Source,
    string? ExceptionType,
    string? BaseExceptionType,
    string? TopFrame,
    string? TopFrames,
    int LeafExceptionCount,
    long Occurrences,
    long SuppressedAbortedIo,
    string? Message,
    string? Owner = null,
    string? Origin = null,
    bool RenderStopped = false);

/// <summary>
/// Folds the unobserved-task flood into a couple of rows per session without losing the count.
///
/// Production: ~83k app_error rows since 2026-06-15, 36,456 of them from a single 2.5 hour
/// session. Every repeat after the first carries no new information — same base type, same frame,
/// same message — so only the first is reported in full and the rest become a number that ships
/// with the periodic and shutdown rollups.
///
/// Reached from the finalizer thread (TaskScheduler.UnobservedTaskException), the flush timer and
/// the shutdown path at the same time, so every mutation here is interlocked and each occurrence
/// is claimed exactly once by exactly one reporter.
/// </summary>
internal sealed class BackgroundFaultTracker
{
    /// <summary>A session with more distinct faults than this has a different problem.</summary>
    internal const int MaxTrackedFaults = 64;
    internal const string OverflowFrame = "(fault key limit reached)";

    private readonly ConcurrentDictionary<string, FaultBucket> buckets = new(StringComparer.Ordinal);
    private long suppressedAbortedIo;
    private string? suppressedBaseExceptionType;

    /// <summary>
    /// Counts a fault that <see cref="App.IsAbortedBackgroundIo"/> dropped. Returns true only for
    /// the first one in the session, which is the one worth writing to the local log.
    /// </summary>
    public bool RecordSuppressed(AggregateException exception)
    {
        Interlocked.Increment(ref suppressedAbortedIo);

        if (Volatile.Read(ref suppressedBaseExceptionType) is not null)
        {
            return false;
        }

        var baseExceptionType = exception.GetBaseException().GetType().FullName
            ?? nameof(AggregateException);

        return Interlocked.CompareExchange(ref suppressedBaseExceptionType, baseExceptionType, null) is null;
    }

    /// <summary>
    /// Records a fault and returns the row to send, or null when it is a repeat of one already
    /// reported in this session.
    /// </summary>
    public BackgroundFaultReport? Record(AggregateException exception)
    {
        var baseException = exception.GetBaseException();
        var origin = BackgroundFaultFrames.Describe(baseException);
        return Record(
            BackgroundFaultSource.UnobservedTask,
            exception,
            baseException,
            origin,
            dispatcher: null,
            stopped: false);
    }

    /// <summary>
    /// Records a fault the render gate observed, keyed and dampened exactly like an unobserved
    /// one — same buckets, same claim protocol, same <see cref="BackgroundFaultReport.Occurrences"/>
    /// fold — with the owning component and dispatching member added to the key so one broken
    /// component costs one row plus a count instead of a row per tick, and two broken components
    /// are still told apart. Returns null for a repeat.
    /// </summary>
    public BackgroundFaultReport? RecordRenderDispatch(Exception exception, string? owner, string? origin, bool stopped)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // The gate already unwrapped to the base exception; GetBaseException on a non-aggregate
        // is the identity, so this is safe either way.
        var baseException = exception.GetBaseException();

        // Sanitized here, once, before anything keys or transmits them: owner is a type name and
        // origin a [CallerMemberName], but origin is an ordinary parameter a caller may pass by
        // hand and neither may carry a path, a machine name or a user name out of the process.
        var dispatcher = new BackgroundFaultDispatcher(
            BackgroundFaultFrames.Identifier(owner),
            BackgroundFaultFrames.Identifier(origin));
        var frames = BackgroundFaultFrames.DescribeRenderDispatch(baseException, owner, origin);

        // A breaker trip gets its own bucket: a component permanently ending its own renders is a
        // different event from the faults that led there, and folding it into them would lose it.
        return Record(
            BackgroundFaultSource.RenderDispatch,
            exception,
            baseException,
            frames,
            dispatcher,
            stopped);
    }

    private BackgroundFaultReport? Record(
        BackgroundFaultSource source,
        Exception exception,
        Exception baseException,
        BackgroundFaultOrigin origin,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped)
    {
        var baseExceptionType = baseException.GetType().FullName ?? baseException.GetType().Name;
        var bucket = GetOrAddBucket(source, baseExceptionType, origin, dispatcher, stopped, exception);

        if (Interlocked.Increment(ref bucket.Count) != 1)
        {
            return null;
        }

        // A flush can race the first occurrence; whoever claims it sends it, once.
        var occurrences = bucket.ClaimUnreported();
        return occurrences <= 0
            ? null
            : bucket.ToReport(BackgroundFaultReportKind.First, occurrences, DrainSuppressed());
    }

    /// <summary>
    /// Everything counted since the last flush: one row per fault that saw repeats, plus the
    /// suppression count — which rides along on the first row, or gets its own row when there is
    /// nothing else to say, so a quiet version can never again be mistaken for a fixed one.
    /// </summary>
    public IReadOnlyList<BackgroundFaultReport> Flush()
    {
        var reports = new List<BackgroundFaultReport>();

        foreach (var bucket in buckets.Values)
        {
            var occurrences = bucket.ClaimUnreported();
            if (occurrences > 0)
            {
                reports.Add(bucket.ToReport(BackgroundFaultReportKind.Rollup, occurrences, 0));
            }
        }

        var suppressed = DrainSuppressed();
        if (suppressed <= 0)
        {
            return reports;
        }

        if (reports.Count > 0)
        {
            reports[0] = reports[0] with { SuppressedAbortedIo = suppressed };
            return reports;
        }

        reports.Add(new BackgroundFaultReport(
            BackgroundFaultReportKind.Suppressed,
            BackgroundFaultSource.UnobservedTask,
            ExceptionType: typeof(AggregateException).FullName,
            BaseExceptionType: Volatile.Read(ref suppressedBaseExceptionType),
            TopFrame: null,
            TopFrames: null,
            LeafExceptionCount: 0,
            Occurrences: suppressed,
            SuppressedAbortedIo: suppressed,
            Message: "Aborted background I/O suppressed."));

        return reports;
    }

    private FaultBucket GetOrAddBucket(
        BackgroundFaultSource source,
        string baseExceptionType,
        BackgroundFaultOrigin origin,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped,
        Exception exception)
    {
        var key = BuildKey(source, baseExceptionType, origin.TopFrame, dispatcher, stopped);
        if (buckets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (buckets.Count >= MaxTrackedFaults)
        {
            // Bound the memory a pathological session can pin. Everything past the limit keeps
            // being counted, it just stops being told apart.
            return buckets.GetOrAdd(
                OverflowFrame,
                _ => new FaultBucket(source, baseExceptionType, OverflowFrame, OverflowFrame, dispatcher, stopped, exception));
        }

        return buckets.GetOrAdd(
            key,
            _ => new FaultBucket(source, baseExceptionType, origin.TopFrame, origin.TopFrames, dispatcher, stopped, exception));
    }

    /// <summary>
    /// The one place a fault's identity is decided, for both sources: base exception type plus
    /// where it came from. Unobserved faults keep the exact key they had. A render fault adds the
    /// owning component and member — the stack alone cannot tell two broken components apart once
    /// their renderer is gone — and the breaker trip, so that event gets its own row.
    /// </summary>
    private static string BuildKey(
        BackgroundFaultSource source,
        string baseExceptionType,
        string topFrame,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped)
    {
        return source == BackgroundFaultSource.UnobservedTask
            ? $"{baseExceptionType}|{topFrame}"
            : $"render|{dispatcher?.Owner}.{dispatcher?.Origin}|{(stopped ? "stopped" : "faulted")}|{baseExceptionType}|{topFrame}";
    }

    private long DrainSuppressed()
    {
        return Interlocked.Exchange(ref suppressedAbortedIo, 0);
    }

    private sealed class FaultBucket(
        BackgroundFaultSource source,
        string baseExceptionType,
        string topFrame,
        string topFrames,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped,
        Exception exception)
    {
        private readonly string exceptionType = exception.GetType().FullName ?? nameof(AggregateException);
        private readonly string? message = BackgroundFaultFrames.Redact(exception.Message);

        // An unobserved fault always arrives wrapped; a render fault is the leaf itself.
        private readonly int leafExceptionCount = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.Count
            : 1;

        internal long Count;
        private long reported;

        /// <summary>
        /// Takes ownership of every occurrence counted but not yet sent. The CAS is what keeps a
        /// flush and a finalizer-thread first sighting from reporting the same fault twice.
        /// </summary>
        internal long ClaimUnreported()
        {
            while (true)
            {
                var count = Interlocked.Read(ref Count);
                var alreadyReported = Interlocked.Read(ref reported);
                if (count <= alreadyReported)
                {
                    return 0;
                }

                if (Interlocked.CompareExchange(ref reported, count, alreadyReported) == alreadyReported)
                {
                    return count - alreadyReported;
                }
            }
        }

        internal BackgroundFaultReport ToReport(
            BackgroundFaultReportKind kind,
            long occurrences,
            long suppressedAbortedIo)
        {
            return new BackgroundFaultReport(
                kind,
                source,
                exceptionType,
                baseExceptionType,
                topFrame,
                topFrames,
                leafExceptionCount,
                occurrences,
                suppressedAbortedIo,
                message,
                dispatcher?.Owner,
                dispatcher?.Origin,
                stopped);
        }
    }
}
