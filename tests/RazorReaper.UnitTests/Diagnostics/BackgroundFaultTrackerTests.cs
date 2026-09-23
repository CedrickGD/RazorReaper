using RazorReaper.Diagnostics;

namespace RazorReaper.UnitTests.Diagnostics;

/// <summary>
/// Dampening contract: a pathological session costs a couple of rows instead of 36,456, and not
/// one fault goes missing — every row carries the number of faults it stands for, so summing
/// Occurrences reproduces the true count.
/// </summary>
public sealed class BackgroundFaultTrackerTests
{
    [Fact]
    public void ReportsTheFirstOccurrenceInFull()
    {
        var tracker = new BackgroundFaultTracker();

        var report = Describe(tracker.Record(Fault(new InvalidOperationException("boom"))));

        Assert.NotNull(report);
        Assert.Equal(BackgroundFaultReportKind.First, report!.Kind);
        Assert.Equal(1, report.Occurrences);
        Assert.Equal(typeof(InvalidOperationException).FullName, report.BaseExceptionType);
        Assert.Equal(typeof(AggregateException).FullName, report.ExceptionType);
        Assert.Equal(1, report.LeafExceptionCount);
        Assert.NotNull(report.TopFrame);
    }

    [Fact]
    public void FoldsRepeatsIntoACountInsteadOfRows()
    {
        var tracker = new BackgroundFaultTracker();

        var first = Describe(tracker.Record(Fault(new InvalidOperationException("boom"))));
        for (var i = 0; i < 36_455; i++)
        {
            Assert.Null(tracker.Record(Fault(new InvalidOperationException("boom"))));
        }

        var rollup = Assert.Single(tracker.Flush());

        // The production worst case: one session, 36,456 faults, now two rows.
        Assert.Equal(BackgroundFaultReportKind.Rollup, rollup.Kind);
        Assert.Equal(36_455, rollup.Occurrences);
        Assert.Equal(36_456, first!.Occurrences + rollup.Occurrences);
    }

    [Fact]
    public void KeepsDistinctBaseTypesApart()
    {
        var tracker = new BackgroundFaultTracker();

        Assert.NotNull(tracker.Record(Fault(new InvalidOperationException("a"))));
        Assert.NotNull(tracker.Record(Fault(new NullReferenceException("b"))));
    }

    [Fact]
    public void KeepsDistinctFramesApart()
    {
        var tracker = new BackgroundFaultTracker();

        // Same exception type, two different throw sites: the whole point of carrying a frame.
        var fromFirstSite = Describe(tracker.Record(Fault(ThrowFromFirstSite())));
        var fromSecondSite = Describe(tracker.Record(Fault(ThrowFromSecondSite())));

        Assert.NotNull(fromFirstSite);
        Assert.NotNull(fromSecondSite);
        Assert.NotEqual(fromFirstSite!.TopFrame, fromSecondSite!.TopFrame);
    }

    [Fact]
    public void TreatsTheSameFrameAsOneFaultAcrossInstances()
    {
        var tracker = new BackgroundFaultTracker();

        Assert.NotNull(tracker.Record(Fault(ThrowFromFirstSite())));
        Assert.Null(tracker.Record(Fault(ThrowFromFirstSite())));
    }

    [Fact]
    public void FlushReturnsNothingWhenNothingIsNew()
    {
        var tracker = new BackgroundFaultTracker();

        Assert.Empty(tracker.Flush());

        tracker.Record(Fault(new InvalidOperationException("boom")));

        // The first occurrence was already reported by Record; a flush must not resend it.
        Assert.Empty(tracker.Flush());
    }

    [Fact]
    public void SuppressionCountRidesOutOnTheNextReportedEvent()
    {
        var tracker = new BackgroundFaultTracker();

        for (var i = 0; i < 7; i++)
        {
            tracker.RecordSuppressed(Fault(new IOException("pipe")));
        }

        var report = Describe(tracker.Record(Fault(new InvalidOperationException("boom"))));

        Assert.Equal(7, report!.SuppressedAbortedIo);
    }

