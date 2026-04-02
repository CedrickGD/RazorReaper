using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using System.Diagnostics;

namespace RazorReaper.Services.Implementations;

public sealed class AutoUpdateManager : IAutoUpdateManager
{
    private const string PrefKeyEnabled = "rr.autoupdate.enabled";
    private const string PrefKeyLastKnownVersion = "rr.autoupdate.lastknownversion";
    private const string PrefKeyInstallerPath = "rr.autoupdate.installerpath";
    private const string PrefKeyInstallerArgs = "rr.autoupdate.installerargs";
    private const string PrefKeyPendingVersion = "rr.autoupdate.pendingversion";

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "RazorReaperUpdate");
    private static readonly string InstallerFileName = "RazorReaper_Update.exe";

    private readonly IUpdateService updateService;
    private readonly HttpClient httpClient;
    private readonly ILogger<AutoUpdateManager> logger;

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
        ILogger<AutoUpdateManager> logger)
    {
        this.updateService = updateService;
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public event Action? StateChanged;

    public bool IsAutoUpdateEnabled
    {
        get => Preferences.Get(PrefKeyEnabled, true);
        set
        {
            Preferences.Set(PrefKeyEnabled, value);
            OnStateChanged();
        }
    }

    public bool IsInstallerReady => isInstallerReady;
    public bool IsDownloading => isDownloading;
    public int? DownloadProgressPercent => _downloadProgressPercent >= 0 ? _downloadProgressPercent : null;
    public Version? PendingVersion => pendingVersion;
    public string StatusMessage => statusMessage;
    public UpdateCheckResult? LastCheckResult => lastCheckResult;

    public async Task RunStartupCheckAsync(CancellationToken cancellationToken = default)
    {
        CleanupStaleInstaller();

        statusMessage = "Checking for updates...";
        OnStateChanged();

        UpdateCheckResult result;
        try
        {
            result = await updateService.CheckForUpdatesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup update check failed");
            result = new UpdateCheckResult
            {
                CurrentVersion = updateService.CurrentVersion,
                ErrorMessage = "Update check failed.",
                CheckedAt = DateTimeOffset.UtcNow
            };
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

        if (!IsAutoUpdateEnabled)
        {
            statusMessage = $"Version {result.LatestVersion} available — auto-update is off.";
            OnStateChanged();
            return;
        }

        await DownloadInstallerAsync(result, cancellationToken);
    }

    public bool LaunchPendingInstaller()
    {
        var path = installerPath ?? Preferences.Get(PrefKeyInstallerPath, "");
        var args = installerArgs ?? Preferences.Get(PrefKeyInstallerArgs, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    UseShellExecute = true
                }
            };
            process.Start();

            Preferences.Remove(PrefKeyInstallerPath);
            Preferences.Remove(PrefKeyInstallerArgs);
            Preferences.Remove(PrefKeyPendingVersion);

            logger.LogInformation("Launched auto-update installer: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to launch auto-update installer: {Path}", path);
            return false;
        }
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
            statusMessage = $"Update v{result.LatestVersion} ready — will install when you close the app.";

            Preferences.Set(PrefKeyInstallerPath, targetPath);
            Preferences.Set(PrefKeyInstallerArgs, args);
            if (result.LatestVersion != null)
                Preferences.Set(PrefKeyPendingVersion, result.LatestVersion.ToString());

            logger.LogInformation("Auto-update installer downloaded: {Path} for v{Version}", targetPath, result.LatestVersion);
            OnStateChanged();
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
