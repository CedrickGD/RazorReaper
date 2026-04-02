using RazorReaper.Models;
using System.Text.RegularExpressions;

namespace RazorReaper.Services.Implementations;

public class NotificationService : INotificationService
{
    private const int MaxMessageLength = 180;

    public event Action<NotificationMessage>? OnNotificationAdded;
    public event Action<string>? OnNotificationRemoved;

    public void ShowSuccess(string message, int durationMs = 3500)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Success,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void ShowError(string message, int durationMs = 5000)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Error,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void ShowWarning(string message, int durationMs = 4500)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Warning,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void ShowInfo(string message, int durationMs = 3500)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Info,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void RemoveNotification(string id)
    {
        OnNotificationRemoved?.Invoke(id);
    }

    private void AddNotification(NotificationMessage notification)
    {
        if (string.IsNullOrWhiteSpace(notification.Message))
        {
            return;
        }

        OnNotificationAdded?.Invoke(notification);
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = message.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        if (normalized.Length > MaxMessageLength)
        {
            normalized = normalized.Substring(0, MaxMessageLength - 3).TrimEnd() + "...";
        }

        return normalized;
    }
}
