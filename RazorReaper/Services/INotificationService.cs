using RazorReaper.Models;

namespace RazorReaper.Services;

public interface INotificationService
{
    event Action<NotificationMessage>? OnNotificationAdded;
    event Action<string>? OnNotificationRemoved;

    void ShowSuccess(string message, int durationMs = 3500);
    void ShowError(string message, int durationMs = 5000);
    void ShowWarning(string message, int durationMs = 4500);

    /// <summary>
    /// A warning that also shows how long is left before it goes away. Only for the case
    /// where the countdown is the point — the update handoff, where the toast is the sole
    /// warning that the app is about to close itself.
    /// </summary>
    void ShowWarningWithCountdown(string message, int durationMs);
    void ShowInfo(string message, int durationMs = 3500);
    void RemoveNotification(string id);
}
