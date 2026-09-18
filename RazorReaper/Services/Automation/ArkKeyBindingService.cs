using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Reads the player's ARK key bindings from <c>Input.ini</c> and answers "which key did they
/// actually bind for this action".
///
/// The first scan happens at startup rather than lazily so the very first script constructed
/// already sees real bindings — scripts read their defaults in their constructors, and a default
/// resolved too late would be the stock one. After that <see cref="RefreshIfStale"/> keeps it
/// honest: every script start checks the file's timestamp, so rebinding Access Inventory in ARK
/// and coming straight back does not leave the scripts pressing yesterday's key.
///
/// This never overrides anything the user typed: script settings read a stored preference first
/// and only fall back to this when nothing has been set, so a manual choice always wins.
/// </summary>
public sealed class ArkKeyBindingService : IArkKeyBindingService
{
    private readonly IArkPathProvider _arkPath;
    private readonly ILogger<ArkKeyBindingService> _logger;

    /// <summary>Guards the published snapshot below. Held for lookups only — never across I/O.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// Serializes the loads themselves. Two script starts a millisecond apart would otherwise
    /// both walk the registry and read the file; the second one waits and then finds nothing
    /// stale left to do.
    /// </summary>
    private readonly object _loadGate = new();

    private IReadOnlyDictionary<string, string>? _bindings;
    private ArkKeyBindingStatus _status = ArkKeyBindingStatus.NotFound;

    /// <summary>What the last load looked at, so <see cref="RefreshIfStale"/> can tell nothing changed.</summary>
    private string? _loadedPath;
    private DateTime _loadedStampUtc;
    private bool _loaded;

    public ArkKeyBindingService(IArkPathProvider arkPath, ILogger<ArkKeyBindingService> logger)
    {
        _arkPath = arkPath;
        _logger = logger;
        Load(force: true);
    }

    public bool HasPlayerBindings
    {
        get { lock (_gate) return _bindings is { Count: > 0 }; }
    }

    public ArkKeyBindingStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public void Refresh() => Load(force: true);

    public void RefreshIfStale() => Load(force: false);

    public string Resolve(string arkAction, string fallback)
    {
        if (string.IsNullOrWhiteSpace(arkAction)) return fallback;

        IReadOnlyDictionary<string, string>? bindings;
        lock (_gate) bindings = _bindings;

        if (bindings is not null && bindings.TryGetValue(arkAction, out var bound) && !string.IsNullOrWhiteSpace(bound))
        {
            return bound;
        }

        // No Input.ini entry means the player left that action on ARK's factory binding — the file
        // only lists what was actually changed.
        if (ArkKeyBindingParser.StockBindings.TryGetValue(arkAction, out var stock))
        {
            return stock;
        }

        return fallback;
    }

    private void Load(bool force)
    {
        // One loader at a time: the stale check below is a read-compare-write across the file
        // system, and two of them interleaving could publish the older of two reads.
        lock (_loadGate)
        {
            try
            {
                var inputIni = ResolveInputIniPath(force);
                var exists = inputIni is not null && File.Exists(inputIni);
                var stamp = default(DateTime);
                if (exists)
                {
                    // A file that vanishes between the two calls reads as "no timestamp", which
                    // differs from the stored one and therefore forces a load — which then finds
                    // it gone and falls back. Never an exception out of the cheap path.
                    try { stamp = File.GetLastWriteTimeUtc(inputIni!); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Input.ini timestamp unreadable — reloading anyway"); }
                }

                if (!force && IsUnchanged(inputIni, exists, stamp)) return;

                if (!exists)
                {
                    if (inputIni is null)
                        _logger.LogDebug("ARK install not found — script key defaults fall back to ARK's stock layout");
                    else
                        _logger.LogDebug("No Input.ini at {Path} — the player never rebound anything", inputIni);

                    Publish(null, ArkKeyBindingStatus.NotFound, inputIni, default);
                    return;
                }

                // ARK writes this file in whatever encoding the game felt like; ReadAllLines with
                // detection handles both the UTF-16 and plain-ASCII variants seen in the wild.
                var lines = File.ReadAllLines(inputIni!, System.Text.Encoding.UTF8);
                var parsed = ArkKeyBindingParser.Parse(lines);
                var status = new ArkKeyBindingStatus(true, CountCustomBindings(parsed));

                Publish(parsed, status, inputIni, stamp);

                _logger.LogInformation(
                    "ARK key bindings scanned: {Count} usable bindings ({Custom} differ from stock) from {Path}",
                    parsed.Count, status.CustomBindingCount, inputIni);

                foreach (var action in ArkKeyBindingParser.StockBindings.Keys)
                {
                    if (parsed.TryGetValue(action, out var key))
                    {
                        _logger.LogDebug("ARK binding: {Action} = {Key}", action, key);
                    }
                }
            }
            catch (Exception ex)
            {
                // The last good scan stays: a read that failed is not a file that is gone. ARK
                // rewrites Input.ini the moment the player changes a keybind in its options, and
                // the stale check now runs on every script start, so landing mid-write is a thing
                // that happens. Throwing the scan away there would drop the player's real keys
                // back to ARK's stock ones for that run and tell the page the file was never
                // found. The stored timestamp is still the old one, so the next start retries.
                //
                // A first scan that fails has nothing to keep and stays on the stock defaults,
                // which is where the fields start.
                _logger.LogWarning(ex, "Reading ARK key bindings failed — keeping the last scan");
            }
        }
    }

    /// <summary>
    /// Where Input.ini is. Finding it is not cheap — two registry reads, a parse of
    /// libraryfolders.vdf and a stat per Steam library — and the stale check runs on every script
    /// start with the global hotkey on the other end of it. So the path the last load used is
    /// reused while it still exists, and the search only runs again when it is gone (ARK installed
    /// or moved since) or when a caller forces it: Rescan, and the ARK path setting changing.
    /// </summary>
    private string? ResolveInputIniPath(bool force)
    {
        if (!force)
        {
            string? known;
            lock (_gate) known = _loadedPath;
            if (known is not null && File.Exists(known)) return known;
        }

        var arkPath = _arkPath.FindArkPath();
        return string.IsNullOrWhiteSpace(arkPath)
            ? null
            : Path.Combine(arkPath, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "Input.ini");
    }

    private bool IsUnchanged(string? path, bool exists, DateTime stampUtc)
    {
        lock (_gate)
        {
            return _loaded
                && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase)
                && _status.InputIniFound == exists
                && _loadedStampUtc == stampUtc;
        }
    }

