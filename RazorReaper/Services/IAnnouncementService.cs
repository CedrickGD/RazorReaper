using RazorReaper.Models;

namespace RazorReaper.Services;

public interface IAnnouncementService
{
    /// <summary>
    /// Fetches the announcements that are currently active and within their display window.
    /// Returns an empty list on any network/parse failure (never throws).
    /// </summary>
    Task<IReadOnlyList<Announcement>> GetActiveAsync(CancellationToken cancellationToken = default);
}
