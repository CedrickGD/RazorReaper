using RazorReaper.Models;

namespace RazorReaper.Services;

public interface IUpdateService
{
    Version CurrentVersion { get; }
    string CurrentVersionLabel { get; }
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}
