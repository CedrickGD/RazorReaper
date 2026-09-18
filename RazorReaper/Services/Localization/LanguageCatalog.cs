using System.Collections.Frozen;
using System.Text.Json;

namespace RazorReaper.Services.Localization;

/// <summary>
/// Reads the shipped dictionaries out of the assembly.
///
/// The JSON files are embedded rather than copied next to the exe for the same reason the INI
/// presets are: the installer ships one folder, and a translation that can be deleted from disk
/// is a translation that will be, leaving a half-English window with no way to explain itself.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>Matches the LogicalName in RazorReaper.csproj.</summary>
    private const string ResourcePrefix = "RazorReaper.I18n.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads one language's flat key/value map. An unreadable or missing resource yields an empty
    /// map, which the localizer resolves through its English fallback — a broken translation file
    /// degrades to English instead of crashing at startup.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + code + ".json");
        if (stream is null) return FrozenDictionary<string, string>.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Options);
            return parsed is null
                ? FrozenDictionary<string, string>.Empty
                : parsed.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return FrozenDictionary<string, string>.Empty;
        }
    }

    /// <summary>The resource names actually embedded, for the tests that check none went missing.</summary>
    public static IReadOnlyList<string> EmbeddedCodes() =>
        typeof(LanguageCatalog).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^".json".Length])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
