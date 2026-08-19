using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Reads the player's ARK key bindings from <c>Input.ini</c> once per app launch and answers
/// "which key did they actually bind for this action".
///
/// Scanning happens at startup rather than lazily so the very first script constructed already
/// sees real bindings — scripts read their defaults in their constructors, and a default resolved
/// too late would be the stock one.
///
/// This never overrides anything the user typed: script settings read a stored preference first
/// and only fall back to this when nothing has been set, so a manual choice always wins.
/// </summary>
public sealed class ArkKeyBindingService : IArkKeyBindingService
{
    private readonly IArkPathProvider _arkPath;
    private readonly ILogger<ArkKeyBindingService> _logger;
    private readonly object _gate = new();

    private IReadOnlyDictionary<string, string>? _bindings;

    public ArkKeyBindingService(IArkPathProvider arkPath, ILogger<ArkKeyBindingService> logger)
    {
        _arkPath = arkPath;
        _logger = logger;
        Load();
    }

    public bool HasPlayerBindings
    {
        get { lock (_gate) return _bindings is { Count: > 0 }; }
    }

    public void Refresh() => Load();

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

    private void Load()
    {
        try
        {
            var arkPath = _arkPath.FindArkPath();
            if (string.IsNullOrWhiteSpace(arkPath))
            {
                _logger.LogDebug("ARK install not found — script key defaults fall back to ARK's stock layout");
                lock (_gate) _bindings = null;
                return;
            }

            var inputIni = Path.Combine(arkPath, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "Input.ini");
            if (!File.Exists(inputIni))
            {
                _logger.LogDebug("No Input.ini at {Path} — the player never rebound anything", inputIni);
                lock (_gate) _bindings = null;
                return;
            }

            // ARK writes this file in whatever encoding the game felt like; ReadAllLines with
            // detection handles both the UTF-16 and plain-ASCII variants seen in the wild.
            var lines = File.ReadAllLines(inputIni, System.Text.Encoding.UTF8);
            var parsed = ArkKeyBindingParser.Parse(lines);

            lock (_gate) _bindings = parsed;

            _logger.LogInformation(
                "ARK key bindings scanned: {Count} usable bindings from {Path}", parsed.Count, inputIni);

            foreach (var action in new[]
                     {
                         ArkActions.AccessInventory, ArkActions.ShowMyInventory, ArkActions.TransferItem,
                         ArkActions.Use, ArkActions.CraftAll, ArkActions.MoveForward,
                     })
            {
                if (parsed.TryGetValue(action, out var key))
                {
                    _logger.LogDebug("ARK binding: {Action} = {Key}", action, key);
                }
            }
        }
        catch (Exception ex)
        {
            // A missing or malformed Input.ini must never stop the app starting; the scripts just
            // keep ARK's stock defaults.
            _logger.LogWarning(ex, "Reading ARK key bindings failed — falling back to stock defaults");
            lock (_gate) _bindings = null;
        }
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
            var service = IPlatformApplication.Current?.Services?.GetService(typeof(IArkKeyBindingService))
                as IArkKeyBindingService;

            return service?.Resolve(arkAction, fallback) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