    [Fact]
    public void SuppressionAloneStillProducesARow()
    {
        var tracker = new BackgroundFaultTracker();

        for (var i = 0; i < 4; i++)
        {
            tracker.RecordSuppressed(Fault(new IOException("pipe")));
        }

        var report = Assert.Single(tracker.Flush());

        // A version that is merely quiet must never again read as fixed.
        Assert.Equal(BackgroundFaultReportKind.Suppressed, report.Kind);
        Assert.Equal(4, report.SuppressedAbortedIo);
        Assert.Equal(4, report.Occurrences);
        Assert.Equal(typeof(IOException).FullName, report.BaseExceptionType);
    }

    [Fact]
    public void SuppressionCountIsDrainedSoItIsNeverDoubleCounted()
    {
        var tracker = new BackgroundFaultTracker();
        tracker.RecordSuppressed(Fault(new IOException("pipe")));

        Assert.Equal(1, Assert.Single(tracker.Flush()).SuppressedAbortedIo);
        Assert.Empty(tracker.Flush());
    }

    [Fact]
    public void OnlyTheFirstSuppressionAsksForALocalLogLine()
    {
        var tracker = new BackgroundFaultTracker();

        Assert.True(tracker.RecordSuppressed(Fault(new IOException("pipe"))));
        Assert.False(tracker.RecordSuppressed(Fault(new IOException("pipe"))));
    }

