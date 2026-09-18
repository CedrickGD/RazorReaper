using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Service for managing application activity tracking.
/// Replaces static shared state with a thread-safe, injectable service.
/// </summary>
public interface IActivityService
{
    /// <summary>
    /// Adds a new activity to the recent activities list.
    /// </summary>
    /// <param name="title">The activity title/description, already in the reader's language.</param>
    /// <param name="type">The activity type (e.g., "info", "warning", "success").</param>
    /// <param name="key">
    /// The dictionary key <paramref name="title"/> was worded from. Optional, and null is the
    /// honest answer for a row that is still an English literal — but a caller that resolved a
    /// key and drops it here leaves the diagnostics bundle guessing at the sentence, which it can
    /// only do in English. See <see cref="Models.ActivityItem.Key"/>.
    /// </param>
    void AddActivity(string title, string type = "info", string? key = null);

    /// <summary>
    /// Gets all recent activities, newest first.
    /// </summary>
    /// <returns>A list of recent ActivityItem objects.</returns>
    IReadOnlyList<ActivityItem> GetRecentActivities();

    /// <summary>
    /// Clears all activities.
    /// </summary>
    void ClearActivities();

    /// <summary>
    /// Event raised when a new activity is added.
    /// </summary>
    event EventHandler<ActivityItem>? ActivityAdded;
}