    private void Publish(
        IReadOnlyDictionary<string, string>? bindings,
        ArkKeyBindingStatus status,
        string? path,
        DateTime stampUtc)
    {
        lock (_gate)
        {
            _bindings = bindings;
            _status = status;
            _loadedPath = path;
            _loadedStampUtc = stampUtc;
            _loaded = true;
        }
    }

    /// <summary>
    /// How many of the actions the scripts press the player moved off ARK's factory key. Counting
    /// every line in the file instead would put a number on the page that no script depends on.
    /// </summary>
    private static int CountCustomBindings(IReadOnlyDictionary<string, string> parsed)
    {
        var custom = 0;
        foreach (var (action, stock) in ArkKeyBindingParser.StockBindings)
        {
            if (parsed.TryGetValue(action, out var key) && !string.Equals(key, stock, StringComparison.OrdinalIgnoreCase))
            {
                custom++;
            }
        }
        return custom;
    }
}

/// <summary>
/// Lets a script ask for a default without taking the service in its constructor.
///
/// The 16 scripts all mirror one base constructor signature, and the headless test harnesses build
/// them with no MAUI application at all, so the service is resolved late here — the same approach
/// <see cref="AutomationScriptBase"/> already uses for the usage gate. With no container available
/// the caller simply keeps its own fallback.
/// </summary>
public static class ArkKeyDefaults
{
    /// <summary>The player's key for <paramref name="arkAction"/>, or <paramref name="fallback"/>.</summary>
    public static string For(string arkAction, string fallback)
    {
        try
        {
            return Service?.Resolve(arkAction, fallback) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Re-reads Input.ini if it changed. Called from <see cref="AutomationScriptBase"/> on every
    /// start, which is the moment a stale scan would actually cost the user a keypress.
    /// </summary>
    public static void RefreshIfStale()
    {
        try { Service?.RefreshIfStale(); }
        catch { /* a scan that cannot run leaves the last good one in place */ }
    }

    /// <summary>
    /// Re-reads Input.ini whatever its timestamp says, for a caller that knows something changed
    /// without the file changing — the ARK path setting moving to a different install. Pages that
    /// hold the service itself (the Scripts page's Rescan) call it there instead.
    /// </summary>
    public static void Refresh()
    {
        try { Service?.Refresh(); }
        catch { /* a scan that cannot run leaves the last good one in place */ }
    }

    /// <summary>
    /// How the statics above find the service, declared before the property that reads it because
    /// static initializers run in source order. Replaceable for the same reason
    /// <see cref="AutomationScriptBase.ResolveUsageGate"/> is: the headless test harnesses build
    /// scripts and macros with no MAUI application, so without a seam here every key default in a
    /// test is the hard-coded fallback and a test of "the scan reaches this key" would pass
    /// whether the scan reached it or not. Restore it afterwards — it is process-wide.
    /// </summary>
    internal static readonly Func<IArkKeyBindingService?> DefaultResolver =
        static () => IPlatformApplication.Current?.Services?.GetService(typeof(IArkKeyBindingService)) as IArkKeyBindingService;

    internal static Func<IArkKeyBindingService?> ResolveService { get; set; } = DefaultResolver;

    private static IArkKeyBindingService? Service => ResolveService();
}
