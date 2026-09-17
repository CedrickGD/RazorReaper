using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// The hybrid update flow. Downloading is still silent and automatic — on launch, every 30
/// minutes, and on demand — but a finished download no longer closes the app. It becomes
/// <see cref="IsInstallerReady"/>: the bell shows its dot, the What's new view offers
/// "Restart &amp; update", and the tray menu offers the same. Whoever asks first wins; nobody
/// asking is fine too, because the next start applies it before anything else runs.
///
/// Two things still restart the app on their own: a release the manifest marks
/// <c>&lt;mandatory&gt;</c>, and the staged-installer pass at the next start. Both go through the
/// same gate as the buttons — never while ARK or a macro is live (<see cref="IUpdateActivityGate"/>).
/// </summary>
public interface IAutoUpdateManager
{
    bool IsChecking { get; }

    /// <summary>An installer for a newer version is on disk, verified, and waiting to be applied.</summary>
    bool IsInstallerReady { get; }

    /// <summary>The handoff was asked for and the warning toast is counting down.</summary>
    bool IsInstallLaunching { get; }

    bool IsDownloading { get; }
    int? DownloadProgressPercent { get; }
    Version? PendingVersion { get; }
    string StatusMessage { get; }
    UpdateCheckResult? LastCheckResult { get; }

    event Action? StateChanged;

    /// <summary>
    /// Raised once the app should shut down so the staged installer can run. The handler calls
    /// <see cref="LaunchPendingInstaller"/> and then exits; the orchestrator it spawns waits for
    /// this process to die before installing.
    /// </summary>
    event Action? InstallRequested;

    /// <summary>
    /// Applies a staged installer left over from an earlier session, then runs the first check
    /// and keeps checking on an interval.
    /// </summary>
    Task RunStartupCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The same check-and-download pass the interval runs, on demand — "Check again" in the
    /// What's new view, for when the automatic download did not go through (offline at the time,
    /// say) and the user does not want to wait for the next interval. A no-op while a check, a
    /// download or a staged install is already in flight.
    /// </summary>
    Task CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the staged update to be applied now. Returns false and leaves the update staged
    /// when nothing is ready, or when ARK or a macro is live — in which case the user is told,
    /// and the action stays available.
    /// </summary>
    Task<bool> ApplyUpdateNowAsync(UpdateApplyTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns the installer orchestrator. Only ever acts after an install was actually requested
    /// — the process-exit handlers call it unconditionally, and a staged installer that is merely
    /// waiting must not turn "Quit" into a silent install plus a relaunch.
    /// </summary>
    bool LaunchPendingInstaller();

    /// <summary>
    /// Called when the app tried to hand off and stayed open anyway. The installer stays staged
    /// (the next start applies it); only the in-flight handoff is cleared.
    /// </summary>
    void ResetPendingInstaller();

    /// <summary>
    /// The version we were on before this one, when the running build is newer than the last one
    /// this install saw. Also emits the <c>update_applied</c> telemetry row.
    /// </summary>
    Version? DetectVersionUpgrade();
}
