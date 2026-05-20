using RazorReaper.Models;
using System.Text.RegularExpressions;

namespace RazorReaper.Services.Implementations;

public partial class NotificationService : INotificationService
{
    private const int MaxMessageLength = 180;
    private const int DefaultSuccessDurationMs = 3500;
    private const int DefaultErrorDurationMs = 5000;
    private const int DefaultWarningDurationMs = 4500;
    private const int DefaultInfoDurationMs = 3500;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public event Action<NotificationMessage>? OnNotificationAdded;
    public event Action<string>? OnNotificationRemoved;

    public void ShowSuccess(string message, int durationMs = DefaultSuccessDurationMs)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Success,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void ShowError(string message, int durationMs = DefaultErrorDurationMs)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Error,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void ShowWarning(string message, int durationMs = DefaultWarningDurationMs)
    {
        var notification = new NotificationMessage
        {
            Message = NormalizeMessage(message),
            Type = NotificationType.Warning,
            DurationMs = durationMs
        };
        AddNotification(notification);
    }

    public void ShowInfo(string message, int durationMs = DefaultInfoDurationMs)
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
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();

        if (normalized.Length > MaxMessageLength)
        {
            normalized = normalized.Substring(0, MaxMessageLength - 3).TrimEnd() + "...";
        }

        return normalized;
    }
}
