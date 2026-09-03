namespace RazorReaper.Services.Overlay;

/// <summary>
/// The notifier backend moved from Railway to the NAS (bot.razorreaper.app) on 2026-08-21 and the
/// Railway service was deleted. Users paste the stream endpoint by hand, so endpoints saved before
/// the move still point at the dead host — rewrite them transparently (path, query and token are kept).
/// </summary>
public static class NotifierEndpointMigration
{
    private const string NewHost = "bot.razorreaper.app";

    private static readonly string[] RetiredHosts =
    {
        "razorreaper-bot-production.up.railway.app",
    };

    public static string Migrate(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint ?? "";
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)) return endpoint;
        foreach (var host in RetiredHosts)
        {
            if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            var b = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Host = NewHost, Port = -1 };
            return b.Uri.ToString();
        }
        return endpoint;
    }
}
