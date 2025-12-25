namespace RazorReaper.Models;

public class NotificationMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; } = NotificationType.Info;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int DurationMs { get; set; } = 3500; // 3.5 seconds default
}

public enum NotificationType
{
    Success,
    Error,
    Warning,
    Info
}
