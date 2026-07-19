namespace RazorReaper.Services;

public interface IFeedbackService
{
    /// <summary>
    /// Submits user feedback to the admin panel. Automatically attaches machine/HWID/license/version
    /// identity so the admin can follow up. <paramref name="contact"/> is an optional user-provided
    /// Discord/email handle.
    /// </summary>
    Task<(bool Success, string Message)> SubmitAsync(string message, string? contact, CancellationToken cancellationToken = default);
}
