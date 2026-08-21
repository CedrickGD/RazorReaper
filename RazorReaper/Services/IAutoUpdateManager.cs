using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Updates are mandatory and unattended: there is no opt-out and no "update now" gesture.
/// The manager checks on launch and then on an interval, downloads any new build, and asks
/// the app to hand off to the installer immediately — which reinstalls silently and
/// relaunches. The Home widget only reports what's happening.
/// </summary>
public interface IAutoUpdateManager
{
    bool IsChecking { get; }
    bool IsInstallerReady { get; }
    bool IsDownloading { get; }
    int? DownloadProgressPercent { get; }
    Version? PendingVersion { get; }
    string StatusMessage { get; }
    UpdateCheckResult? LastCheckResult { get; }

    event Action? StateChanged;

    /// <summary>
    /// Raised once an installer is on disk and the app should shut down so it can run.
    /// The handler calls <see cref="LaunchPendingInstaller"/> and then exits; the
    /// orchestrator it spawns waits for this process to die before installing.
    /// </summary>
    event Action? InstallRequested;

    /// <summary>Runs the first check, then keeps checking on an interval.</summary>
    Task RunStartupCheckAsync(CancellationToken cancellationToken = default);

    bool LaunchPendingInstaller();

    /// <summary>
    /// Discards a staged install after the app failed to hand off to it. The interval
    /// checks stand down while an installer is staged, so an app that stays open has to
    /// say so — otherwise the session is parked on an installer that will never run.
    /// </summary>
    void ResetPendingInstaller();

    Version? DetectVersionUpgrade();
}
