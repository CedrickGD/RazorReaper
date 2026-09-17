namespace RazorReaper.Services;

public sealed record FeedbackSubmissionResult(bool Success, string Message, string? ReportId = null);

/// <summary>
/// The "kind" the admin panel's POST /api/feedback accepts. Older clients send none, and the
/// server then treats a body with diagnostics as support and one without as feedback — the
/// same split these two values make explicit.
/// </summary>
public static class FeedbackKinds
{
    /// <summary>Ideas and opinions; sent without a diagnostic snapshot.</summary>
    public const string Feedback = "feedback";

    /// <summary>A problem report; sent with the diagnostic snapshot.</summary>
    public const string Support = "support";
}

public interface IFeedbackService
{
    /// <summary>
    /// Submits user feedback (ideas, opinions — kind "feedback") to the admin panel without a
    /// diagnostic snapshot. Automatically attaches machine/HWID/license/version identity so the
    /// admin can follow up. <paramref name="contact"/> is an optional user-provided Discord/email
    /// handle. The result carries the report id the panel assigned, when it sent one.
    /// </summary>
    Task<FeedbackSubmissionResult> SubmitAsync(string message, string? contact, CancellationToken cancellationToken = default);

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
