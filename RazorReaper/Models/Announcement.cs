using System.Text.Json.Serialization;

namespace RazorReaper.Models;

/// <summary>
/// An admin-authored announcement fetched from the admin panel and shown in the app's Home banner.
/// Shape mirrors the public /api/announcements/active response.
/// </summary>
public class Announcement
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>"info", "warning", or "critical".</summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("starts_at")]
    public string? StartsAt { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}
