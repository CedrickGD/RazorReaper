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
/// A first sighting as the observing thread hands it over. Everything cheap is already decided —
/// the bucket exists, this occurrence is claimed, the row will be a
/// <see cref="BackgroundFaultReportKind.First"/> — but the frames are not yet described. That is
/// a PDB-backed stack walk, and the thread that observed a render fault is the renderer's own
/// dispatcher, so <see cref="Describe"/> is for the reporting path, off that thread.
/// </summary>
internal sealed class PendingBackgroundFaultReport
{
    private readonly BackgroundFaultTracker.FaultBucket bucket;
    private readonly long occurrences;
    private readonly long suppressedAbortedIo;

    internal PendingBackgroundFaultReport(
        BackgroundFaultTracker.FaultBucket bucket,
        Exception exception,
        long occurrences,
        long suppressedAbortedIo)
    {
        this.bucket = bucket;
        this.occurrences = occurrences;
        this.suppressedAbortedIo = suppressedAbortedIo;
        Exception = exception;
    }

    /// <summary>The fault as it was observed, for the local error record. Held here, not by the bucket.</summary>
    public Exception Exception { get; }

    public BackgroundFaultSource Source => bucket.Source;

    /// <summary>True once the frames have been described. Pins that the observing thread never does it.</summary>
    internal bool IsDescribed => bucket.IsDescribed;

    /// <summary>
    /// Finishes the row: describes the frames if no one has yet, and releases the exception the
    /// bucket held for that. Safe from any thread, idempotent, and never throws.
    /// </summary>
    public BackgroundFaultReport Describe()
        => bucket.ToReport(BackgroundFaultReportKind.First, occurrences, suppressedAbortedIo);
}

/// <summary>
/// Folds the unobserved-task flood into a couple of rows per session without losing the count.
///
/// Production: ~83k app_error rows since 2026-06-15, 36,456 of them from a single 2.5 hour
/// session. Every repeat after the first carries no new information — same base type, same frame,
/// same message — so only the first is reported in full and the rest become a number that ships
/// with the periodic and shutdown rollups.
///
/// Reached from the finalizer thread (TaskScheduler.UnobservedTaskException), the renderer's
/// dispatcher (RenderDispatchGate), the flush timer and the shutdown path at the same time, so
/// every mutation here is interlocked and each occurrence is claimed exactly once by exactly one
/// reporter. The observing thread does only what the fold needs — a cheap site capture and a
/// count; describing the frames is deferred to whoever reports the row.
/// </summary>
internal sealed class BackgroundFaultTracker
{
    /// <summary>
    /// Per source. A session with more distinct faults than this from one observer has a
    /// different problem. The budgets are separate because the render key is far more granular
    /// than the unobserved one — owner, member and breaker state on top of type and site — so one
    /// component churning distinct dispatchers would otherwise spend every bucket and collapse
    /// the finalizer's faults, the population this whole investigation is chasing, into the
    /// overflow row with no frame.
    /// </summary>
    internal const int MaxTrackedFaults = 64;

    /// <summary>Everything the tracker can pin: both sources at their cap, each with its overflow bucket.</summary>
    internal const int MaxTrackedFaultsTotal = 2 * (MaxTrackedFaults + 1);

    internal const string OverflowFrame = "(fault key limit reached)";

    private readonly ConcurrentDictionary<string, FaultBucket> unobservedBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FaultBucket> renderBuckets = new(StringComparer.Ordinal);
    private long suppressedAbortedIo;
    private string? suppressedBaseExceptionType;

    /// <summary>Buckets held right now, both sources. For the tests that pin the bound.</summary>
    internal int TrackedFaultCount => unobservedBuckets.Count + renderBuckets.Count;

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
    public PendingBackgroundFaultReport? Record(AggregateException exception)
    {
        var baseException = exception.GetBaseException();
        return Record(
            BackgroundFaultSource.UnobservedTask,
            exception,
            baseException,
            BackgroundFaultFrames.CaptureSite(baseException),
            dispatcher: null,
            stopped: false);
    }

    /// <summary>
    /// Records a fault the render gate observed, keyed and dampened exactly like an unobserved
    /// one — same claim protocol, same <see cref="BackgroundFaultReport.Occurrences"/> fold, its
    /// own budget — with the owning component and dispatching member added to the key so one
    /// broken component costs one row plus a count instead of a row per tick, and two broken
    /// components are still told apart. Returns null for a repeat.
    /// </summary>
    public PendingBackgroundFaultReport? RecordRenderDispatch(Exception exception, string? owner, string? origin, bool stopped)
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