    [Fact]
    public void BoundsTheNumberOfDistinctFaultsItWillTrack()
    {
        var tracker = new BackgroundFaultTracker();
        var reports = new List<BackgroundFaultReport>();

        foreach (var exception in DistinctExceptions(BackgroundFaultTracker.MaxTrackedFaults + 20))
        {
            var report = Describe(tracker.Record(Fault(exception)));
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        // Memory a pathological session can pin is capped; the overflow is still counted, it
        // just stops being told apart.
        Assert.True(reports.Count <= BackgroundFaultTracker.MaxTrackedFaults + 1);
        Assert.Contains(reports, report => report.TopFrame == BackgroundFaultTracker.OverflowFrame);
    }

    /// <summary>
    /// The render key is far more granular than the unobserved one — owner, member and breaker
    /// state on top of type and site — so under a shared budget one page churning distinct
    /// dispatchers spent every bucket, and every later finalizer fault, NullReferenceException
    /// included, collapsed into the overflow row and lost its frame: the exact field this work
    /// exists to add, lost in exactly the broken session where it is needed.
    /// </summary>
    [Fact]
    public void ARenderComponentChurningDistinctDispatchersCannotCrowdOutTheFinalizersFaults()
    {
        var tracker = new BackgroundFaultTracker();

        var render = new List<BackgroundFaultReport>();
        for (var i = 0; i < BackgroundFaultTracker.MaxTrackedFaults + 20; i++)
        {
            var report = Describe(tracker.RecordRenderDispatch(NoOwnFrames(), $"Component{i}", "OnTick", stopped: false));
            if (report is not null)
            {
                render.Add(report);
            }
        }

        // The render budget is spent.
        Assert.Contains(render, report => report.TopFrame == BackgroundFaultTracker.OverflowFrame);

        // The finalizer's fault still gets its own row, with its own frame.
        var unobserved = Describe(tracker.Record(Fault(ThrowFromFirstSite())));

        Assert.NotNull(unobserved);
        Assert.NotEqual(BackgroundFaultTracker.OverflowFrame, unobserved!.TopFrame);
        Assert.Contains(nameof(ThrowFromFirstSite), unobserved.TopFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void AFinalizerFloodOfDistinctTypesCannotCrowdOutRenderFaults()
    {
        var tracker = new BackgroundFaultTracker();

        var unobserved = new List<BackgroundFaultReport>();
        foreach (var exception in DistinctExceptions(BackgroundFaultTracker.MaxTrackedFaults + 20))
        {
            var report = Describe(tracker.Record(Fault(exception)));
            if (report is not null)
            {
                unobserved.Add(report);
            }
        }

        Assert.Contains(unobserved, report => report.TopFrame == BackgroundFaultTracker.OverflowFrame);

        // A component that breaks now is still told apart from everything else.
        var render = Describe(tracker.RecordRenderDispatch(ThrowFromFirstSite(), "Home", "OnTick", stopped: false));

        Assert.NotNull(render);
        Assert.NotEqual(BackgroundFaultTracker.OverflowFrame, render!.TopFrame);
        Assert.Equal("Home", render.Owner);
        Assert.Contains(nameof(ThrowFromFirstSite), render.TopFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBudgetsSpentStillLeaveTheTotalBoundedAndEachOverflowRowLabelledWithItsSource()
    {
        var tracker = new BackgroundFaultTracker();
        var reports = new List<BackgroundFaultReport>();

        foreach (var exception in DistinctExceptions(BackgroundFaultTracker.MaxTrackedFaults + 20))
        {
            if (Describe(tracker.Record(Fault(exception))) is { } report)
            {
                reports.Add(report);
            }
        }

        for (var i = 0; i < BackgroundFaultTracker.MaxTrackedFaults + 20; i++)
        {
            if (Describe(tracker.RecordRenderDispatch(NoOwnFrames(), $"Component{i}", "OnTick", stopped: false)) is { } report)
            {
                reports.Add(report);
            }
        }

        // Each source is at its cap plus its own overflow bucket, and nothing beyond that.
        Assert.Equal(BackgroundFaultTracker.MaxTrackedFaultsTotal, tracker.TrackedFaultCount);

        // Under the shared dictionary one overflow bucket served both sources and wore whichever
        // source hit the limit first, so the other source's overflow was reported as the wrong
        // population. Each budget now overflows into a row of its own kind.
        var overflow = reports.Where(report => report.TopFrame == BackgroundFaultTracker.OverflowFrame).ToList();
        Assert.Contains(overflow, report => report.Source == BackgroundFaultSource.UnobservedTask);
        Assert.Contains(overflow, report => report.Source == BackgroundFaultSource.RenderDispatch);
    }

    [Fact]
    public async Task LosesNoFaultUnderConcurrentRecordAndFlush()
    {
        // UnobservedTaskException arrives on the finalizer thread while the 5-minute flush timer
        // runs on a pool thread. Every occurrence must be claimed exactly once by exactly one of
        // them: no double-counting, no silent loss.
        var tracker = new BackgroundFaultTracker();
        const int writers = 8;
        const int perWriter = 2_000;

        long reported = 0;
        var flushing = true;

        var flusher = Task.Run(() =>
        {
            long drained = 0;
            while (Volatile.Read(ref flushing))
            {
                drained += tracker.Flush().Sum(report => report.Occurrences);
            }

            return drained;
        });

        Parallel.For(0, writers, _ =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var report = Describe(tracker.Record(Fault(new InvalidOperationException("boom"))));
                if (report is not null)
                {
                    Interlocked.Add(ref reported, report.Occurrences);
                }
            }
        });

        Volatile.Write(ref flushing, false);
        var flushed = await flusher;
        flushed += tracker.Flush().Sum(report => report.Occurrences);

        Assert.Equal(writers * perWriter, Interlocked.Read(ref reported) + flushed);
    }

    /// <summary>
    /// The render gate's faults go through the same claim protocol and the same Occurrences
    /// fold as the finalizer's. A component faulting every tick — the production shape is 4 a
    /// second for 2.5 hours — must cost one row plus a count, not a POST per tick, and the panel
    /// must still be able to tell two broken components apart.
    /// </summary>
    [Fact]
    public void FoldsRepeatedRenderDispatchFaultsIntoACountLikeTheFinalizersAre()
    {
        var tracker = new BackgroundFaultTracker();

        var first = Describe(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: false));
        for (var i = 0; i < 35_999; i++)
        {
            Assert.Null(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: false));
        }

        var rollup = Assert.Single(tracker.Flush());

        Assert.NotNull(first);
        Assert.Equal(BackgroundFaultReportKind.First, first!.Kind);
        Assert.Equal(BackgroundFaultSource.RenderDispatch, first.Source);
        Assert.Equal(36_000, first.Occurrences + rollup.Occurrences);
    }

    [Fact]
    public void KeepsBrokenComponentsApartEvenWhenTheStackCannot()
    {
        var tracker = new BackgroundFaultTracker();

        // A dispatch that faults because its renderer is gone throws inside the framework, so
        // both of these carry the same (absent) RazorReaper frame. Only the owner tells them
        // apart, which is the whole reason the gate's owner/origin is worth carrying.
        var home = Describe(tracker.RecordRenderDispatch(NoOwnFrames(), "Home", "OnTick", stopped: false));
        var account = Describe(tracker.RecordRenderDispatch(NoOwnFrames(), "Account", "OnTick", stopped: false));
        var otherMember = Describe(tracker.RecordRenderDispatch(NoOwnFrames(), "Home", "OnPoll", stopped: false));

        Assert.NotNull(home);
        Assert.NotNull(account);
        Assert.NotNull(otherMember);
        Assert.Equal("Home", home!.Owner);
        Assert.Equal("Account", account!.Owner);
        Assert.Equal("OnPoll", otherMember!.Origin);
    }

    [Fact]
    public void KeepsRenderFaultsApartFromTheIdenticalUnobservedOne()
    {
        var tracker = new BackgroundFaultTracker();

        // Same base type, same session. They are two populations — one caught at the render, one
        // republished by the finalizer — and folding them together would hide which is which.
        Assert.NotNull(tracker.Record(Fault(new NullReferenceException())));
        Assert.NotNull(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: false));
    }

