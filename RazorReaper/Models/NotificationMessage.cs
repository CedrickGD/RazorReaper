namespace RazorReaper.Models;

public class NotificationMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Message { get; set; } = string.Empty;
    public NotificationType Type { get; set; } = NotificationType.Info;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int DurationMs { get; set; } = 3500;

    /// <summary>
    /// Draws a depleting line along the toast for <see cref="DurationMs"/>. Opt-in, and
    /// deliberately not the default: on an ordinary toast the countdown is noise, but when
    /// the toast is the only warning before something happens to the user — the update
    /// handoff closing the app — the remaining time is the whole message.
    /// </summary>
    public bool ShowCountdown { get; set; }
}

public enum NotificationType
{
    Success,
    Error,
    Warning,
    Info
}
