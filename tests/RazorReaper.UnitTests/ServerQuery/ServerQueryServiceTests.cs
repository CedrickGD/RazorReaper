using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.ServerQuery;

namespace RazorReaper.UnitTests.ServerQuery;

/// <summary>
/// RR-E1003: every A2S receive used to be abandoned by <c>Task.WaitAsync(timeout)</c> and then
/// have the socket closed under it, which faulted the orphan with WinSock 995 on a task nobody
/// held. These tests query loopback sockets the test owns, and watch for exactly that leak.
/// </summary>
[Collection("ServerQuery")]
public sealed class ServerQueryServiceTests
{
    private const int QueryTimeoutMs = 400;
    private const int PlayerTimeoutMs = 200;

    private static ServerQueryService NewService() =>
        new(NullLogger<ServerQueryService>.Instance, QueryTimeoutMs, PlayerTimeoutMs);

    [Fact]
    public async Task SilentEndpointTimesOutWithinItsBudgetAndLeavesNoFaultedReceive()
    {
        using var watch = new UnobservedFaultWatch();
        using var blackHole = new LoopbackServer(answerInfo: false);
        var service = NewService();

        var stopwatch = Stopwatch.StartNew();
        var info = await service.QueryAsync(IPAddress.Loopback.ToString(), blackHole.Port);
        stopwatch.Stop();

        Assert.Null(info);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 3_000);
        Assert.Empty(await watch.DrainSocketFaultsAsync());
    }

    [Fact]
    public async Task ServerThatAnswersInfoButDropsThePlayerListStillReturnsInfo()
    {
        // The production shape: A2S_PLAYER is firewalled off, so that round always times out
        // and the client falls back to the A2S_INFO figure. It used to leak a receive per poll.
        using var watch = new UnobservedFaultWatch();
        using var server = new LoopbackServer(answerInfo: true, players: 12, maxPlayers: 70);
        var service = NewService();

        var info = await service.QueryAsync(IPAddress.Loopback.ToString(), server.Port);

        Assert.NotNull(info);
        Assert.Equal("Test Server", info!.Name);
        Assert.Equal("TheIsland", info.Map);
        Assert.Equal(12, info.Players);
        Assert.Equal(70, info.MaxPlayers);
        Assert.Empty(await watch.DrainSocketFaultsAsync());
    }

    [Fact]
    public async Task RepeatedQueriesAgainstASilentEndpointLeaveNothingBehind()
    {
        // The Session HUD polls every 5s for the whole time ARK runs; one leak per poll is what
        // turned a single unreachable server into tens of thousands of telemetry rows.
        using var watch = new UnobservedFaultWatch();
        using var blackHole = new LoopbackServer(answerInfo: false);
        var service = NewService();

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await service.QueryAsync(IPAddress.Loopback.ToString(), blackHole.Port));
        }

        Assert.Empty(await watch.DrainSocketFaultsAsync());
    }

    [Fact]
    public async Task RepeatedQueriesAgainstAFirewalledPlayerListLeaveNothingBehind()
    {
        // A reachable, healthy server that simply refuses A2S_PLAYER leaked one receive on every
        // single poll — the widest blast radius of the whole family, because nothing looks wrong.
        using var watch = new UnobservedFaultWatch();
        using var server = new LoopbackServer(answerInfo: true, players: 12, maxPlayers: 70);
        var service = NewService();

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await service.QueryAsync(IPAddress.Loopback.ToString(), server.Port));
        }

        Assert.Empty(await watch.DrainSocketFaultsAsync());
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndStillLeavesNothingBehind()
    {
        using var watch = new UnobservedFaultWatch();
        using var blackHole = new LoopbackServer(answerInfo: false);
        var service = new ServerQueryService(NullLogger<ServerQueryService>.Instance, 30_000, 30_000);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.QueryAsync(IPAddress.Loopback.ToString(), blackHole.Port, caller.Token));

        Assert.Empty(await watch.DrainSocketFaultsAsync());
    }

    [Fact]
    public async Task AnInvalidEndpointIsRejectedWithoutOpeningASocket()
    {
        var service = NewService();

        Assert.Null(await service.QueryAsync("not-an-ip", 27015));
        Assert.Null(await service.QueryAsync(IPAddress.Loopback.ToString(), 0));
        Assert.Null(await service.QueryAsync(IPAddress.Loopback.ToString(), 70_000));
    }

    [Fact]
    public async Task TheLeakWatchItselfDetectsAnAbandonedFaultedTask()
    {
        // Without this the assertions above could pass by never detecting anything at all.
        using var watch = new UnobservedFaultWatch();

        Leak();

        Assert.Contains(await watch.DrainSocketFaultsAsync(), fault =>
            fault.GetBaseException() is SocketException { SocketErrorCode: SocketError.OperationAborted });

        static void Leak() => _ = Task.FromException(new SocketException((int)SocketError.OperationAborted));
    }

    /// <summary>
    /// Collects <see cref="TaskScheduler.UnobservedTaskException"/> and forces the finalizer to
    /// publish anything that was dropped. Only socket-shaped faults are reported, so a test
    /// running in another collection cannot colour the result.
    /// </summary>
    private sealed class UnobservedFaultWatch : IDisposable
    {
        private readonly List<Exception> _faults = [];

        public UnobservedFaultWatch() => TaskScheduler.UnobservedTaskException += OnUnobserved;

        private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception is { } exception)
            {
                lock (_faults) _faults.Add(exception);
            }
            e.SetObserved();
        }

        public async Task<IReadOnlyList<Exception>> DrainSocketFaultsAsync()
        {
            // An aborted overlapped receive completes through the IO port after the handle is
            // closed, so give it a moment to actually fault before asking the GC to publish it.
            await Task.Delay(500);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();

            lock (_faults)
            {
                // Other collections run in parallel and drop their own tasks into this global
                // event, so keep only what actually came out of a socket.
                return _faults
                    .Where(fault => fault.ToString() is var text
                        && (text.Contains("System.Net.Sockets", StringComparison.Ordinal)
                            || text.Contains("RazorReaper.Services.ServerQuery", StringComparison.Ordinal)))
                    .ToArray();
            }
        }

        public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    /// <summary>
    /// A loopback UDP socket the test owns. It must stay bound even when it answers nothing:
    /// an unbound port makes Windows return ICMP port-unreachable, which is a different failure
    /// from the silent server this is meant to stand in for.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public LoopbackServer(bool answerInfo, byte players = 0, byte maxPlayers = 0)
        {
            Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
            _loop = answerInfo
                ? Task.Run(() => AnswerInfoOnlyAsync(players, maxPlayers, _stop.Token))
                : Task.CompletedTask;
        }

        public int Port { get; }

        /// <summary>Answers A2S_INFO ('T') and drops A2S_PLAYER ('U'), as a firewalled server does.</summary>
        private async Task AnswerInfoOnlyAsync(byte players, byte maxPlayers, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var request = await _socket.ReceiveAsync(token);
                    if (request.Buffer.Length > 4 && request.Buffer[4] == 0x54)
                    {
                        var reply = BuildInfoReply(players, maxPlayers);
                        await _socket.SendAsync(reply, request.RemoteEndPoint, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private static byte[] BuildInfoReply(byte players, byte maxPlayers)
        {
            var payload = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17 };

            void Text(string value)
            {
                payload.AddRange(Encoding.UTF8.GetBytes(value));
                payload.Add(0);
            }

            Text("Test Server");
            Text("TheIsland");
            Text("ark");
            Text("ARK: Survival Ascended");
            payload.AddRange(new byte[] { 0x00, 0x00 }); // Steam AppID
            payload.Add(players);
            payload.Add(maxPlayers);
            payload.Add(0);                              // bots
            payload.AddRange(new byte[] { 0x64, 0x77 }); // server type + environment
            payload.AddRange(new byte[] { 0x00, 0x00 }); // visibility + VAC
            Text("358.16");

            return payload.ToArray();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* already torn down */ }
            _stop.Dispose();
        }
    }
}
