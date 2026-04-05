using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Thread-safe implementation of IActivityService for managing application activities.
/// </summary>
public class ActivityService : IActivityService
{
    private readonly ILogger<ActivityService> _logger;
    private readonly AppConfiguration _config;
    private readonly List<ActivityItem> _activities = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public event EventHandler<ActivityItem>? ActivityAdded;

    public ActivityService(
        ILogger<ActivityService> logger,
        IOptions<AppConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <inheritdoc/>
    public void AddActivity(string title, string type = "info")
    {
        try
        {
            var activity = new ActivityItem
            {
                Title = title,
                Type = type,
                Timestamp = DateTime.Now
            };

            lock (_lock)
            {
                _activities.Add(activity);

                var maxActivities = _config.Monitoring.MaxRecentActivities;
                if (_activities.Count > maxActivities)
                {
                    _activities.RemoveRange(0, _activities.Count - maxActivities);
                }
            }

            _logger.LogDebug("Activity added: {Title} (Type: {Type})", title, type);

            // Raise event
            ActivityAdded?.Invoke(this, activity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding activity: {Title}", title);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ActivityItem> GetRecentActivities()
    {
        try
        {
            lock (_lock)
            {
                return _activities
                    .OrderByDescending(a => a.Timestamp)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent activities");
            return new List<ActivityItem>();
        }
    }

    /// <inheritdoc/>
    public void ClearActivities()
    {
        try
        {
            lock (_lock)
            {
                _activities.Clear();
                _logger.LogInformation("All activities cleared");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing activities");
        }
    }
}
