using System.Text.Json.Serialization;

namespace RazorReaper.Services;

public sealed record SupportReply(
    long Id,
    [property: JsonPropertyName("feedback_id")] long FeedbackId,
    string Message,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("read_at")] DateTimeOffset? ReadAt,
    [property: JsonPropertyName("original_message")] string OriginalMessage,
    [property: JsonPropertyName("report_id")] string ReportId);

public interface ISupportInboxService
{
    IReadOnlyList<SupportReply> Replies { get; }
    int UnreadCount { get; }
    long? NextBefore { get; }
    bool HasLoaded { get; }
    string? Error { get; }
    event Action? Changed;
    Task RefreshAsync(bool older = false, CancellationToken cancellationToken = default);
    Task MarkReadAsync(long replyId, CancellationToken cancellationToken = default);
}
