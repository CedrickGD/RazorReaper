using RazorReaper.Services.Localization;

namespace RazorReaper.Models;

/// <summary>
/// Represents an activity item displayed in the recent activities feed.
/// </summary>
public class ActivityItem
{
    /// <summary>
    /// Gets or sets the title/description of the activity.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Gets or sets the timestamp when the activity occurred.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the type/category of the activity (e.g., "info", "warning", "success").
    /// </summary>
    public string Type { get; set; } = "info";

    /// <summary>
    /// How long ago the activity occurred, in the reader's language: "Just now", "5m ago",
    /// "2h ago", "3d ago", or a date like "Jan 15".
    /// </summary>
    /// <remarks>
    /// Resolved on read rather than stored. An item sits in the list for the life of the
    /// session, so a stored wording would be as stale as the age it describes.
    /// </remarks>
    public string GetTimeAgo(ILocalizer localizer)
    {
        var span = DateTime.Now - Timestamp;

        if (span.TotalSeconds < 60)
            return localizer.T("activity.justnow");
        if (span.TotalMinutes < 60)
            return localizer.T("activity.minutes", (int)span.TotalMinutes);
        if (span.TotalHours < 24)
            return localizer.T("activity.hours", (int)span.TotalHours);
        if (span.TotalDays < 7)
            return localizer.T("activity.days", (int)span.TotalDays);

        return Timestamp.ToString("MMM dd");
    }
}
