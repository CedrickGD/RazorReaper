using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using RazorReaper.Components;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// RR-E1003 is an unobserved faulted Task. Every test here leans on the fault counter, which only
/// moves once the gate has read <c>Task.Exception</c> — the read that marks the fault observed.
/// </summary>
public sealed class RenderDispatchGateTests
{
    private static RenderDispatchGate NewGate() => new(nameof(RenderDispatchGateTests));

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
    public void ObservesAFaultedDispatch()
    {
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new NullReferenceException()));

        Expect(() => gate.ConsecutiveFaults == 1, "the fault should have been observed");
        Assert.False(gate.IsStopped);
    }

    [Fact]
    public void ObservesAFaultThatArrivesAfterTheDispatchReturns()
    {
        var gate = NewGate();
        var source = new TaskCompletionSource();

        gate.Dispatch(() => source.Task);
        Assert.Equal(0, gate.ConsecutiveFaults);

        source.SetException(new NullReferenceException());

        Expect(() => gate.ConsecutiveFaults == 1, "a late fault should still be observed");
    }

    [Fact]
    public void DoesNotRethrowWhenTheDispatchItselfThrows()
    {
        var gate = NewGate();

        // InvokeAsync throws synchronously when the dispatcher is already gone.
        gate.Dispatch(() => throw new ObjectDisposedException("Renderer"));

        Assert.Equal(1, gate.ConsecutiveFaults);
        Assert.True(gate.IsStopped);
    }

    [Fact]
    public void ObservesACanceledDispatch()
    {
        var gate = NewGate();

        gate.Dispatch(() => Task.FromCanceled(new CancellationToken(canceled: true)));

        Expect(() => gate.ConsecutiveFaults == 1, "cancellation is a fault for breaker purposes");
        Assert.False(gate.IsStopped);
    }

    [Fact]
    public void ResetsTheFaultCountOnASuccessfulDispatch()
    {
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new NullReferenceException()));
        Expect(() => gate.ConsecutiveFaults == 1, "the fault should have been observed");

        gate.Dispatch(() => Task.CompletedTask);

        Assert.Equal(0, gate.ConsecutiveFaults);
    }

    [Fact]
    public void StopsAfterConsecutiveFaults()
    {
        var gate = NewGate();

        for (var i = 1; i <= RenderDispatchGate.MaxConsecutiveFaults; i++)
        {
            Assert.False(gate.IsStopped);
            gate.Dispatch(() => Faulted(new NullReferenceException()));
            Expect(() => gate.ConsecutiveFaults == i, $"fault {i} should have been observed");
        }

        Assert.True(gate.IsStopped);
    }

    [Fact]
    public void KeepsDispatchingWhenFaultsAreSeparatedBySuccesses()
    {
        var gate = NewGate();

        // The production shape is an uninterrupted stream — 240 faults a minute for 25 minutes.
        // A component that still renders in between must not be shut down.
        for (var i = 0; i < RenderDispatchGate.MaxConsecutiveFaults * 5; i++)
        {
            gate.Dispatch(() => Faulted(new NullReferenceException()));
            Expect(() => gate.ConsecutiveFaults == 1, "the fault should have been observed");

            gate.Dispatch(() => Task.CompletedTask);
            Assert.Equal(0, gate.ConsecutiveFaults);
        }

        Assert.False(gate.IsStopped);
    }

    [Theory]
    [InlineData("There is no browser renderer with ID 3.")]
    [InlineData("Cannot process pending renders after the renderer has been disposed.")]
    public void StopsImmediatelyOnADeadRendererInvalidOperation(string message)
    {
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new InvalidOperationException(message)));

        Expect(() => gate.IsStopped, "a dead renderer cannot come back");
    }

    [Fact]
    public void StopsImmediatelyOnADisconnectedJsRuntime()
    {
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new JSDisconnectedException("WebView reloaded.")));

        Expect(() => gate.IsStopped, "a disconnected WebView cannot render");
    }

    [Fact]
    public void KeepsDispatchingForAnUnrelatedInvalidOperationException()
    {
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new InvalidOperationException("Sequence contains no elements.")));

        Expect(() => gate.ConsecutiveFaults == 1, "the fault should have been observed");
        Assert.False(gate.IsStopped);
    }

    [Fact]
    public void StoppedGateNeverStartsTheDispatch()
    {
        var gate = NewGate();
        var started = 0;

        gate.Stop();
        gate.Dispatch(() =>
        {
            Interlocked.Increment(ref started);
            return Task.CompletedTask;
        });

        Assert.Equal(0, started);
    }

    [Fact]
    public void OnlyTheFirstStopCallReportsTheTransition()
    {
        var gate = NewGate();

        Assert.True(gate.Stop());
        Assert.False(gate.Stop());
    }

    [Fact]
    public void LogsTeardownAtDebugAndAnUnexpectedFaultOnce()
    {
        var gate = NewGate();
        var logger = new RecordingLogger<RenderDispatchGateTests>();

        gate.Dispatch(() => Faulted(new TaskCanceledException()), logger);
        Expect(() => logger.Entries.Count == 1, "the teardown fault should have been logged");
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));

        gate.Dispatch(() => Faulted(new NullReferenceException()), logger);
        Expect(
            () => logger.Entries.Count(entry => entry.Level == LogLevel.Warning) == 1,
            "the first unexpected fault should warn once");

        // A 2 Hz stream of faults must not become a 2 Hz stream of warnings.
        for (var i = 0; i < 5; i++)
        {
            gate.Dispatch(() => Faulted(new NullReferenceException()), logger);
        }

        Expect(() => logger.Entries.Count >= 7, "every fault should have been logged");
        Assert.Equal(1, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void LogsOnceMoreWhenItStops()
    {
        var gate = NewGate();
        var logger = new RecordingLogger<RenderDispatchGateTests>();

        gate.Dispatch(() => Faulted(new ObjectDisposedException("Renderer")), logger);

        Expect(
            () => logger.Count("stopped dispatching renders") == 1,
            "stopping is the one thing worth finding in a support log");
    }

    [Fact]
    public void WorksWithoutALogger()
    {
        var gate = NewGate();

        gate.Dispatch(() => Faulted(new NullReferenceException()));

        Expect(() => gate.ConsecutiveFaults == 1, "a missing logger must not skip the observation");
    }
}
