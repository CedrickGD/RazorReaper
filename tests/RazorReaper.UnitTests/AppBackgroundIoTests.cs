using System.Net.Sockets;

namespace RazorReaper.UnitTests;

/// <summary>
/// The suppression predicate is scoped to the Discord RPC pipe's abandoned read and nothing
/// else. Until v1.5.2 it matched any aborted-socket leaf, which swallowed the app's own orphaned
/// UDP receives: SocketException went to exactly zero on 1.4.10/1.5.0/1.5.2 while unfixed 1.4.8.11
/// installs kept reporting it for another twelve days. Quiet is not the same as fixed.
/// </summary>
public sealed class AppBackgroundIoTests
{
    private const int OperationAbortedNativeError = 995;
    private const int OperationAbortedHResult = unchecked((int)0x800703E3);

    [Fact]
    public void ReportsBareOperationAbortedSocketException()
    {
        // Our own ServerQueryService orphaning a UdpClient receive, not Discord. An app bug,
        // so it has to reach telemetry.
        var exception = new AggregateException(new SocketException(OperationAbortedNativeError));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void ReportsBareOperationAbortedSocketErrorCode()
    {
        var socketException = new SocketException((int)SocketError.OperationAborted);

        Assert.False(App.IsAbortedBackgroundIo(new AggregateException(socketException)));
    }

    [Fact]
    public void SuppressesOperationAbortedIOExceptionHResult()
    {
        // The genuine shape: NamedPipeClientStream's aborted BeginRead.
        var ioException = new IOException("The I/O operation was aborted.", OperationAbortedHResult);

        Assert.True(App.IsAbortedBackgroundIo(new AggregateException(ioException)));
    }

    [Fact]
    public void SuppressesOperationAbortedSocketExceptionWrappedInIOException()
    {
        var socketException = new SocketException(OperationAbortedNativeError);
        var exception = new AggregateException(new IOException("Discord IPC read failed.", socketException));

        Assert.True(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void SuppressesCancellationWrappedInIOException()
    {
        var exception = new AggregateException(
            new IOException("Discord IPC read canceled.", new OperationCanceledException()));

        Assert.True(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void ReportsBareCancellation()
    {
        // A cancellation nobody observed is still the app's own dropped task, not pipe noise.
        Assert.False(App.IsAbortedBackgroundIo(
            new AggregateException(new OperationCanceledException())));
    }

    [Theory]
    [InlineData((int)SocketError.ConnectionReset)]
    [InlineData((int)SocketError.ConnectionRefused)]
    public void ReportsNonOperationAbortedSocketErrors(int nativeErrorCode)
    {
        var exception = new AggregateException(new SocketException(nativeErrorCode));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void ReportsNonOperationAbortedSocketErrorNestedInIOException()
    {
        var socketException = new SocketException((int)SocketError.ConnectionReset);
        var exception = new AggregateException(new IOException("Network read failed.", socketException));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void ReportsMixedBenignAndRealAggregateLeaves()
    {
        var exception = new AggregateException(
            new IOException("Discord IPC read failed.", OperationAbortedHResult),
            new InvalidOperationException("Real background failure."));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void DoesNotFollowNonIoWrappers()
    {
        var socketException = new SocketException(OperationAbortedNativeError);
        var exception = new AggregateException(
            new InvalidOperationException("Unexpected operation failed.", socketException));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void ReportsNullReferenceException()
    {
        // The 74 % family. It was never suppressed, and it must stay that way.
        Assert.False(App.IsAbortedBackgroundIo(
            new AggregateException(new NullReferenceException())));
    }

    [Fact]
    public void ReportsEmptyAggregate()
    {
        Assert.False(App.IsAbortedBackgroundIo(new AggregateException()));
    }

    [Fact]
    public void StopsFollowingDeeplyNestedWrappers()
    {
        // A chain deeper than Discord ever produces is reported rather than recursed into:
        // this predicate runs on the finalizer thread.
        Exception nested = new SocketException(OperationAbortedNativeError);
        for (var depth = 0; depth < 12; depth++)
        {
            nested = new IOException("Wrapped.", nested);
        }

        Assert.False(App.IsAbortedBackgroundIo(new AggregateException(nested)));
    }
}
