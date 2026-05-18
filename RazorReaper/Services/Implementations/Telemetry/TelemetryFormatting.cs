using System.Text.RegularExpressions;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Stateless helpers that hash, clamp, and trim values into the shapes the telemetry backend
/// expects. Kept separate from the live service so the formatting rules can be reasoned about
/// (and unit-tested) without touching HTTP transport or identity state.
/// </summary>
internal static class TelemetryFormatting
{
    private static readonly Regex InvalidIdentifierChars = new("[^a-zA-Z0-9._:-]", RegexOptions.Compiled);

    /// <summary>Validate telemetry config. Returns false with a human-readable <paramref name="error"/>
    /// describing the first problem encountered; the caller logs it (once) and skips the push.</summary>
    public static bool HasValidConfiguration(TelemetrySettings settings, out string error)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            error = "Telemetry endpoint is missing.";
            return false;
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Telemetry endpoint must be a valid HTTP/HTTPS URL.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.AppKey))
        {
            error = "Telemetry AppKey is missing.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Strip <paramref name="value"/> down to an identifier the backend's tagging engine
    /// accepts: ASCII letters/digits and the small set of separators (._-:), collapsed runs of
    /// underscore, max 64 characters. Falls back to <paramref name="fallback"/> if the input
    /// reduces to nothing.</summary>
    public static string SanitizeIdentifier(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        normalized = normalized.Replace(' ', '_');
        normalized = InvalidIdentifierChars.Replace(normalized, "_");
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        if (normalized.Length > 64)
        {
            normalized = normalized[..64];
        }

        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    /// <summary>Trim a free-text message and cap to 500 chars so we don't ship a stack trace.</summary>
    public static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = message.Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    /// <summary>Wire-format spelling of <see cref="TelemetryEventStatus"/> — keep in sync with
    /// the backend's accepted values.</summary>
    public static string ToStatusText(TelemetryEventStatus status)
    {
        return status switch
        {
            TelemetryEventStatus.Ok => "ok",
            TelemetryEventStatus.Degraded => "degraded",
            TelemetryEventStatus.Down => "down",
            _ => "ok"
        };
    }

    public static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public static async Task<string> SafeReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength];
    }
}
