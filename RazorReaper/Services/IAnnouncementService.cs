using RazorReaper.Models;

namespace RazorReaper.Services;

public sealed record AnnouncementFetchResult(
    bool Succeeded,
    IReadOnlyList<Announcement> Announcements)
{
    public static AnnouncementFetchResult Success(IReadOnlyList<Announcement> announcements)
        => new(true, announcements);

    public static AnnouncementFetchResult Failure { get; }
        = new(false, Array.Empty<Announcement>());
}

public interface IAnnouncementService
{
    /// <summary>
    /// Fetches the announcements that are currently active and within their display window.
    /// Distinguishes a successful empty response from a network/parse failure so callers can
    /// preserve their last-known announcements while the backend is temporarily unavailable.
    /// </summary>
    Task<AnnouncementFetchResult> GetActiveAsync(CancellationToken cancellationToken = default);
}
