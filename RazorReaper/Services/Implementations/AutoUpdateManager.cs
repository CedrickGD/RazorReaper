using Microsoft.Extensions.Logging;
using RazorReaper.Diagnostics;
using RazorReaper.Models;
using System.Diagnostics;
using System.Globalization;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Owns the staged installer: downloads it silently, keeps it across sessions, and hands off to
/// it when the moment is right. What "the right moment" means lives in
/// <see cref="UpdateApplyPolicy"/>; everything MAUI-shaped (preferences, temp files, the
/// orchestrator script) lives here.
/// </summary>
public sealed class AutoUpdateManager : IAutoUpdateManager
{
    private const string PrefKeyLastKnownVersion = "rr.autoupdate.lastknownversion";
    private const string PrefKeyInstallerPath = "rr.autoupdate.installerpath";
    private const string PrefKeyInstallerArgs = "rr.autoupdate.installerargs";
    private const string PrefKeyPendingVersion = "rr.autoupdate.pendingversion";

    // The hybrid flow keeps an installer across sessions, so the next start has to be able to
    // tell a finished download from a half-written file it must never run. The byte count is
    // written only after the response was verified; the ready flag says the write finished.
    private const string PrefKeyReady = "rr.autoupdate.ready";
    private const string PrefKeyInstallerBytes = "rr.autoupdate.installerbytes";
    private const string PrefKeyStagedAt = "rr.autoupdate.stagedat";
    private const string PrefKeyMandatory = "rr.autoupdate.mandatory";
    private const string PrefKeyLaunchTrigger = "rr.autoupdate.launchtrigger";
    private const string PrefKeyFailedVersion = "rr.autoupdate.failedversion";
    private const string PrefKeyFailedCount = "rr.autoupdate.failedcount";