        // A breaker trip gets its own bucket: a component permanently ending its own renders is a
        // different event from the faults that led there, and folding it into them would lose it.
        return Record(
            BackgroundFaultSource.RenderDispatch,
            exception,
            baseException,
            BackgroundFaultFrames.CaptureSite(baseException),
            dispatcher,
            stopped);
    }

    private PendingBackgroundFaultReport? Record(
        BackgroundFaultSource source,
        Exception exception,
        Exception baseException,
        string site,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped)
    {
        var baseExceptionType = baseException.GetType().FullName ?? baseException.GetType().Name;
        var bucket = GetOrAddBucket(source, baseExceptionType, site, dispatcher, stopped, exception, baseException);

        if (Interlocked.Increment(ref bucket.Count) != 1)
        {
            return null;
        }

        // A flush can race the first occurrence; whoever claims it sends it, once.
        var occurrences = bucket.ClaimUnreported();
        return occurrences <= 0
            ? null
            : new PendingBackgroundFaultReport(bucket, exception, occurrences, DrainSuppressed());
    }

    /// <summary>
    /// Everything counted since the last flush: one row per fault that saw repeats, plus the
    /// suppression count — which rides along on the first row, or gets its own row when there is
    /// nothing else to say, so a quiet version can never again be mistaken for a fixed one.
    /// Runs on the flush timer or the shutdown path, never on an observing thread, so this is
    /// where a bucket nobody has described yet gets its frames.
    /// </summary>
    public IReadOnlyList<BackgroundFaultReport> Flush()
    {
        var reports = new List<BackgroundFaultReport>();

        foreach (var bucket in unobservedBuckets.Values.Concat(renderBuckets.Values))
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
        string site,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped,
        Exception exception,
        Exception baseException)
    {
        var buckets = source == BackgroundFaultSource.RenderDispatch ? renderBuckets : unobservedBuckets;

        var key = BuildKey(source, baseExceptionType, site, dispatcher, stopped);
        if (buckets.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (buckets.Count >= MaxTrackedFaults)
        {
            // Bound the memory a pathological session can pin. Everything past the limit keeps
            // being counted, it just stops being told apart — and only within its own source:
            // the other observer's budget is untouched.
            return buckets.GetOrAdd(
                OverflowFrame,
                _ => FaultBucket.Overflow(source, baseExceptionType, dispatcher, stopped, exception));
        }

        return buckets.GetOrAdd(
            key,
            _ => new FaultBucket(source, baseExceptionType, dispatcher, stopped, exception, baseException));
    }

    /// <summary>
    /// The one place a fault's identity is decided, for both sources: base exception type plus
    /// where it came from — the top own method and IL offset, which is as site-specific as the
    /// file and line the described frame carries and costs no symbol lookup. A render fault
    /// adds the owning component and member — the stack alone cannot tell two broken components
    /// apart once their renderer is gone — and the breaker trip, so that event gets its own row.
    /// </summary>
    private static string BuildKey(
        BackgroundFaultSource source,
        string baseExceptionType,
        string site,
        BackgroundFaultDispatcher? dispatcher,
        bool stopped)
    {
        return source == BackgroundFaultSource.UnobservedTask
            ? $"{baseExceptionType}|{site}"
            : $"render|{dispatcher?.Owner}.{dispatcher?.Origin}|{(stopped ? "stopped" : "faulted")}|{baseExceptionType}|{site}";
    }

    private long DrainSuppressed()
    {
        return Interlocked.Exchange(ref suppressedAbortedIo, 0);
    }

    /// <summary>
    /// One distinct fault. Holds strings and counters — plus, until someone reports it, the first
    /// sighting's base exception, because describing its frames is the one expensive step and it
    /// must not happen on the thread that observed the fault. The reference is dropped the moment
    /// the frames exist.
    /// </summary>
    internal sealed class FaultBucket
    {
        private readonly BackgroundFaultDispatcher? dispatcher;
        private readonly bool stopped;
        private readonly string baseExceptionType;
        private readonly string exceptionType;
        private readonly string? message;

        // An unobserved fault always arrives wrapped; a render fault is the leaf itself.
        private readonly int leafExceptionCount;

        private readonly object describeGate = new();
        private Exception? undescribed;
        private string? topFrame;
        private string? topFrames;

        internal long Count;
        private long reported;

        internal FaultBucket(
            BackgroundFaultSource source,
            string baseExceptionType,
            BackgroundFaultDispatcher? dispatcher,
            bool stopped,
            Exception exception,
            Exception? baseException)
        {
            Source = source;
            this.baseExceptionType = baseExceptionType;
            this.dispatcher = dispatcher;
            this.stopped = stopped;
            exceptionType = exception.GetType().FullName ?? nameof(AggregateException);
            message = BackgroundFaultFrames.Redact(exception.Message);
            leafExceptionCount = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.Count
                : 1;
            undescribed = baseException;
        }

        internal static FaultBucket Overflow(
            BackgroundFaultSource source,
            string baseExceptionType,
            BackgroundFaultDispatcher? dispatcher,
            bool stopped,
            Exception exception)
        {
            // Nothing to describe: the overflow row says only that the limit was hit.
            return new FaultBucket(source, baseExceptionType, dispatcher, stopped, exception, baseException: null)
            {
                topFrames = OverflowFrame,
                topFrame = OverflowFrame
            };
        }

        internal BackgroundFaultSource Source { get; }

        internal bool IsDescribed => Volatile.Read(ref topFrame) is not null;

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
            EnsureDescribed();

            return new BackgroundFaultReport(
                kind,
                Source,
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

        /// <summary>
        /// The PDB-backed walk, done once per bucket by the first reporter to need it — the
        /// first-sighting hop or a flush, both on the pool — and never by an observing thread.
        /// </summary>
        private void EnsureDescribed()
        {
            if (IsDescribed)
            {
                return;
            }

            lock (describeGate)
            {
                if (topFrame is not null)
                {
                    return;
                }

                var exception = undescribed;
                undescribed = null;

                var origin = Source == BackgroundFaultSource.RenderDispatch
                    ? BackgroundFaultFrames.DescribeRenderDispatch(exception, dispatcher?.Owner, dispatcher?.Origin)
                    : BackgroundFaultFrames.Describe(exception);

                // Published last, with release semantics, so a reader that sees the top frame
                // through IsDescribed also sees the joined frames.
                topFrames = origin.TopFrames;
                Volatile.Write(ref topFrame, origin.TopFrame);
            }
        }
    }
}
