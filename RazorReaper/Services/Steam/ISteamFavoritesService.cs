namespace RazorReaper.Services.Steam;

/// <summary>
/// A single server endpoint parsed from a bulk-pasted list. The port is treated as the
/// QUERY port, mirroring the single-server input field on the Server page (the page
/// converts it to the game port before building the Steam favorites URL).
/// </summary>
public sealed class BulkServerEntry
{
    public int LineNumber { get; init; }
    public string RawLine { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public int QueryPort { get; init; }
    public string Endpoint => $"{IpAddress}:{QueryPort}";
}

/// <summary>A line that could not be parsed into a server endpoint, with the reason why.</summary>
public sealed class BulkServerLineError
{
    public int LineNumber { get; init; }
    public string RawLine { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>Outcome of parsing a bulk server list. Comment and empty lines are ignored
/// entirely and appear in neither collection.</summary>
public sealed class BulkParseResult
{
    public List<BulkServerEntry> Valid { get; } = new();
    public List<BulkServerLineError> Invalid { get; } = new();
    public bool HasInput => Valid.Count > 0 || Invalid.Count > 0;
}

/// <summary>
/// Helpers for bulk-adding servers to Steam's server-browser favorites: tolerant parsing
/// of pasted server lists, and read-only detection of servers already present in the
/// user's Steam favorites (so duplicates can be skipped instead of re-added).
/// </summary>
public interface ISteamFavoritesService
{
    /// <summary>
    /// Parses a pasted multi-line server list. Accepted per line: "ip:port", "ip port",
    /// "ip,port", steam://connect/ip:port URLs and steam://run/... "+connect ip:port"
    /// URLs; trailing junk after the endpoint is ignored. Empty lines and lines starting
    /// with '#' or "//" are skipped. Duplicate endpoints within the list are reported
    /// as invalid so each server is only applied once.
    /// </summary>
    BulkParseResult ParseServerList(string? input);

    /// <summary>
    /// Best-effort, read-only scan of Steam's serverbrowser_hist.vdf for endpoints
    /// ("ip:port") already in the favorites list. Returns an empty set when Steam or the
    /// file cannot be found or parsed — callers should then treat every server as new,
    /// which is harmless (re-adding an existing favorite is a no-op in Steam).
    /// </summary>
    Task<HashSet<string>> GetExistingFavoriteEndpointsAsync(CancellationToken cancellationToken = default);
}
