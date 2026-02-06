namespace RazorReaper.Services;

public interface IScopeModeStartupService
{
    Task ApplySavedScopeModeAsync(CancellationToken cancellationToken = default);
}
