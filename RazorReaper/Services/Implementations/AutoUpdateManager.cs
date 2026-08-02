using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using System.Diagnostics;

namespace RazorReaper.Services.Implementations;

public sealed class AutoUpdateManager : IAutoUpdateManager
{
    private const string PrefKeyLastKnownVersion = "rr.autoupdate.lastknownversion";
    private const string PrefKeyInstallerPath = "rr.autoupdate.installerpath";
    private const string PrefKeyInstallerArgs = "rr.autoupdate.installerargs";
    private const string PrefKeyPendingVersion = "rr.autoupdate.pendingversion";

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "RazorReaperUpdate");
    private static readonly string InstallerFileName = "RazorReaper_Update.exe";

    /// <summary>How often to re-check while the app stays open, so a release published
    /// mid-session is picked up without waiting for the next launch.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>Breathing room between the "installing" toast and the app vanishing.</summary>
    private static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(4);

    private readonly IUpdateService updateService;
    private readonly HttpClient httpClient;
    private readonly INotificationService notifications;
    private readonly ILogger<AutoUpdateManager> logger;

    private int recurringStarted;
    private int orchestratorLaunched;

    private volatile bool isChecking;
    private volatile bool isDownloading;
    private volatile bool isInstallerReady;
    private int _downloadProgressPercent = -1; // -1 means null
    private volatile string statusMessage = "";
    private Version? pendingVersion;
    private UpdateCheckResult? lastCheckResult;
    private string? installerPath;
    private string? installerArgs;

    public AutoUpdateManager(
        IUpdateService updateService,
        HttpClient httpClient,
        INotificationService notifications,
        ILogger<AutoUpdateManager> logger)
    {
        this.updateService = updateService;
        this.httpClient = httpClient;
        this.notifications = notifications;
        this.logger = logger;
    }

    public event Action? StateChanged;
    public event Action? InstallRequested;

    public bool IsChecking => isChecking;
    public bool IsInstallerReady => isInstallerReady;
    public bool IsDownloading => isDownloading;
    public int? DownloadProgressPercent => _downloadProgressPercent >= 0 ? _downloadProgressPercent : null;
    public Version? PendingVersion => pendingVersion;
    public string StatusMessage => statusMessage;
    public UpdateCheckResult? LastCheckResult => lastCheckResult;

    public async Task RunStartupCheckAsync(CancellationToken cancellationToken = default)
    {
        CleanupStaleInstaller();
        await CheckAndInstallAsync(cancellationToken);
        StartRecurringChecks();
    }

    /// <summary>
    /// Re-checks on <see cref="CheckInterval"/> for as long as the app is open. Started
    /// once; the interlock keeps a second call from spawning a second loop.
    /// </summary>
    private void StartRecurringChecks()
    {
        if (Interlocked.Exchange(ref recurringStarted, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync())
            {
                // Once an installer is staged the handoff is already in flight; checking
                // again would only download the same build twice.
                if (isInstallerReady || isDownloading) continue;

                try
                {
                    await CheckAndInstallAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Recurring update check failed");
                }
            }
        });
    }

    private async Task CheckAndInstallAsync(CancellationToken cancellationToken)
    {
        isChecking = true;
        statusMessage = "Checking for updates...";
        OnStateChanged();

        UpdateCheckResult result;
        try
        {
            result = await updateService.CheckForUpdatesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update check failed");
            result = new UpdateCheckResult
            {
                CurrentVersion = updateService.CurrentVersion,
                ErrorMessage = "Update check failed.",
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            isChecking = false;
        }

        lastCheckResult = result;

        if (!result.IsSuccess)
        {
            statusMessage = result.ErrorMessage ?? "Update check failed.";
            OnStateChanged();
            return;
        }

        if (!result.HasUpdate)
        {
            statusMessage = "You're on the latest version.";
            OnStateChanged();
            return;
        }

        // No opt-out: a new build is downloaded and installed as soon as it's seen.
        await DownloadInstallerAsync(result, cancellationToken);
    }

    /// <summary>
    /// Tells the app to hand off. Warns first and waits <see cref="HandoffGrace"/> so the
    /// window doesn't just disappear out from under whatever the user was doing.
    /// </summary>
    private void RequestInstall(Version? version)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var label = version?.ToString() ?? "a new version";
                // Countdown variant, because this toast's lifetime *is* the grace period:
                // when it runs out the window is gone. A static warning gave no hint how
                // much time was left to finish what you were doing.
                notifications.ShowWarningWithCountdown(
                    $"Installing update v{label} — Razor Reaper will restart.",
                    durationMs: (int)HandoffGrace.TotalMilliseconds);

                await Task.Delay(HandoffGrace);

                logger.LogInformation("Requesting install handoff for v{Version}", label);
                InstallRequested?.Invoke();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Install handoff request failed");
            }
        });
    }

    public bool LaunchPendingInstaller()
    {
        // The forced path calls this twice on its own: App.HandleInstallRequested launches
        // the orchestrator and then calls Environment.Exit(0), which fires ProcessExit —
        // and that handler calls in here again. Nothing about the staged state stops the
        // second call, because the installer .exe is still on disk while orchestrator #1
        // sits in its tasklist wait loop. Two orchestrators would mean two silent installs
        // racing over the same files and two relaunches, so one is allowed to win, and a
        // later caller is told the handoff is already underway rather than "it failed".
        if (Volatile.Read(ref orchestratorLaunched) != 0)
        {
            return true;
        }

        var path = installerPath ?? Preferences.Get(PrefKeyInstallerPath, "");
        var args = installerArgs ?? Preferences.Get(PrefKeyInstallerArgs, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        // Claim the launch before touching the disk — the read above is only a cheap
        // shortcut, this is the one that actually decides between concurrent callers.
        if (Interlocked.Exchange(ref orchestratorLaunched, 1) != 0)
        {
            return true;
        }

        try
        {
            // Spawn a self-contained orchestrator script so the relaunch path doesn't depend
            // on the external Inno Setup .iss PostInstall config. The script:
            //   1. Waits for our PID to disappear (so file locks are released)
            //   2. Force-kills any leftover RazorReaper.exe (paranoia)
            //   3. Runs the installer silently
            //   4. Relaunches the freshly installed RazorReaper.exe from its own dir
            //   5. Deletes itself
            var ourExe = Process.GetCurrentProcess().MainModule?.FileName
                         ?? Path.Combine(AppContext.BaseDirectory, "RazorReaper.exe");
            var ourPid = Environment.ProcessId;
            var scriptDir = Path.GetDirectoryName(path) ?? Path.GetTempPath();
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, "rr_update.cmd");

            var script = $@"@echo off
:wait
tasklist /FI ""PID eq {ourPid}"" 2>nul | find ""{ourPid}"" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
taskkill /F /IM RazorReaper.exe >nul 2>&1
""{path}"" {args}
start """" ""{ourExe}""
del ""%~f0"" >nul 2>&1
";
            File.WriteAllText(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            // Drop the in-memory copies as well, not just the prefs: they were the reason a
            // re-entrant call sailed past every guard above.
            installerPath = null;
            installerArgs = null;

            Preferences.Remove(PrefKeyInstallerPath);
            Preferences.Remove(PrefKeyInstallerArgs);
            Preferences.Remove(PrefKeyPendingVersion);

            logger.LogInformation("Launched auto-update orchestrator for installer: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            // Nothing was spawned, so hand the claim back — otherwise a retry after
            // ResetPendingInstaller would be answered with a phantom "already launched".
            Interlocked.Exchange(ref orchestratorLaunched, 0);
            logger.LogError(ex, "Failed to launch auto-update orchestrator: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Called when the app tried to hand off and stayed open anyway. The recurring loop
    /// skips every tick while an installer is staged, and <c>isInstallerReady</c> is only
    /// ever set — so without this the session would never check again and the Home widget
    /// would sit on "Installing v… — restarting..." forever while nothing restarts.
    /// </summary>
    public void ResetPendingInstaller()
    {
        isInstallerReady = false;
        installerPath = null;
        installerArgs = null;
        pendingVersion = null;
        _downloadProgressPercent = -1;
        statusMessage = "Update couldn't start — will retry at the next check.";

        Preferences.Remove(PrefKeyInstallerPath);
        Preferences.Remove(PrefKeyInstallerArgs);
        Preferences.Remove(PrefKeyPendingVersion);

        logger.LogWarning("Auto-update handoff failed; cleared the staged installer so checks resume");
        OnStateChanged();
    }

    public Version? DetectVersionUpgrade()
    {
        var currentVersion = updateService.CurrentVersion;
        var storedText = Preferences.Get(PrefKeyLastKnownVersion, "");

        Preferences.Set(PrefKeyLastKnownVersion, currentVersion.ToString());

        if (string.IsNullOrWhiteSpace(storedText))
            return null;

        if (!Version.TryParse(storedText, out var previousVersion))
            return null;

        if (previousVersion < currentVersion)
            return previousVersion;

        return null;
    }

    private async Task DownloadInstallerAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            statusMessage = "Update available but download URL is missing.";
            OnStateChanged();
            return;
        }

        isDownloading = true;
        _downloadProgressPercent = 0;
        statusMessage = "Downloading update...";
        OnStateChanged();

        try
        {
            Directory.CreateDirectory(TempDir);
            var targetPath = Path.Combine(TempDir, InstallerFileName);

            using var response = await httpClient.GetAsync(result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            long bytesRead = 0;
            int lastReportedPercent = 0;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    var percent = (int)(bytesRead * 100 / totalBytes.Value);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        _downloadProgressPercent = percent;
                        statusMessage = $"Downloading update... {percent}%";
                        OnStateChanged();
                    }
                }
            }

            var args = result.InstallerArgs ?? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

            installerPath = targetPath;
            installerArgs = args;
            pendingVersion = result.LatestVersion;
            isInstallerReady = true;
            isDownloading = false;
            _downloadProgressPercent = 100;
            statusMessage = $"Installing v{result.LatestVersion} — restarting...";

            Preferences.Set(PrefKeyInstallerPath, targetPath);
            Preferences.Set(PrefKeyInstallerArgs, args);
            if (result.LatestVersion != null)
                Preferences.Set(PrefKeyPendingVersion, result.LatestVersion.ToString());

            logger.LogInformation("Auto-update installer downloaded: {Path} for v{Version}", targetPath, result.LatestVersion);
            OnStateChanged();

            // Straight to install — no waiting for the user to close the app.
            RequestInstall(result.LatestVersion);
        }
        catch (OperationCanceledException)
        {
            isDownloading = false;
            _downloadProgressPercent = -1;
            statusMessage = "Download cancelled.";
            OnStateChanged();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download auto-update installer");
            isDownloading = false;
            _downloadProgressPercent = -1;
            statusMessage = "Failed to download update.";
            OnStateChanged();
        }
    }

    private void CleanupStaleInstaller()
    {
        try
        {
            var stalePath = Preferences.Get(PrefKeyInstallerPath, "");
            if (!string.IsNullOrWhiteSpace(stalePath) && File.Exists(stalePath))
            {
                File.Delete(stalePath);
                logger.LogDebug("Cleaned up stale installer: {Path}", stalePath);
            }

            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
            }

            Preferences.Remove(PrefKeyInstallerPath);
            Preferences.Remove(PrefKeyInstallerArgs);
            Preferences.Remove(PrefKeyPendingVersion);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stale installer cleanup failed (non-critical)");
        }
    }

    private void OnStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // UI callback failures should not break the update flow.
        }
    }
}
