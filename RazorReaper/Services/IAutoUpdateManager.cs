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

    /// <summary>
    /// Triggered by the "Update Now" button. Downloads the pending installer (even when
    /// auto-update is off) and returns once the installer is on disk and ready to launch.
    /// No-op if no update is available, a download is already in progress, or the installer
    /// is already prepared.
    /// </summary>
    Task PrepareInstallerOnDemandAsync(CancellationToken cancellationToken = default);
}