    [Fact]
    public void TheBreakerTripIsItsOwnRowRatherThanAFoldedRepeat()
    {
        var tracker = new BackgroundFaultTracker();

        Assert.NotNull(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: false));
        Assert.Null(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: false));

        // The component has permanently stopped rendering. That is a different event from the
        // faults that got it there, so it must not be swallowed as their 3rd occurrence.
        var stop = Describe(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: true));

        Assert.NotNull(stop);
        Assert.True(stop!.RenderStopped);
    }

    [Fact]
    public void CarriesTheOwnerTheOriginAndTheTopOwnFrame()
    {
        var tracker = new BackgroundFaultTracker();

        var report = Describe(tracker.RecordRenderDispatch(ThrowFromFirstSite(), "Home", "OnTick", stopped: false));

        Assert.NotNull(report);
        Assert.Equal("Home", report!.Owner);
        Assert.Equal("OnTick", report.Origin);
        Assert.Equal(typeof(InvalidOperationException).FullName, report.BaseExceptionType);
        Assert.Equal(typeof(InvalidOperationException).FullName, report.ExceptionType);

        // A real own frame wins over the gate's own name, and the gate's name still rides along
        // in top_frames so the component is never lost.
        Assert.Contains(nameof(ThrowFromFirstSite), report.TopFrame, StringComparison.Ordinal);
        Assert.Contains("Home.OnTick", report.TopFrames, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToTheComponentWhenTheStackHasNoOwnFrame()
    {
        var tracker = new BackgroundFaultTracker();

        var report = Describe(tracker.RecordRenderDispatch(NoOwnFrames(), "Home", "OnTick", stopped: false));

        // Otherwise the whole family reports as "(no RazorReaper frame)" and stays exactly as
        // unattributable as the ~62k NullReferenceException rows are today.
        Assert.Equal("Home.OnTick", report!.TopFrame);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\RazorReaper")]
    [InlineData(@"\\NAS-BOX\share\build")]
    [InlineData("someone@example.com")]
    [InlineData("Home OnTick")]
    public void DropsAnOriginThatIsNotAMemberNameRatherThanScrubbingIt(string origin)
    {
        var tracker = new BackgroundFaultTracker();

        var report = Describe(tracker.RecordRenderDispatch(NoOwnFrames(), "Home", origin, stopped: false));

        // Salvaging the letters would leave the user name or the host behind. Nothing that is not
        // already a C# member name may leave the process.
        Assert.NotNull(report);
        Assert.Equal(BackgroundFaultFrames.UnknownMember, report!.Origin);
        Assert.Equal($"Home.{BackgroundFaultFrames.UnknownMember}", report.TopFrame);
    }

    [Fact]
    public async Task LosesNoRenderFaultUnderConcurrentRecordAndFlush()
    {
        // The gate reports from the renderer's dispatcher, from timer threads and from the
        // finalizer thread, while the 5-minute flush timer runs on a pool thread.
        var tracker = new BackgroundFaultTracker();
        const int Writers = 8;
        const int PerWriter = 2_000;

        long reported = 0;
        var flushing = true;

        var flusher = Task.Run(() =>
        {
            long drained = 0;
            while (Volatile.Read(ref flushing))
            {
                drained += tracker.Flush().Sum(report => report.Occurrences);
            }

            return drained;
        });

        Parallel.For(0, Writers, _ =>
        {
            for (var i = 0; i < PerWriter; i++)
            {
                var report = Describe(tracker.RecordRenderDispatch(new NullReferenceException(), "Home", "OnTick", stopped: false));
                if (report is not null)
                {
                    Interlocked.Add(ref reported, report.Occurrences);
                }
            }
        });

        Volatile.Write(ref flushing, false);
        var flushed = await flusher;
        flushed += tracker.Flush().Sum(report => report.Occurrences);

        Assert.Equal(Writers * PerWriter, Interlocked.Read(ref reported) + flushed);
    }

    /// <summary>
    /// Describing the frames is a PDB-backed stack walk, and the thread that observes a render
    /// fault is the renderer's own dispatcher. So the observing thread decides everything cheap —
    /// the bucket, the claim, the row's kind — and leaves the walk to whoever reports the row.
    /// </summary>
    [Fact]
    public void DescribesTheFramesOnTheReportingPathNotWhereTheFaultWasObserved()
    {
        var tracker = new BackgroundFaultTracker();

        var render = tracker.RecordRenderDispatch(ThrowFromFirstSite(), "Home", "OnTick", stopped: false);
        var unobserved = tracker.Record(Fault(ThrowFromSecondSite()));

        Assert.NotNull(render);
        Assert.NotNull(unobserved);
        Assert.False(render!.IsDescribed);
        Assert.False(unobserved!.IsDescribed);

        var renderReport = render.Describe();
        var unobservedReport = unobserved.Describe();

        // Nothing the panel expects is lost by waiting: the same frames, from the same exception.
        Assert.True(render.IsDescribed);
        Assert.Contains(nameof(ThrowFromFirstSite), renderReport.TopFrame, StringComparison.Ordinal);
        Assert.Contains("Home.OnTick", renderReport.TopFrames, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowFromSecondSite), unobservedReport.TopFrame, StringComparison.Ordinal);

        // Idempotent: a second reporter gets the same row, not a second walk's worth of drift.
        Assert.Equal(renderReport.TopFrame, render.Describe().TopFrame);
    }

    [Fact]
    public void AFlushDescribesABucketNoReporterHasFinishedYet()
    {
        var tracker = new BackgroundFaultTracker();

        // The first sighting's reporter has not run yet (or never will); the repeats still ship.
        var sighting = tracker.RecordRenderDispatch(ThrowFromFirstSite(), "Home", "OnTick", stopped: false);
        Assert.Null(tracker.RecordRenderDispatch(ThrowFromFirstSite(), "Home", "OnTick", stopped: false));

        var rollup = Assert.Single(tracker.Flush());

        Assert.Contains(nameof(ThrowFromFirstSite), rollup.TopFrame, StringComparison.Ordinal);
        Assert.True(sighting!.IsDescribed);
        Assert.Equal(rollup.TopFrame, sighting.Describe().TopFrame);
    }

    /// <summary>
    /// Until this change every call to RecordRenderDispatch described the frames — before the
    /// dedupe, on whichever thread observed the fault — so a component faulting at Crosshair's
    /// 20 Hz paid a symbol lookup per tick on the thread that draws the UI. Now a repeat costs a
    /// symbol-free site capture and an interlocked count, and never touches the PDB-backed walk
    /// that describing needs.
    ///
    /// This used to time one call to <see cref="BackgroundFaultFrames.DescribeRenderDispatch"/>
    /// against one repeat call (2,000 iterations, best of 5 rounds) and assert the repeat came in
    /// faster. It failed about once a night while a game or a build loaded the machine: two
    /// separately-timed microsecond loops, and the scheduler noise between them was the same size
    /// as the gap the test was checking for. So instead of timing anything, this asks the question
    /// the timing was a proxy for — did a repeat perform the walk at all? — directly, with the
    /// tracker's own seam for that: <see cref="PendingBackgroundFaultReport.IsDescribed"/>, already
    /// used by <see cref="DescribesTheFramesOnTheReportingPathNotWhereTheFaultWasObserved"/> above.
    /// Ten thousand repeats on the same key, and the first sighting's bucket is still undescribed —
    /// no repeat did anything a description would have left a trace of. Deterministic at any load,
    /// because nothing here is timed.
    /// </summary>
    [Fact]
    public void ARepeatOnTheObservingThreadCostsLessThanOneFrameDescription()
    {
        var exception = ThrowFromFirstSite();
        var tracker = new BackgroundFaultTracker();

        var first = tracker.RecordRenderDispatch(exception, "Home", "OnTick", stopped: false);
        Assert.NotNull(first);
        Assert.False(first!.IsDescribed);

        for (var i = 0; i < 10_000; i++)
        {
            // Same key as `first`, so every one of these is a repeat, folded into its count.
            Assert.Null(tracker.RecordRenderDispatch(exception, "Home", "OnTick", stopped: false));
        }

        // The claim itself: none of the 10,000 repeats touched the PDB-backed walk. If even one
        // had described the frames instead of just counting, `first` would already read as
        // described here — this is the same bucket a repeat and `first` both key onto.
        Assert.False(
            first.IsDescribed,
            "a repeat must cost the observing thread nothing that a frame description would leave behind");

        // And describing still works, exactly once, whenever it is actually asked for.
        var report = first.Describe();
        Assert.True(first.IsDescribed);
        Assert.Contains(nameof(ThrowFromFirstSite), report.TopFrame, StringComparison.Ordinal);
    }

    /// <summary>What App does on the pool: finish the row. Null stays null so repeats read as before.</summary>
    private static BackgroundFaultReport? Describe(PendingBackgroundFaultReport? sighting) => sighting?.Describe();

    private static AggregateException Fault(Exception exception) => new(exception);

    /// <summary>
    /// A never-thrown exception has no stack at all — the same thing BackgroundFaultFrames sees
    /// when a dispatch faults inside the framework's dispatcher with no RazorReaper frame left.
    /// </summary>
    private static NullReferenceException NoOwnFrames() => new("renderer is gone");

    private static IEnumerable<Exception> DistinctExceptions(int count)
    {
        return typeof(Exception).Assembly
            .GetTypes()
            .Where(type => typeof(Exception).IsAssignableFrom(type)
                && !type.IsAbstract
                && !type.ContainsGenericParameters
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .Take(count)
            .Select(type => (Exception)Activator.CreateInstance(type)!);
    }

    private static InvalidOperationException ThrowFromFirstSite()
    {
        try
        {
            throw new InvalidOperationException("first site");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static InvalidOperationException ThrowFromSecondSite()
    {
        try
        {
            throw new InvalidOperationException("second site");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