    private const string DefaultInstallerArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "RazorReaperUpdate");
    private const string InstallerFileName = "RazorReaper_Update.exe";

    /// <summary>Written by the orchestrator script when the installer returned a non-zero exit
    /// code, read and deleted at the next start.</summary>
    private const string FailureMarkerFileName = "update-failed.txt";

    /// <summary>Two failures on the same build is the installer saying it will not work here.
    /// A third attempt would be the 1.4.8 download loop with extra steps.</summary>
    private const int MaxInstallAttemptsPerVersion = 2;

    /// <summary>How often to re-check while the app stays open, so a release published
    /// mid-session is picked up without waiting for the next launch.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>How often a mandatory update that ran into the gate tries again.</summary>
    private static readonly TimeSpan MandatoryRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Breathing room between the "installing" toast and the app vanishing.</summary>
    private static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(4);

    /// <summary>What the user is told when ARK or a macro is in the way. The update stays ready
    /// and the action stays available — this is a "not yet", not a failure.</summary>
    internal const string GatedMessage = "Close ARK (or stop the running macro) first, then restart to update.";

    private readonly IUpdateService updateService;
    private readonly HttpClient httpClient;
    private readonly INotificationService notifications;
    private readonly IUpdateActivityGate activityGate;
    private readonly ITelemetryService telemetry;
    private readonly ILogger<AutoUpdateManager> logger;

    private int recurringStarted;
    private int orchestratorLaunched;
    private int handoffStarted;
    private int mandatoryRetryStarted;

    private volatile bool isChecking;
    private volatile bool isDownloading;
    private volatile bool isInstallerReady;
    private volatile bool isInstallLaunching;

    /// <summary>Set the moment a handoff is asked for. <see cref="LaunchPendingInstaller"/>
    /// refuses to do anything until it is.</summary>
    private volatile bool installRequested;

    private int _downloadProgressPercent = -1; // -1 means null
    private volatile string statusMessage = "";

    /// <summary>What the user is told about the install that failed at the previous handoff, or
    /// null when the last one worked (or there was none). Outranks the "ready" status line for
    /// the rest of the session, because "ready — restart to install" is exactly the sentence
    /// that was on screen before the installer silently did nothing.</summary>
    private volatile string? installFailureMessage;

    private Version? pendingVersion;
    private UpdateCheckResult? lastCheckResult;
    private string? installerPath;
    private string? installerArgs;
    private long stagedBytes;
    private volatile bool stagedIsMandatory;
    private UpdateApplyTrigger requestedTrigger = UpdateApplyTrigger.Download;

    public AutoUpdateManager(
        IUpdateService updateService,
        HttpClient httpClient,
        INotificationService notifications,
        IUpdateActivityGate activityGate,
        ITelemetryService telemetry,
        ILogger<AutoUpdateManager> logger)
    {
        this.updateService = updateService;
        this.httpClient = httpClient;
        this.notifications = notifications;
        this.activityGate = activityGate;
        this.telemetry = telemetry;
        this.logger = logger;
    }

    public event Action? StateChanged;
    public event Action? InstallRequested;

    public bool IsChecking => isChecking;
    public bool IsInstallerReady => isInstallerReady;
    public bool IsInstallLaunching => isInstallLaunching;
    public bool IsDownloading => isDownloading;
    public int? DownloadProgressPercent => _downloadProgressPercent >= 0 ? _downloadProgressPercent : null;
    public Version? PendingVersion => pendingVersion;
    public string StatusMessage => statusMessage;
    public string? InstallFailureMessage => installFailureMessage;
    public UpdateCheckResult? LastCheckResult => lastCheckResult;

    public async Task RunStartupCheckAsync(CancellationToken cancellationToken = default)
    {
        // Before anything else: was the last handoff's installer actually able to run, and is
        // there still a usable installer on disk? An update staged in an earlier session is
        // applied here, while nothing is in flight and the window has barely appeared.
        ReportPreviousInstallFailure();

        if (RestoreStagedInstaller())
        {
            TryApply(UpdateApplyTrigger.Startup);
        }

        // Runs either way. If the handoff above went through, this process has a few seconds
        // left and the check is harmless — the download path stands down while an install is
        // requested. If it was gated (ARK is often already running: the ARK link is what
        // launched us), the session still gets its check result and its interval.
        await CheckAndInstallAsync(cancellationToken);
        StartRecurringChecks();
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        // Same guard as the interval loop, plus "already checking": two overlapping passes
        // would download the same build twice and race over the same installer file.
        if (isChecking || isInstallerReady || isDownloading) return;

        // Off the caller's context, like the startup pass (App.RunStartupTask): this is
        // called from a Blazor click handler, and an installer download awaited on the
        // renderer's dispatcher would post every read continuation back through it.
        await Task.Run(() => CheckAndInstallAsync(cancellationToken), cancellationToken);
    }

    public Task<bool> ApplyUpdateNowAsync(
        UpdateApplyTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        // The gate enumerates processes, which is slow enough to notice on a click handler.
        return Task.Run(() => TryApply(trigger), cancellationToken);
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
                // A handoff is already in flight — the process is on its way out.
                if (isDownloading || installRequested) continue;

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
        // A handoff is already under way — the status line belongs to it, not to a check whose
        // answer nobody will be around to read.
        if (installRequested) return;

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

        if (isInstallerReady)
        {
            // Already staged. Same build (the common case): keep it and let the policy decide
            // whether this is a release that may not wait. A newer one means the staged file is
            // now stale, so it goes and the new build is fetched.
            if (pendingVersion is not null && result.LatestVersion is not null && result.LatestVersion > pendingVersion)
            {
                logger.LogInformation(
                    "A newer build v{Latest} superseded the staged v{Staged}; re-downloading",
                    result.LatestVersion,
                    pendingVersion);
                DiscardStagedInstaller();
            }
            else
            {
                stagedIsMandatory |= result.IsMandatory;
                TryApply(UpdateApplyTrigger.Download);
                return;
            }
        }

        // The download itself stays automatic and silent; what happens once it lands does not.
        await DownloadInstallerAsync(result, cancellationToken);
    }

    // ---- Applying ------------------------------------------------------------------

    /// <summary>
    /// Collects the facts, asks <see cref="UpdateApplyPolicy"/>, and acts on the answer.
    /// Returns true only when a handoff was actually started.
    /// </summary>
    private bool TryApply(UpdateApplyTrigger trigger)
    {
        if (!isInstallerReady || installRequested)
        {
            return false;
        }

        var staged = pendingVersion;
        var mandatory = stagedIsMandatory;

        var decision = UpdateApplyPolicy.Decide(new UpdateApplyInputs(
            StagedVersion: staged,
            RunningVersion: updateService.CurrentVersion,
            InstallerComplete: IsStagedInstallerComplete(),
            IsMandatory: mandatory,
            ArkRunning: activityGate.IsArkRunning,
            MacroRunning: activityGate.IsMacroRunning,
            Trigger: trigger,
            LastFailedVersion: RecordedFailedVersion()));

        switch (decision)
        {
            case UpdateApplyDecision.Apply:
                TrackInstall("requested", trigger, staged, mandatory);
                RequestInstall(staged, trigger);
                return true;

            case UpdateApplyDecision.Gated:
                TrackInstall("gated", trigger, staged, mandatory);
                logger.LogInformation("Update v{Version} stays staged: ARK or a macro is running", Label(staged));

                // Only say it when someone is waiting for an answer. ARK is running at startup
                // more often than not (the ARK link is what launched us), and a toast on every
                // launch would be noise; the mandatory retry would repeat it every minute.
                if (trigger is UpdateApplyTrigger.Button or UpdateApplyTrigger.Tray
                    || (mandatory && trigger != UpdateApplyTrigger.Mandatory))
                {
                    notifications.ShowWarning(GatedMessage);
                }

                statusMessage = $"Update v{Label(staged)} is ready — close ARK, then restart to install.";
                OnStateChanged();
                StartMandatoryRetry();
                return false;

            case UpdateApplyDecision.StayReady:
                statusMessage = ReadyMessage(staged);
                OnStateChanged();
                return false;

            default:
                // The file went missing, was truncated, or names a build we already run.
                DiscardStagedInstaller();
                return false;
        }
    }

    /// <summary>
    /// A mandatory release that ran into the gate keeps asking, once a minute, until ARK and the
    /// macros are quiet. Nothing else retries by itself.
    /// </summary>
    private void StartMandatoryRetry()
    {
        if (!stagedIsMandatory) return;
        if (Interlocked.Exchange(ref mandatoryRetryStarted, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(MandatoryRetryInterval);
                while (await timer.WaitForNextTickAsync())
                {
                    if (!isInstallerReady || installRequested) return;
                    if (TryApply(UpdateApplyTrigger.Mandatory)) return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mandatory update retry stopped");
            }
            finally
            {
                Interlocked.Exchange(ref mandatoryRetryStarted, 0);
            }
        });
    }

    /// <summary>
    /// Tells the app to hand off. Warns first and waits <see cref="HandoffGrace"/> so the
    /// window doesn't just disappear out from under whatever the user was doing.
    /// </summary>
    private void RequestInstall(Version? version, UpdateApplyTrigger trigger)
    {
        if (Interlocked.Exchange(ref handoffStarted, 1) != 0) return;

        // Set before the grace period, not after: from here on the exit handlers are allowed to
        // launch the installer, because the user has been told the app is about to restart.
        requestedTrigger = trigger;
        installRequested = true;
        isInstallLaunching = true;
        Preferences.Set(PrefKeyLaunchTrigger, UpdateApplyPolicy.TriggerName(trigger, stagedIsMandatory));

        // A new attempt is underway, so the previous one's warning has had its say.
        installFailureMessage = null;

        var label = Label(version);
        statusMessage = $"Installing v{label} — restarting...";
        OnStateChanged();

        _ = Task.Run(async () =>
        {
            try
            {
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
        // The window-destroying and process-exit handlers call this on every shutdown. Under the
        // hybrid flow an installer can sit staged for days, and "Quit" must not turn into a
        // silent install — a UAC prompt out of nowhere plus an app that comes back after the
        // user closed it. Nothing happens here until a handoff was actually asked for.
        if (!installRequested)
        {
            return false;
        }

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
        var args = installerArgs ?? Preferences.Get(PrefKeyInstallerArgs, DefaultInstallerArgs);

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
            //   4. Leaves a marker file behind if the installer failed
            //   5. Relaunches the freshly installed RazorReaper.exe from its own dir
            //   6. Deletes itself
            var ourExe = Process.GetCurrentProcess().MainModule?.FileName
                         ?? Path.Combine(AppContext.BaseDirectory, "RazorReaper.exe");
            var ourPid = Environment.ProcessId;
            var scriptDir = Path.GetDirectoryName(path) ?? Path.GetTempPath();
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, "rr_update.cmd");
            var markerPath = Path.Combine(TempDir, FailureMarkerFileName);

            // The installer is an Inno Setup build that installs into Program Files, so this is
            // where Windows shows a UAC prompt. That is expected and deliberate: it now follows
            // a restart the user asked for, or lands in the first seconds of a launch, instead
            // of ambushing a session mid-raid. See installer/RazorReaper.iss.
            //
            // RR_CODE is captured on its own line, not inside a parenthesised block — %VAR% in
            // a block is expanded when the block is parsed, which would always read the value
            // from before the installer ran. The failure line is reached by a jump rather than
            // written as `if ... >file echo`, so there is no question about what the redirect
            // attaches to, and the redirect comes before `echo` so a single-digit exit code
            // cannot be parsed as a stream handle (`echo 2>file` redirects stderr).
            // `del "%~f0"` stays the last line: cmd reads this file as it goes.
            var script = $@"@echo off
:wait
tasklist /FI ""PID eq {ourPid}"" 2>nul | find ""{ourPid}"" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
taskkill /F /IM RazorReaper.exe >nul 2>&1
""{path}"" {args}
set RR_CODE=%ERRORLEVEL%
if ""%RR_CODE%""==""0"" goto relaunch
>""{markerPath}"" echo %RR_CODE%
:relaunch
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

            // The staged installer and its preferences stay on disk on purpose. If the install
            // fails, the orchestrator drops a marker and the next start retries from the same
            // file instead of downloading 73 MB again; once the running version has caught up,
            // the cleanup pass deletes it.
            TrackInstall("launched", requestedTrigger, pendingVersion, stagedIsMandatory);

            logger.LogInformation("Launched auto-update orchestrator for installer: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            // Nothing was spawned, so hand the claim back — otherwise a retry after
            // ResetPendingInstaller would be answered with a phantom "already launched".
            Interlocked.Exchange(ref orchestratorLaunched, 0);
            logger.LogError(ex, "Failed to launch auto-update orchestrator: {Path}", path);
            TrackInstall("failed", requestedTrigger, pendingVersion, stagedIsMandatory);
            return false;
        }
    }

    /// <summary>
    /// Called when the app tried to hand off and stayed open anyway. Only the in-flight handoff
    /// is cleared — the installer stays staged, because it is complete and newer, and the next
    /// start applies it before anything else runs.
    /// </summary>
    public void ResetPendingInstaller()
    {
        installRequested = false;
        isInstallLaunching = false;
        Interlocked.Exchange(ref handoffStarted, 0);
        statusMessage = $"Update v{Label(pendingVersion)} couldn't start — it will be applied at the next start.";

        logger.LogWarning("Auto-update handoff failed; the installer stays staged for the next start");
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

        if (previousVersion >= currentVersion)
            return null;

        // The install worked, so forget any failure streak recorded against the build we left.
        Preferences.Remove(PrefKeyFailedVersion);
        Preferences.Remove(PrefKeyFailedCount);

        Track("update_applied", TelemetryEventStatus.Ok, "Update applied.", new Dictionary<string, object?>
        {
            ["from"] = Label(previousVersion),
            ["to"] = Label(currentVersion)
        });

        return previousVersion;
    }

    // ---- Downloading ---------------------------------------------------------------

    private async Task DownloadInstallerAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            statusMessage = "Update available but download URL is missing.";
            OnStateChanged();
            return;
        }

        if (HasExhaustedAttempts(result.LatestVersion))
        {
            statusMessage = $"Update v{Label(result.LatestVersion)} could not be installed — install it manually.";
            logger.LogWarning(
                "Not downloading v{Version} again: its installer already failed {Count} times",
                Label(result.LatestVersion),
                MaxInstallAttemptsPerVersion);
            OnStateChanged();
            return;
        }

        isDownloading = true;
        _downloadProgressPercent = 0;
        statusMessage = "Downloading update...";
        OnStateChanged();

        var startedAt = Stopwatch.GetTimestamp();
        var targetPath = Path.Combine(TempDir, InstallerFileName);
        long bytesWritten = 0;
        long? expectedBytes = null;

        try
        {
            Directory.CreateDirectory(TempDir);

            using (var response = await httpClient.GetAsync(result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                expectedBytes = response.Content.Headers.ContentLength;

                int lastReportedPercent = 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int read;
                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesWritten += read;

                    if (expectedBytes is > 0)
                    {
                        var percent = (int)(bytesWritten * 100 / expectedBytes.Value);
                        if (percent != lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            _downloadProgressPercent = percent;
                            statusMessage = $"Downloading update... {percent}%";
                            OnStateChanged();
                        }
                    }
                }
            }

            // The streams are closed, so the length on disk is the final one. A truncated
            // response (a proxy giving up halfway) used to be staged and run as if it were an
            // installer; it is not, and the whole hybrid flow rests on never doing that.
            var onDisk = new FileInfo(targetPath).Length;
            var complete = onDisk > 0 && (expectedBytes is not > 0 || onDisk == expectedBytes.Value);

            if (!complete)
            {
                logger.LogWarning(
                    "Update download was incomplete: {OnDisk} of {Expected} bytes",
                    onDisk,
                    expectedBytes);
                TryDeleteStagedFile(targetPath);
                isDownloading = false;
                _downloadProgressPercent = -1;
                statusMessage = "Update download was incomplete — it will be retried.";
                TrackDownload("failed", onDisk, startedAt, result.LatestVersion);
                OnStateChanged();
                return;
            }

            StageInstaller(targetPath, result, onDisk);
            TrackDownload("ok", onDisk, startedAt, result.LatestVersion);

            logger.LogInformation("Auto-update installer downloaded: {Path} for v{Version}", targetPath, result.LatestVersion);
            OnStateChanged();

            // Ready, not restarting: only a mandatory release goes straight on to the handoff.
            TryApply(UpdateApplyTrigger.Download);
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
            TrackDownload("failed", bytesWritten, startedAt, result.LatestVersion);
            OnStateChanged();
        }
    }

    private void StageInstaller(string targetPath, UpdateCheckResult result, long bytes)
    {
        var args = result.InstallerArgs ?? DefaultInstallerArgs;

        installerPath = targetPath;
        installerArgs = args;
        pendingVersion = result.LatestVersion;
        stagedBytes = bytes;
        stagedIsMandatory = result.IsMandatory;
        isInstallerReady = true;
        isDownloading = false;
        _downloadProgressPercent = 100;

        // A different build is a different installer, so an earlier failure has nothing left to
        // warn about. The same build staged again keeps the warning — and keeps the policy's
        // hands off the unattended retry.
        if (result.LatestVersion is null || result.LatestVersion != RecordedFailedVersion())
        {
            installFailureMessage = null;
        }

        statusMessage = ReadyMessage(result.LatestVersion);

        Preferences.Set(PrefKeyInstallerPath, targetPath);
        Preferences.Set(PrefKeyInstallerArgs, args);
        Preferences.Set(PrefKeyInstallerBytes, bytes);
        Preferences.Set(PrefKeyStagedAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Preferences.Set(PrefKeyMandatory, result.IsMandatory);
        if (result.LatestVersion != null)
            Preferences.Set(PrefKeyPendingVersion, result.LatestVersion.ToString());

        // Last, so a crash between the writes above never leaves a half-described installer
        // looking ready at the next start.
        Preferences.Set(PrefKeyReady, true);
    }

    // ---- Staged installer across sessions -------------------------------------------

    /// <summary>
    /// Picks up an installer staged by an earlier session, if it is still worth keeping.
    /// Replaces the old unconditional cleanup, which deleted a perfectly good installer on
    /// every launch and left 1.4.8 downloading the same 73 MB forever.
    /// </summary>
    private bool RestoreStagedInstaller()
    {
        try
        {
            var path = Preferences.Get(PrefKeyInstallerPath, "");
            var ready = Preferences.Get(PrefKeyReady, false);
            var bytes = Preferences.Get(PrefKeyInstallerBytes, 0L);

            var staged = Version.TryParse(Preferences.Get(PrefKeyPendingVersion, ""), out var parsedVersion)
                ? parsedVersion
                : null;

            var stagedAt = DateTimeOffset.TryParse(
                Preferences.Get(PrefKeyStagedAt, ""),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedStagedAt)
                ? parsedStagedAt
                : DateTimeOffset.MinValue;

            var onDisk = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            var complete = ready && onDisk && bytes > 0 && new FileInfo(path).Length == bytes;

            if (!UpdateApplyPolicy.KeepStagedInstaller(
                    staged,
                    updateService.CurrentVersion,
                    complete,
                    stagedAt,
                    DateTimeOffset.UtcNow))
            {
                DiscardStagedInstaller();
                return false;
            }

            installerPath = path;
            installerArgs = Preferences.Get(PrefKeyInstallerArgs, DefaultInstallerArgs);
            pendingVersion = staged;
            stagedBytes = bytes;
            stagedIsMandatory = Preferences.Get(PrefKeyMandatory, false);
            isInstallerReady = true;
            _downloadProgressPercent = 100;
            statusMessage = ReadyMessage(staged);

            logger.LogInformation("Picked up a staged installer for v{Version}", Label(staged));
            OnStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not restore the staged installer (non-critical)");
            return false;
        }
    }

    /// <summary>True while the staged file is still exactly the size it was verified at.</summary>
    private bool IsStagedInstallerComplete()
    {
        try
        {
            var path = installerPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return stagedBytes > 0 && new FileInfo(path).Length == stagedBytes;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not measure the staged installer");
            return false;
        }
    }

    /// <summary>Drops a staged installer that is partial, superseded, or past its shelf life.</summary>
    private void DiscardStagedInstaller()
    {
        isInstallerReady = false;
        installerPath = null;
        installerArgs = null;
        pendingVersion = null;
        stagedBytes = 0;
        stagedIsMandatory = false;
        _downloadProgressPercent = -1;

        try
        {
            var stalePath = Preferences.Get(PrefKeyInstallerPath, "");
            if (!string.IsNullOrWhiteSpace(stalePath) && File.Exists(stalePath))
            {
                File.Delete(stalePath);
                logger.LogDebug("Deleted a staged installer that is no longer usable: {Path}", stalePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Staged installer cleanup failed (non-critical)");
        }

        Preferences.Remove(PrefKeyInstallerPath);
        Preferences.Remove(PrefKeyInstallerArgs);
        Preferences.Remove(PrefKeyPendingVersion);
        Preferences.Remove(PrefKeyInstallerBytes);
        Preferences.Remove(PrefKeyStagedAt);
        Preferences.Remove(PrefKeyMandatory);
        Preferences.Remove(PrefKeyReady);
    }

    private static void TryDeleteStagedFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A partial file that cannot be deleted is overwritten by the next download anyway.
        }
    }

    /// <summary>
    /// Reads the marker the orchestrator leaves when the installer returned a non-zero exit
    /// code, reports it, and decides whether the same build is worth one more attempt.
    /// </summary>
    private void ReportPreviousInstallFailure()
    {
        try
        {
            var markerPath = Path.Combine(TempDir, FailureMarkerFileName);
            if (!File.Exists(markerPath)) return;

            var raw = File.ReadAllText(markerPath).Trim();
            File.Delete(markerPath);

            var version = Preferences.Get(PrefKeyPendingVersion, "");
            var trigger = Preferences.Get(PrefKeyLaunchTrigger, "startup");
            logger.LogWarning("The previous update install for v{Version} exited with {Code}", version, raw);

            var parsedCode = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                ? code
                : (int?)null;

            var metrics = new Dictionary<string, object?>
            {
                ["status"] = "failed",
                ["trigger"] = trigger,
                ["version"] = version,
                ["exit_code"] = parsedCode
            };
            Track("update_install", TelemetryEventStatus.Degraded, "Update install failed.", metrics);

            var failures = string.Equals(Preferences.Get(PrefKeyFailedVersion, ""), version, StringComparison.Ordinal)
                ? Preferences.Get(PrefKeyFailedCount, 0) + 1
                : 1;
            Preferences.Set(PrefKeyFailedVersion, version);
            Preferences.Set(PrefKeyFailedCount, failures);

            // One retry. Twice is the installer saying it will not work on this machine, and
            // re-running it forever is the loop this whole change exists to end.
            var givingUp = failures >= MaxInstallAttemptsPerVersion;

            // Say it. A silent failure left the app on the old version with "ready — restart to
            // install" still on screen: the user pressed the button, watched it restart, and
            // came back to the same sentence with nothing to explain it.
            var label = string.IsNullOrWhiteSpace(version) ? "?" : version;
            var exitCode = parsedCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            var failureMessage = givingUp
                ? $"Update to v{label} could not be installed (installer exit code {exitCode}). "
                  + "It has been discarded — install the latest version manually."
                : $"Update to v{label} could not be installed (installer exit code {exitCode}). "
                  + "Restart & update to try again.";

            installFailureMessage = failureMessage;
            statusMessage = failureMessage;
            notifications.ShowWarning(failureMessage);

            if (givingUp)
            {
                logger.LogWarning("Giving up on v{Version} after {Count} failed installs", version, failures);
                DiscardStagedInstaller();
            }

            OnStateChanged();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the update failure marker (non-critical)");
        }
    }

    /// <summary>The build whose installer already came back non-zero on this machine, as the
    /// policy wants it. Persisted, so it still answers after the app was closed and reopened.</summary>
    private Version? RecordedFailedVersion()
    {
        try
        {
            return Version.TryParse(Preferences.Get(PrefKeyFailedVersion, ""), out var failed) ? failed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The "ready" status line, unless a failed install has something more important to
    /// say — "restart to install" is the sentence that was already on screen when nothing
    /// installed.</summary>
    private string ReadyMessage(Version? staged)
        => installFailureMessage ?? $"Update v{Label(staged)} is ready — restart to install.";

    private bool HasExhaustedAttempts(Version? version)
    {
        if (version is null) return false;

        try
        {
            return string.Equals(Preferences.Get(PrefKeyFailedVersion, ""), version.ToString(), StringComparison.Ordinal)
                   && Preferences.Get(PrefKeyFailedCount, 0) >= MaxInstallAttemptsPerVersion;
        }
        catch
        {
            return false;
        }
    }

    // ---- Telemetry -------------------------------------------------------------------

    private void TrackDownload(string status, long bytes, long startedAtTimestamp, Version? version)
    {
        Track(
            "update_download",
            status == "ok" ? TelemetryEventStatus.Ok : TelemetryEventStatus.Degraded,
            status == "ok" ? "Update downloaded." : "Update download failed.",
            new Dictionary<string, object?>
            {
                ["status"] = status,
                ["bytes"] = bytes,
                ["seconds"] = Math.Round(Stopwatch.GetElapsedTime(startedAtTimestamp).TotalSeconds, 2),
                ["version"] = Label(version)
            });
    }

    private void TrackInstall(string status, UpdateApplyTrigger trigger, Version? version, bool mandatory)
    {
        Track(
            "update_install",
            status is "requested" or "launched" ? TelemetryEventStatus.Ok : TelemetryEventStatus.Degraded,
            $"Update install {status}.",
            new Dictionary<string, object?>
            {
                ["status"] = status,
                ["trigger"] = UpdateApplyPolicy.TriggerName(trigger, mandatory),
                ["version"] = Label(version)
            });
    }

    /// <summary>No install id, no paths, no user: version strings, counters and a status word.</summary>
    private void Track(string name, TelemetryEventStatus status, string message, Dictionary<string, object?> metrics)
    {
        try
        {
            _ = telemetry.TrackEventAsync(name, status, message, metrics);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Update telemetry '{Event}' could not be queued", name);
        }
    }

    private static string Label(Version? version)
        => version is null ? "?" : AppVersionInfo.FormatVersion(version);

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
