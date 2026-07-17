using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RazorReaper.Services.Implementations;

namespace RazorReaper.Services.Steam;

/// <summary>
/// Bulk Steam-favorites helpers for the Server page. Parsing is pure string work; the
/// duplicate check reads Steam's serverbrowser_hist.vdf (located via the same registry
/// lookup <see cref="SteamPathLocator"/> uses elsewhere) and never writes to it — the
/// actual "add" stays on the page and reuses the single-server steam:// URL code path.
/// </summary>
public sealed class SteamFavoritesService : ISteamFavoritesService
{
    // steam://connect/1.2.3.4:27015 (optionally followed by /password or other junk)
    private static readonly Regex SteamConnectRegex = new(
        @"steam://connect/([0-9]{1,3}(?:\.[0-9]{1,3}){3}(?::[0-9]{1,5})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "address"  "1.2.3.4:27015" entries inside serverbrowser_hist.vdf
    private static readonly Regex FavoriteAddressRegex = new(
        @"""address""\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<SteamFavoritesService> _logger;

    public SteamFavoritesService(ILogger<SteamFavoritesService> logger)
    {
        _logger = logger;
    }

    public BulkParseResult ParseServerList(string? input)
    {
        var result = new BulkParseResult();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = input.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryExtractEndpoint(line, out var ip, out var port, out var reason))
            {
                result.Invalid.Add(new BulkServerLineError
                {
                    LineNumber = lineNumber,
                    RawLine = line,
                    Reason = reason
                });
                continue;
            }

            var endpoint = $"{ip}:{port}";
            if (!seenEndpoints.Add(endpoint))
            {
                result.Invalid.Add(new BulkServerLineError
                {
                    LineNumber = lineNumber,
                    RawLine = line,
                    Reason = $"Duplicate of an earlier line ({endpoint})"
                });
                continue;
            }

            result.Valid.Add(new BulkServerEntry
            {
                LineNumber = lineNumber,
                RawLine = line,
                IpAddress = ip,
                QueryPort = port
            });
        }

        return result;
    }

    public async Task<HashSet<string>> GetExistingFavoriteEndpointsAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var steamPath = SteamPathLocator.GetSteamInstallPath();
            if (steamPath is null)
            {
                _logger.LogDebug("Steam install path not found; skipping favorites duplicate check");
                return endpoints;
            }

            var historyFile = Path.Combine(steamPath, "config", "serverbrowser_hist.vdf");
            if (!File.Exists(historyFile))
            {
                _logger.LogDebug("serverbrowser_hist.vdf not found at {Path}; skipping favorites duplicate check", historyFile);
                return endpoints;
            }

            var content = await File.ReadAllTextAsync(historyFile, cancellationToken);

            // Only scan the "favorites" block — the same file also holds the history
            // list, and a server that was merely joined once must not be reported as
            // "already in favorites".
            var favoritesBlock = ExtractFavoritesBlock(content);
            if (favoritesBlock is null)
            {
                return endpoints;
            }

            foreach (Match match in FavoriteAddressRegex.Matches(favoritesBlock))
            {
                var address = match.Groups[1].Value.Trim();
                if (address.Length > 0)
                {
                    endpoints.Add(address);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read existing Steam favorites; duplicate detection disabled for this run");
            endpoints.Clear();
        }

        return endpoints;
    }

    private static bool TryExtractEndpoint(string line, out string ip, out int port, out string reason)
    {
        ip = "";
        port = 0;
        reason = "No IP:PORT endpoint found";

        var candidate = line;

        // steam://connect/ip:port style URLs — take the embedded endpoint.
        var steamMatch = SteamConnectRegex.Match(candidate);
        if (steamMatch.Success)
        {
            candidate = steamMatch.Groups[1].Value;
        }
        else
        {
            // steam://run/346110//+connect ip:port style URLs (and raw "+connect ip:port").
            var plusConnect = candidate.IndexOf("+connect", StringComparison.OrdinalIgnoreCase);
            if (plusConnect >= 0)
            {
                candidate = candidate[(plusConnect + "+connect".Length)..];
            }
        }

        // Normalize separators so "ip,port", "ip;port" and "ip<TAB>port" all tokenize.
        candidate = candidate.Replace(',', ' ').Replace(';', ' ').Replace('\t', ' ');

        var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            reason = "Nothing left after trimming";
            return false;
        }

        string ipToken;
        string? portToken = null;

        var first = tokens[0];
        var colonIndex = first.IndexOf(':');
        if (colonIndex > 0)
        {
            ipToken = first[..colonIndex];
            portToken = TakeLeadingDigits(first[(colonIndex + 1)..]);
        }
        else
        {
            ipToken = first;
            if (tokens.Length > 1)
            {
                portToken = TakeLeadingDigits(tokens[1]);
            }
        }

        if (!IPAddress.TryParse(ipToken, out var parsedAddress) ||
            parsedAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            reason = $"Invalid IPv4 address '{ipToken}'";
            return false;
        }

        if (string.IsNullOrEmpty(portToken))
        {
            reason = "Missing port";
            return false;
        }

        if (!int.TryParse(portToken, out port) || port is < 1 or > 65535)
        {
            reason = $"Invalid port '{portToken}' (must be 1-65535)";
            return false;
        }

        ip = parsedAddress.ToString();
        return true;
    }

    /// <summary>Strips trailing junk from a port token, e.g. "27015/password" or "27015)".</summary>
    private static string TakeLeadingDigits(string value)
    {
        var end = 0;
        while (end < value.Length && char.IsAsciiDigit(value[end]))
        {
            end++;
        }

        return value[..end];
    }

    /// <summary>Returns the brace-balanced "favorites" block from a VDF document, or null.</summary>
    private static string? ExtractFavoritesBlock(string vdf)
    {
        var keyIndex = vdf.IndexOf("\"favorites\"", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
        {
            return null;
        }

        var braceStart = vdf.IndexOf('{', keyIndex);
        if (braceStart < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = braceStart; i < vdf.Length; i++)
        {
            if (vdf[i] == '{')
            {
                depth++;
            }
            else if (vdf[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return vdf.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        return null;
    }
}
