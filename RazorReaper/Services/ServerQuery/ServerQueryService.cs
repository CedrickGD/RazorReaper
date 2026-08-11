using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.ServerQuery;

/// <summary>Result of a successful A2S_INFO query, including the measured UDP round-trip time.</summary>
public sealed record ServerQueryInfo(
    string Name,
    string Map,
    int Players,
    int MaxPlayers,
    string Version,
    int PingMs);

/// <summary>
/// Valve Source (A2S) server query — the protocol ARK servers answer on their query port.
/// Extracted from the Server page so the Session HUD (and anything else) can query servers
/// without a page being open.
/// </summary>
public interface IServerQueryService
{
    /// <summary>
    /// Query a server's A2S_INFO endpoint. Returns null when the server is offline, the
    /// endpoint is invalid, or the query times out. Never throws except on cancellation.
    /// </summary>
    Task<ServerQueryInfo?> QueryAsync(string ip, int queryPort, CancellationToken cancellationToken = default);
}

public sealed class ServerQueryService : IServerQueryService
{
    private const int QueryTimeoutMs = 3500;

    // A2S_INFO: 0xFF×4 header, 'T', "Source Engine Query\0"
    private static readonly byte[] InfoQuery =
    {
        0xFF, 0xFF, 0xFF, 0xFF,
        0x54,
        0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20,
        0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20,
        0x51, 0x75, 0x65, 0x72, 0x79, 0x00
    };

    private readonly ILogger<ServerQueryService> _logger;

    public ServerQueryService(ILogger<ServerQueryService> logger)
    {
        _logger = logger;
    }

