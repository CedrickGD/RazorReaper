using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Reads the ARK key bindings that apply and answers "which key does this action press".
///
/// Two layers, resolved the way Unreal resolves them: the install's own
/// <c>ShooterGame/Config/DefaultInput.ini</c> is the base — ARK's factory layout for the build
/// that is actually on disk — and the player's <c>Saved/Config/WindowsNoEditor/Input.ini</c> goes
/// on top, because that file lists only what they changed. <see cref="ArkKeyBindingParser.StockBindings"/>
/// is the fallback for when there is no install to read, and nothing more: a hard-coded table is
/// the one layer that can drift away from the game without anybody noticing, which is exactly what
/// it did with Access Inventory on "E" until 1.5.3.
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
    private string? _loadedDefaultPath;
    private DateTime _loadedStampUtc;
    private bool _loaded;

    public ArkKeyBindingService(IArkPathProvider arkPath, ILogger<ArkKeyBindingService> logger)
    {
        _arkPath = arkPath;
        _logger = logger;
        Load(force: true);
    }

    /// <summary>
    /// The player's own file, not the install's defaults — which is why this reads the status and
    /// not <see cref="_bindings"/>: those now hold ARK's factory layout too, and would say "yes"
    /// on an install where the player never rebound a thing.
    /// </summary>
    public bool HasPlayerBindings
    {
        get { lock (_gate) return _status.InputIniFound; }
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

        // Nothing in either layer means there was no install to read a factory layout from, so the
        // hard-coded table is all that is left.
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
                var (inputIni, defaultIni) = ResolveConfigPaths(force);
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

                // Only read here, never in the cheap path above: the stale check returns while
                // nothing moved, so the install's defaults cost one 250-line file per real reload.
                var factory = ReadFactoryBindings(defaultIni);

                if (!exists)
                {
                    if (inputIni is null)
                        _logger.LogDebug("ARK install not found — script key defaults fall back to the stock table");
                    else
                        _logger.LogDebug("No Input.ini at {Path} — the player never rebound anything", inputIni);

                    // Still "using ARK's default keys" as far as the page is concerned — and now
                    // they really are ARK's, read from the install rather than remembered.
                    Publish(factory, ArkKeyBindingStatus.NotFound, inputIni, defaultIni, default);
                    return;
                }

                // ARK writes this file in whatever encoding the game felt like; ReadAllLines with
                // detection handles both the UTF-16 and plain-ASCII variants seen in the wild.
                var lines = File.ReadAllLines(inputIni!, System.Text.Encoding.UTF8);
                var player = ArkKeyBindingParser.Parse(lines);
                var effective = Overlay(factory, player);
                var status = new ArkKeyBindingStatus(true, CountCustomBindings(factory, player));

                Publish(effective, status, inputIni, defaultIni, stamp);

                _logger.LogInformation(
                    "ARK key bindings scanned: {Count} usable bindings ({Custom} differ from the install's defaults) from {Path}",
                    player.Count, status.CustomBindingCount, inputIni);

                foreach (var action in ArkKeyBindingParser.StockBindings.Keys)
                {
                    if (effective.TryGetValue(action, out var key))
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
    /// Where the player's Input.ini and the install's DefaultInput.ini are. Finding them is not
    /// cheap — two registry reads, a parse of libraryfolders.vdf and a stat per Steam library —
    /// and the stale check runs on every script start with the global hotkey on the other end of
    /// it. So the pair the last load used is reused while the player's file still exists, and the
    /// search only runs again when it is gone (ARK installed or moved since) or when a caller
    /// forces it: Rescan, and the ARK path setting changing.
    /// </summary>
    private (string? InputIni, string? DefaultIni) ResolveConfigPaths(bool force)
    {
        if (!force)
        {
            string? known, knownDefault;
            lock (_gate) { known = _loadedPath; knownDefault = _loadedDefaultPath; }
            if (known is not null && File.Exists(known)) return (known, knownDefault);
        }

        var arkPath = _arkPath.FindArkPath();
        return string.IsNullOrWhiteSpace(arkPath)
            ? (null, null)
            : (Path.Combine(arkPath, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "Input.ini"),
               Path.Combine(arkPath, "ShooterGame", "Config", "DefaultInput.ini"));
    }

    /// <summary>
    /// ARK's factory bindings, from the install that is actually there. Its own try/catch: a
    /// DefaultInput.ini that cannot be read must not cost the player their Input.ini as well, and
    /// <see cref="ArkKeyBindingParser.StockBindings"/> still answers for whatever is missing.
    /// </summary>
    private IReadOnlyDictionary<string, string> ReadFactoryBindings(string? defaultIni)
    {
        try
        {
            if (defaultIni is null || !File.Exists(defaultIni)) return ReadOnlyDictionary<string, string>.Empty;
            return ArkKeyBindingParser.Parse(File.ReadAllLines(defaultIni, System.Text.Encoding.UTF8));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DefaultInput.ini unreadable at {Path} — the stock table answers instead", defaultIni);
            return ReadOnlyDictionary<string, string>.Empty;
        }
    }

    /// <summary>The player's file on top of the install's defaults, later winning as Unreal has it.</summary>
    private static IReadOnlyDictionary<string, string> Overlay(
        IReadOnlyDictionary<string, string> factory,
        IReadOnlyDictionary<string, string> player)
    {
        if (factory.Count == 0) return player;

        var merged = new Dictionary<string, string>(factory, StringComparer.OrdinalIgnoreCase);
        foreach (var (action, key) in player) merged[action] = key;
        return merged;
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
        string? defaultPath,
        DateTime stampUtc)
    {
        lock (_gate)
        {
            _bindings = bindings;
            _status = status;
            _loadedPath = path;
            _loadedDefaultPath = defaultPath;
            _loadedStampUtc = stampUtc;
            _loaded = true;
        }
    }

    /// <summary>
    /// How many of the actions the scripts press the player moved off the key the install ships
    /// with. Only their own file is looked at: the base layer is the factory layout, so counting it
    /// would report six rebinds on an install nobody touched. Counting every line in the player's
    /// file instead would put a number on the page that no script depends on.
    /// </summary>
    private static int CountCustomBindings(
        IReadOnlyDictionary<string, string> factory,
        IReadOnlyDictionary<string, string> player)
    {
        var custom = 0;
        foreach (var (action, stock) in ArkKeyBindingParser.StockBindings)
        {
            if (!player.TryGetValue(action, out var key)) continue;

            var shipped = factory.TryGetValue(action, out var fromInstall) ? fromInstall : stock;
            if (!string.Equals(key, shipped, StringComparison.OrdinalIgnoreCase)) custom++;
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
    /// The key a script presses for <paramref name="arkAction"/>: the player's own when they typed
    /// one into the script's field (a preference under <paramref name="prefKey"/>), otherwise what
    /// the scan says right now, with <paramref name="stock"/>, ARK's factory key, as the last
    /// resort. Never the key the script held before: fed back as the fallback, a rebind the player
    /// undid in ARK stayed on the script until the app restarted.
    ///
    /// A stored key that equals the scan is dropped. Until now every settings save wrote the keys
    /// along with whatever was being saved, so a player who only changed Runs had pinned F and T
    /// without touching them. A stored copy of ARK's own binding cannot be told apart from that, so
    /// it goes and the key follows ARK again.
    /// </summary>
    public static string Follow(string prefKey, string arkAction, string stock)
    {
        var scanned = For(arkAction, stock);
        try
        {
            if (!Prefs.ContainsKey(prefKey)) return scanned;

            var own = Prefs.Get(prefKey, scanned);
            if (!string.IsNullOrWhiteSpace(own) && !SameKey(own, scanned)) return own.Trim();

            Prefs.Remove(prefKey);
        }
        catch
        {
            // An unreadable store is a key nobody set.
        }
        return scanned;
    }

    /// <summary>
    /// Stores a key the player typed into a script's key field and returns the key to use. Only a
    /// key that differs from ARK's binding is stored; one that matches it, or a blank field, leaves
    /// no preference behind, so it keeps following ARK. Call it from that field's edit and nowhere
    /// else: saving any other setting must not touch the key.
    /// </summary>
    public static string Keep(string prefKey, string arkAction, string stock, string? typed)
    {
        var scanned = For(arkAction, stock);
        var key = string.IsNullOrWhiteSpace(typed) ? scanned : typed.Trim();
        try
        {
            if (SameKey(key, scanned)) Prefs.Remove(prefKey);
            else Prefs.Set(prefKey, key);
        }
        catch
        {
            // The key still applies for this session; the store only decides the next one.
        }
        return key;
    }

    internal static bool SameKey(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where <see cref="Follow"/> and <see cref="Keep"/> keep the keys a player set. Replaceable
    /// for the same reason as <see cref="ResolveService"/>: a test must never write the machine's
    /// real preference store. Restore it afterwards, it is process-wide.
    /// </summary>
    internal static IPreferencesStore Prefs { get; set; } = new Implementations.MauiPreferencesStore();

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

/// <summary>
/// One script key that presses an ARK action, on top of <see cref="ArkKeyDefaults.Follow"/> and
/// <see cref="ArkKeyDefaults.Keep"/>. Setting <see cref="Value"/> is the player typing into the key
/// field and is the only thing that stores anything; <see cref="Follow"/> is Rescan and every start
/// asking the scan again. A script's other settings never go through here, so saving them cannot
/// pin a key the player never set.
/// </summary>
public sealed class ArkKeySetting(string prefKey, string arkAction, string stock)
{
    private string _value = ArkKeyDefaults.Follow(prefKey, arkAction, stock);

    public string Value
    {
        get => _value;
        set => _value = ArkKeyDefaults.Keep(prefKey, arkAction, stock, value);
    }

    public void Follow() => _value = ArkKeyDefaults.Follow(prefKey, arkAction, stock);
}
