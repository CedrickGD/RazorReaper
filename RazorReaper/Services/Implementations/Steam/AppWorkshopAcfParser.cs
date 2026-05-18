using System.Globalization;
using System.Text.RegularExpressions;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Parses Valve's ACF (Valve Data Format) <c>appworkshop_&lt;appid&gt;.acf</c> files into a
/// dictionary keyed by workshop item id. Only the fields the workshop scan cares about
/// (title, size, time_updated, time_touched) are extracted — unknown keys/sections are
/// silently skipped so a Steam-side format change doesn't break the parse.
/// </summary>
internal static class AppWorkshopAcfParser
{
    private static readonly Regex QuotedTokenRegex =
        new(@"""((?:\\.|[^""])*)""", RegexOptions.Compiled);

    /// <summary>Per-workshop-item metadata pulled from the ACF file. Fields are nullable so
    /// callers can tell "ACF didn't have it" apart from "ACF said 0".</summary>
    public sealed class WorkshopAcfMetadata
    {
        public string? Title { get; set; }
        public long? SizeBytes { get; set; }
        public DateTime? TimeUpdatedUtc { get; set; }
        public DateTime? TimeTouchedUtc { get; set; }
    }

    public static Dictionary<string, WorkshopAcfMetadata> Parse(string content)
    {
        var metadataById = new Dictionary<string, WorkshopAcfMetadata>(StringComparer.Ordinal);
        var contextStack = new Stack<string>();
        string? pendingKey = null;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(pendingKey))
                {
                    contextStack.Push(pendingKey);
                    pendingKey = null;
                }

                continue;
            }

            if (trimmed.StartsWith("}", StringComparison.Ordinal))
            {
                if (contextStack.Count > 0)
                {
                    contextStack.Pop();
                }

                pendingKey = null;
                continue;
            }

            var tokens = ExtractQuotedTokens(trimmed);
            if (tokens.Count == 1)
            {
                pendingKey = tokens[0];

                if (trimmed.EndsWith("{", StringComparison.Ordinal))
                {
                    contextStack.Push(pendingKey);
                    pendingKey = null;
                }

                continue;
            }

            if (tokens.Count >= 2)
            {
                pendingKey = null;
                ApplyAcfPair(contextStack, tokens[0], tokens[1], metadataById);
            }
        }

        return metadataById;
    }

    private static void ApplyAcfPair(
        Stack<string> contextStack,
        string key,
        string value,
        IDictionary<string, WorkshopAcfMetadata> metadataById)
    {
        if (contextStack.Count < 2)
        {
            return;
        }

        var stack = contextStack.ToArray();
        var itemId = stack[0];
        var section = stack[1];

        if (!IsNumeric(itemId))
        {
            return;
        }

        if (!section.Equals("WorkshopItemsInstalled", StringComparison.OrdinalIgnoreCase) &&
            !section.Equals("WorkshopItemDetails", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!metadataById.TryGetValue(itemId, out var metadata))
        {
            metadata = new WorkshopAcfMetadata();
            metadataById[itemId] = metadata;
        }

        var normalizedKey = key.ToLowerInvariant();
        switch (normalizedKey)
        {
            case "title":
                if (string.IsNullOrWhiteSpace(metadata.Title))
                {
                    metadata.Title = value;
                }
                break;

            case "size":
                if (TryParseInt64(value, out var size))
                {
                    metadata.SizeBytes = size;
                }
                break;

            case "timeupdated":
                if (TryParseUnixTimestamp(value, out var timeUpdated))
                {
                    metadata.TimeUpdatedUtc = timeUpdated;
                }
                break;

            case "timetouched":
                if (TryParseUnixTimestamp(value, out var timeTouched))
                {
                    metadata.TimeTouchedUtc = timeTouched;
                }
                break;
        }
    }

    private static List<string> ExtractQuotedTokens(string line)
    {
        var tokens = new List<string>();
        foreach (Match match in QuotedTokenRegex.Matches(line))
        {
            tokens.Add(UnescapeVdfValue(match.Groups[1].Value));
        }

        return tokens;
    }

    private static string UnescapeVdfValue(string value)
    {
        return value
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    private static bool TryParseInt64(string value, out long parsed)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static bool TryParseUnixTimestamp(string value, out DateTime timestampUtc)
    {
        timestampUtc = default;

        if (!TryParseInt64(value, out var seconds) || seconds <= 0)
        {
            return false;
        }

        try
        {
            timestampUtc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
