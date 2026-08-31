using System.Net.Sockets;

namespace RazorReaper.UnitTests;

public sealed class AppBackgroundIoTests
{
    private const int OperationAbortedNativeError = 995;

    [Fact]
    public void RecognizesDirectOperationAbortedSocketException()
    {
        var exception = new AggregateException(new SocketException(OperationAbortedNativeError));

        Assert.True(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void RecognizesOperationAbortedSocketExceptionNestedInIOException()
    {
        var socketException = new SocketException(OperationAbortedNativeError);
        var exception = new AggregateException(new IOException("Discord IPC read failed.", socketException));

        Assert.True(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void PreservesOperationAbortedIOExceptionHResultHandling()
    {
        var ioException = new IOException(
            "The I/O operation was aborted.",
            unchecked((int)0x800703E3));

        Assert.True(App.IsAbortedBackgroundIo(new AggregateException(ioException)));
    }

    [Fact]
    public void PreservesCancellationHandling()
    {
        Assert.True(App.IsAbortedBackgroundIo(
            new AggregateException(new OperationCanceledException())));
    }

    [Theory]
    [InlineData((int)SocketError.ConnectionReset)]
    [InlineData((int)SocketError.ConnectionRefused)]
    public void RejectsNonOperationAbortedSocketErrors(int nativeErrorCode)
    {
        var exception = new AggregateException(new SocketException(nativeErrorCode));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void RejectsNonOperationAbortedSocketErrorNestedInIOException()
    {
        var socketException = new SocketException((int)SocketError.ConnectionReset);
        var exception = new AggregateException(new IOException("Network read failed.", socketException));

        Assert.False(App.IsAbortedBackgroundIo(exception));
    }

    [Fact]
    public void RejectsMixedBenignAndRealAggregateLeaves()
    {
        var exception = new AggregateException(
            new SocketException(OperationAbortedNativeError),
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
}