    public async Task<ServerQueryInfo?> QueryAsync(string ip, int queryPort, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var parsedAddress) || queryPort is < 1 or > 65535)
            {
                return null;
            }

            using var client = new UdpClient();
            client.Client.ReceiveTimeout = QueryTimeoutMs;
            client.Client.SendTimeout = QueryTimeoutMs;
            var endpoint = new IPEndPoint(parsedAddress, queryPort);

            var stopwatch = Stopwatch.StartNew();
            await client.SendAsync(InfoQuery, InfoQuery.Length, endpoint);
            var response = await client.ReceiveAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(QueryTimeoutMs), cancellationToken);
            stopwatch.Stop();

            var data = response.Buffer;

            // Modern servers answer with an S2C_CHALLENGE ('A') first; resend with the token.
            // Re-measure so the reported ping is one clean round trip, not two.
            if (data.Length >= 9 && data[4] == 0x41)
            {
                var challengeQuery = new byte[InfoQuery.Length + 4];
                Array.Copy(InfoQuery, challengeQuery, InfoQuery.Length);
                Array.Copy(data, 5, challengeQuery, InfoQuery.Length, 4);

                stopwatch.Restart();
                await client.SendAsync(challengeQuery, challengeQuery.Length, endpoint);
                response = await client.ReceiveAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(QueryTimeoutMs), cancellationToken);
                stopwatch.Stop();
                data = response.Buffer;
            }

            var info = ParseInfoResponse(data, (int)stopwatch.ElapsedMilliseconds);
            if (info == null) return null;

            // ARK's A2S_INFO player count is not the number of people on the server — it counts
            // reserved and queued slots too, which is why the HUD kept showing numbers like 67/70
            // on a server with half that many players. The player list is the real thing, so ask
            // for it and count the entries; if that query fails we keep the INFO number rather
            // than showing nothing.
            var actual = await QueryPlayerCountAsync(client, endpoint, cancellationToken);
            return actual is { } count ? info with { Players = count } : info;
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("A2S query timed out for {IP}:{Port}", ip, queryPort);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A2S query error for {IP}:{Port}", ip, queryPort);
        }
        return null;
    }

    /// <summary>
    /// The player-list enrichment is optional garnish on top of a query that already
    /// succeeded, so it gets a short budget of its own. On its own it re-used the full
    /// 3.5s timeout, and a server with A2S_PLAYER firewalled off stalled every refresh
    /// of the Server page and every Session HUD poll by that much (7s with a challenge).
    /// </summary>
    private const int PlayerQueryTimeoutMs = 1200;

    /// <summary>
    /// A2S_PLAYER: ask for the player list and count the entries actually returned. Null when
    /// the server refuses or times out — the caller then keeps the A2S_INFO figure.
    /// </summary>
    private async Task<int?> QueryPlayerCountAsync(UdpClient client, IPEndPoint endpoint, CancellationToken ct)
    {
        try
        {
            // One shared budget across challenge + data receives, so the worst case stays
            // ~1.2s rather than doubling whenever a challenge round is involved.
            var deadline = Stopwatch.StartNew();
            TimeSpan Remaining() =>
                TimeSpan.FromMilliseconds(Math.Max(1, PlayerQueryTimeoutMs - deadline.ElapsedMilliseconds));

            // 0x55 with a -1 challenge asks for the token; the server answers 'A' + 4 bytes.
            var request = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x55, 0xFF, 0xFF, 0xFF, 0xFF };
            await client.SendAsync(request, request.Length, endpoint);
            var data = (await client.ReceiveAsync().WaitAsync(Remaining(), ct)).Buffer;

            if (data.Length >= 9 && data[4] == 0x41)
            {
                Array.Copy(data, 5, request, 5, 4);
                await client.SendAsync(request, request.Length, endpoint);
                data = (await client.ReceiveAsync().WaitAsync(Remaining(), ct)).Buffer;
            }

            // Split response (FE FF FF FF): a full 70-player ARK list overflows one datagram,
            // which is precisely the crowded-server case this enrichment exists for — so
            // reassemble instead of bailing back to the inflated INFO figure.
            if (data.Length >= 4 && data[0] == 0xFE)
            {
                data = await ReassembleSplitAsync(client, data, Remaining, ct) ?? Array.Empty<byte>();
            }

            if (data.Length < 6 || data[0] != 0xFF || data[4] != 0x44) return null;

            var offset = 5;
            int header = data[offset++];

            // Walk the entries rather than trusting the header byte (it wraps at 255 and
            // some servers misreport it): index, name, score, duration. Termination needs
            // no entry cap — offset strictly advances at least 9 bytes per iteration.
            var counted = 0;
            while (offset < data.Length)
            {
                offset++;                                   // index
                if (offset >= data.Length) break;
                ReadString(data, ref offset);               // name
                offset += 8;                                // score (int32) + duration (float)
                if (offset > data.Length) break;
                counted++;
            }

            return counted > 0 ? counted : header;
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A2S_PLAYER query failed for {Endpoint}", endpoint);
            return null;
        }
    }

    /// <summary>
    /// Reassembles a Source-engine split response (FE FF FF FF header) into one payload.
    /// Returns null for anything not worth risking a mis-parse on: mismatched IDs, an
    /// implausible fragment count, a bzip2-compressed payload (ID bit 31), or a timeout
    /// before all fragments arrive.
    /// </summary>
    private static async Task<byte[]?> ReassembleSplitAsync(
        UdpClient client, byte[] first, Func<TimeSpan> remaining, CancellationToken ct)
    {
        // Split header: int32 -2, int32 id, byte total, byte number, int16 splitSize.
        const int HeaderLen = 12;
        if (first.Length < HeaderLen) return null;

        var id = BitConverter.ToInt32(first, 4);
        if ((id & unchecked((int)0x80000000)) != 0) return null; // compressed — not handled
        int total = first[8];
        if (total < 1 || total > 16) return null;

        var parts = new byte[total][];
        void Store(byte[] packet)
        {
            int number = packet[9];
            if (number < total && parts[number] == null)
                parts[number] = packet[HeaderLen..];
        }

        Store(first);
        var have = 1;
        while (have < total)
        {
            var next = (await client.ReceiveAsync().WaitAsync(remaining(), ct)).Buffer;
            if (next.Length < HeaderLen || next[0] != 0xFE) continue;   // stray datagram
            if (BitConverter.ToInt32(next, 4) != id) return null;      // different response
            Store(next);
            have = parts.Count(p => p != null);
        }

        return parts.SelectMany(p => p!).ToArray();
    }

    /// <summary>Parse an S2A_INFO ('I') payload; null for anything else or a truncated packet.</summary>
    private ServerQueryInfo? ParseInfoResponse(byte[] data, int pingMs)
    {
        try
        {
            if (data.Length > 6 && data[4] == 0x49)
            {
                var offset = 5;
                offset++; // protocol version

                var name = ReadString(data, ref offset);
                var map = ReadString(data, ref offset);
                ReadString(data, ref offset); // folder
                ReadString(data, ref offset); // game

                if (offset + 2 < data.Length)
                {
                    offset += 2; // Steam AppID (short)
                    var players = data[offset++];
                    var maxPlayers = data[offset++];
                    offset++;    // bots
                    offset += 2; // server type + environment
                    offset += 2; // visibility + VAC

                    var version = ReadString(data, ref offset);
                    return new ServerQueryInfo(name, map, players, maxPlayers, version, pingMs);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing A2S info response");
        }
        return null;
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        if (offset >= data.Length) return "";

        var start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }

        if (offset > start)
        {
            try
            {
                var result = System.Text.Encoding.UTF8.GetString(data, start, offset - start);
                offset++;
                return result;
            }
            catch
            {
                var result = System.Text.Encoding.ASCII.GetString(data, start, offset - start);
                offset++;
                return result;
            }
        }

        if (offset < data.Length) offset++;
        return "";
    }
}
