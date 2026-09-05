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
    /// Sends diagnostics together with the user's required written description.
    /// </summary>
    Task<FeedbackSubmissionResult> SubmitDiagnosticsAsync(
        string message,
        string? contact,
        string? sourceRoute,
        CancellationToken cancellationToken = default);
}
