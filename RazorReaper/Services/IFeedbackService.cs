namespace RazorReaper.Services;

public sealed record FeedbackSubmissionResult(bool Success, string Message, string? ReportId = null);

public interface IFeedbackService
{
    /// <summary>
    /// Submits user feedback to the admin panel. Automatically attaches machine/HWID/license/version
    /// identity so the admin can follow up. <paramref name="contact"/> is an optional user-provided
    /// Discord/email handle.
    /// </summary>
    Task<(bool Success, string Message)> SubmitAsync(string message, string? contact, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the same established feedback fields plus an optional versioned diagnostic snapshot.
    /// The snapshot is gathered automatically; callers must not ask the user to run extra steps.
    /// </summary>
    Task<FeedbackSubmissionResult> SubmitWithDiagnosticsAsync(
        string message,
        string? contact,
        string? sourceRoute,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One-click diagnostic report for users who do not have a written message. The established
    /// required message field receives a stable technical description.
    /// </summary>
    Task<FeedbackSubmissionResult> SubmitDiagnosticsAsync(
        string? contact,
        string? sourceRoute,
        CancellationToken cancellationToken = default);
}
