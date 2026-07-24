using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RazorReaper.Services.Implementations;

namespace RazorReaper.Services.Steam;

/// <summary>One join entry from Steam's server-browser history. Address ports are query ports.</summary>
public sealed record SteamServerHistoryEntry(string Ip, int QueryPort, DateTime LastPlayedUtc)
{
    public string Address => $"{Ip}:{QueryPort}";
}

/// <summary>
/// Reads the "history" block of Steam's serverbrowser_hist.vdf — the file Steam stamps every
/// time the game joins a server — across all local accounts (userdata\*\7\remote) plus the
/// legacy global copy (config\). This is how the Session HUD learns which server ARK is on
/// without touching the game.
/// </summary>
public interface ISteamServerHistoryService
{
    /// <summary>The most recently joined server across all local Steam accounts, or null.</summary>
    SteamServerHistoryEntry? GetMostRecentEntry();
}

public sealed class SteamServerHistoryService : ISteamServerHistoryService
{
    // "address" is immediately followed by "LastPlayed" inside each history entry.
    private static readonly Regex EntryRegex = new(
        @"""address""\s*""([^""]+)""\s*""LastPlayed""\s*""(\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<SteamServerHistoryService> _logger;

    // Per-file cache keyed by path: re-parse only when the file's write time changes.
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTime WriteTimeUtc, SteamServerHistoryEntry? Newest)> _cache = new();

    public SteamServerHistoryService(ILogger<SteamServerHistoryService> logger)
    {
        _logger = logger;
    }

    public SteamServerHistoryEntry? GetMostRecentEntry()
    {
        SteamServerHistoryEntry? newest = null;
        foreach (var file in EnumerateHistoryFiles())
        {
            var entry = GetNewestEntryForFile(file);
            if (entry != null && (newest == null || entry.LastPlayedUtc > newest.LastPlayedUtc))
            {
                newest = entry;
            }
        }
        return newest;
    }

    private IEnumerable<string> EnumerateHistoryFiles()
    {
        var steamPath = SteamPathLocator.GetSteamInstallPath();
        if (steamPath is null) yield break;

        // Legacy global location (older Steam clients).
        var legacy = Path.Combine(steamPath, "config", "serverbrowser_hist.vdf");
        if (File.Exists(legacy)) yield return legacy;

        // Current per-account location: userdata\<accountid>\7\remote\serverbrowser_hist.vdf.
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) yield break;

        string[] accountDirs;
        try { accountDirs = Directory.GetDirectories(userdata); }
        catch { yield break; }

        foreach (var accountDir in accountDirs)
        {
            var file = Path.Combine(accountDir, "7", "remote", "serverbrowser_hist.vdf");
            if (File.Exists(file)) yield return file;
        }
    }

    private SteamServerHistoryEntry? GetNewestEntryForFile(string path)
    {
        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(path);
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(path, out var cached) && cached.WriteTimeUtc == writeTimeUtc)
                {
                    return cached.Newest;
                }
            }

            var content = File.ReadAllText(path);
            var historyBlock = ExtractBlock(content, "\"history\"");
            var newest = historyBlock is null ? null : FindNewestEntry(historyBlock);

            lock (_cacheLock)
            {
                _cache[path] = (writeTimeUtc, newest);
            }
            return newest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Steam server history from {Path}", path);
            return null;
        }
    }

    private static SteamServerHistoryEntry? FindNewestEntry(string historyBlock)
    {
        SteamServerHistoryEntry? newest = null;
        foreach (Match match in EntryRegex.Matches(historyBlock))
        {
            if (!TryParseAddress(match.Groups[1].Value, out var ip, out var port)) continue;
            if (!long.TryParse(match.Groups[2].Value, out var unix) || unix <= 0) continue;

            var lastPlayedUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if (newest == null || lastPlayedUtc > newest.LastPlayedUtc)
            {
                newest = new SteamServerHistoryEntry(ip, port, lastPlayedUtc);
            }
        }
        return newest;
    }

    private static bool TryParseAddress(string address, out string ip, out int port)
    {
        ip = "";
        port = 0;
        var colon = address.IndexOf(':');
        if (colon <= 0) return false;

        if (!IPAddress.TryParse(address[..colon], out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(address[(colon + 1)..], out port) || port is < 1 or > 65535)
        {
            return false;
        }

        ip = parsed.ToString();
        return true;
    }

    /// <summary>Returns the brace-balanced block following <paramref name="key"/>, or null.</summary>
    private static string? ExtractBlock(string vdf, string key)
    {
        var keyIndex = vdf.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0) return null;

        var braceStart = vdf.IndexOf('{', keyIndex);
        if (braceStart < 0) return null;

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
