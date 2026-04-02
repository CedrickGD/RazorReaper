using RazorReaper.Models;

namespace RazorReaper.Services;

public interface IAutoUpdateManager
{
    bool IsAutoUpdateEnabled { get; set; }
    bool IsInstallerReady { get; }
    bool IsDownloading { get; }
    int? DownloadProgressPercent { get; }
    Version? PendingVersion { get; }
    string StatusMessage { get; }
    UpdateCheckResult? LastCheckResult { get; }

    event Action? StateChanged;

    Task RunStartupCheckAsync(CancellationToken cancellationToken = default);
    bool LaunchPendingInstaller();
    Version? DetectVersionUpgrade();
}
