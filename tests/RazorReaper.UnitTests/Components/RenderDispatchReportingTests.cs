using System.Collections.Concurrent;
using RazorReaper.Components;
using RazorReaper.Diagnostics;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// The gate observes every render-dispatch fault, which is what stops RR-E1003 — and until this
/// commit it told nobody but the local Serilog file, which is exactly how v1.4.10's Discord
/// filter turned a live fault family into eight weeks of silence that read as a fix. These tests
/// pin the seam that carries them out: it reports the faults worth seeing, it never becomes a
/// fault itself, and it is a silent no-op until App installs it.
///
/// The sink is process-wide state, so every test that touches it lives in this one class — xUnit
/// never runs two tests from the same class in parallel — and every sink here ignores faults
/// from any other owner, so a gate in another test class running concurrently cannot land in
/// these counts.
/// </summary>
public sealed class RenderDispatchReportingTests
{
    private const string Owner = nameof(RenderDispatchReportingTests);

    private static RenderDispatchGate NewGate() => new(Owner);

    private static Task Faulted(Exception exception) => Task.FromException(exception);

    /// <summary>Waits for a continuation that normally runs inline, so the tests cannot race it.</summary>
    private static void Expect(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1);
        }

        Assert.True(condition(), because);
    }

    [Fact]
    public void ANullSinkIsANoOp()
    {
        // Before startup finishes, and in every unit test that does not install one.
        using var scope = SinkScope.None();
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new NullReferenceException()));

        Expect(() => gate.ConsecutiveFaults == 1, "the fault must still be observed with no sink");
        Assert.False(RenderDispatchReporting.HasSink);
    }

    [Fact]
    public void ReportsTheFaultFamilyThatWouldOtherwiseBeSilenced()
    {
        var reported = new ConcurrentQueue<RenderDispatchFault>();
        using var scope = SinkScope.Collecting(reported);
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new NullReferenceException("boom")), origin: "OnTick");

        Expect(() => reported.Count == 1, "the NullReferenceException family must reach the reporter");
        Assert.True(reported.TryDequeue(out var fault));
        Assert.IsType<NullReferenceException>(fault.Exception);
        Assert.Equal(Owner, fault.Owner);
        Assert.Equal("OnTick", fault.Origin);
        Assert.Equal(1, fault.ConsecutiveFaults);
        Assert.False(fault.Stopped);
    }

    [Fact]
    public void DoesNotReportOrdinaryTeardown()
    {
        var reported = new ConcurrentQueue<RenderDispatchFault>();
        using var scope = SinkScope.Collecting(reported);
        var gate = NewGate();

        // Navigating away cancels in-flight dispatches on every page in the app. Reporting those
        // would spend the tracker's 64 buckets on shutdown noise and crowd out the real faults.
        gate.Dispatch(() => Faulted(new TaskCanceledException()), origin: "OnDispose");

        Expect(() => gate.ConsecutiveFaults == 1, "the fault should still have been observed");
        Assert.Empty(reported);
    }

    [Fact]
    public void ReportsTheBreakerTrip()
    {
        var reported = new ConcurrentQueue<RenderDispatchFault>();
        using var scope = SinkScope.Collecting(reported);
        var gate = NewGate();

        // A component that permanently stops rendering is the most diagnostic thing this class
        // can say, and it used to be one Warning in a file nobody uploads. Teardown-shaped faults
        // are not reported one by one, so ten of them in a row prove the trip is its own event.
        for (var i = 0; i < RenderDispatchGate.MaxConsecutiveFaults; i++)
        {
            gate.Dispatch(() => Faulted(new TaskCanceledException()));
        }

        Expect(() => gate.IsStopped, "ten consecutive faults must trip the breaker");
        var trip = Assert.Single(reported);
        Assert.True(trip.Stopped);
        Assert.Equal(RenderDispatchGate.MaxConsecutiveFaults, trip.ConsecutiveFaults);
    }

    [Fact]
    public void ASinkThatThrowsNeverEscapesAndNeverBlocksTheNextReport()
    {
        var calls = 0;
        using var scope = SinkScope.Running(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("the reporter is broken");
        });
        var gate = NewGate();

        // The sink runs inside the dispatch continuation. A throw would fault that continuation's
        // Task — an unobserved faulted Task, which is the defect this whole branch exists to end.
        gate.Dispatch(() => Faulted(new NullReferenceException()));
        Expect(() => Volatile.Read(ref calls) == 1, "the first fault should have reached the sink");

        gate.Dispatch(() => Faulted(new NullReferenceException()));

        // Proves the re-entrancy flag is released on the throwing path too.
        Expect(() => Volatile.Read(ref calls) == 2, "a throwing sink must not block the next report");
        Assert.Equal(2, gate.ConsecutiveFaults);
    }

    [Fact]
    public void ASinkThatFaultsItsOwnDispatchDoesNotRecurse()
    {
        var calls = 0;
        RenderDispatchGate? gate = null;
        using var scope = SinkScope.Running(_ =>
        {
            Interlocked.Increment(ref calls);

            // Without the guard this is unbounded recursion on the reporting thread: reporting a
            // fault dispatches a render, that dispatch faults, and it reports again.
            gate!.Dispatch(() => Faulted(new NullReferenceException("from the sink")));
        });

        gate = NewGate();
        gate.Dispatch(() => Faulted(new NullReferenceException("first")));

        Expect(() => Volatile.Read(ref calls) >= 1, "the first fault should have reached the sink");
        Thread.Sleep(50);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task TheRecursionGuardIsPerThreadSoConcurrentReportsAreNotDropped()
    {
        // The sink is called from the renderer's dispatcher, from timer threads and from the
        // finalizer thread. A process-wide guard would silently drop whichever fault arrived
        // while another thread was reporting — the reporting bug all over again.
        var reported = new ConcurrentQueue<RenderDispatchFault>();
        using var scope = SinkScope.Collecting(reported, beforeEnqueue: () => Thread.Sleep(5));

        const int Threads = 8;
        await Task.WhenAll(Enumerable.Range(0, Threads).Select(i => Task.Run(() =>
            NewGate().Dispatch(() => Faulted(new NullReferenceException()), origin: "OnTick"))));

        Expect(() => reported.Count == Threads, "every thread's fault must be reported");
    }

    [Fact]
    public void ReportsOnTheObservingThreadWithoutPostingBackToItsSynchronizationContext()
    {
        // Stands in for the renderer's dispatcher: the sink runs where the fault was observed
        // rather than being marshalled onto a context that may already be tearing down.
        var reported = new ConcurrentQueue<int>();
        using var scope = SinkScope.Running(_ => reported.Enqueue(Environment.CurrentManagedThreadId));

        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            NewGate().Dispatch(() => Faulted(new NullReferenceException()));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Expect(() => reported.Count == 1, "the fault should have been reported");
        Assert.True(reported.TryDequeue(out var threadId));
        Assert.Equal(Environment.CurrentManagedThreadId, threadId);
        Assert.Equal(0, context.Posts);
    }

    [Fact]
    public void CarriesNoPersonalDataEvenWhenACallerPassesAPathAsTheOrigin()
    {
        var reported = new ConcurrentQueue<RenderDispatchFault>();
        using var scope = SinkScope.Collecting(reported);

        // origin is an ordinary optional parameter, so nothing guarantees it is a member name.
        NewGate().Dispatch(
            () => Faulted(new NullReferenceException()),
            origin: @"C:\Users\someone\AppData\Local\RazorReaper");

        Expect(() => reported.Count == 1, "the fault should have been reported");
        Assert.True(reported.TryDequeue(out var fault));

        // The gate passes origin through verbatim; the tracker is where anything that is not a
        // member name is dropped, so that one sanitiser covers this sink and every future one.
        var report = new BackgroundFaultTracker()
            .RecordRenderDispatch(fault.Exception, fault.Owner, fault.Origin, fault.Stopped);

        Assert.NotNull(report);
        Assert.Equal(BackgroundFaultFrames.UnknownMember, report!.Origin);
        Assert.DoesNotContain("someone", report.TopFrames, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installs a sink for the duration of one test and clears it afterwards.</summary>
    private sealed class SinkScope : IDisposable
    {
        private SinkScope(Action<RenderDispatchFault>? sink) => RenderDispatchReporting.UseSink(sink);

        public static SinkScope None() => new(null);

        /// <summary>Runs <paramref name="sink"/> for this class's own faults and ignores the rest.</summary>
        public static SinkScope Running(Action<RenderDispatchFault> sink)
            => new(fault =>
            {
                if (fault.Owner == Owner)
                {
                    sink(fault);
                }
            });

        public static SinkScope Collecting(
            ConcurrentQueue<RenderDispatchFault> reported,
            Action? beforeEnqueue = null)
            => Running(fault =>
            {
                beforeEnqueue?.Invoke();
                reported.Enqueue(fault);
            });

        public void Dispose() => RenderDispatchReporting.UseSink(null);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int posts;

        public int Posts => Volatile.Read(ref posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref posts);
            base.Post(d, state);
        }
    }
}
