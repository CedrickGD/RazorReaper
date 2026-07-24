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

            return ParseInfoResponse(data, (int)stopwatch.ElapsedMilliseconds);
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
