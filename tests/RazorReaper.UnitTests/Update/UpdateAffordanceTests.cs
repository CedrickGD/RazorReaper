using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests.Update;

/// <summary>
/// The hybrid flow's visible surface, pinned where it is written: the What's new view, the bell,
/// the tray menu, and the manager's own rules about when a staged installer may run. Source
/// scans rather than renders — the same approach the rest of the layout tests take — because the
/// point is the wording and the wiring, both of which are easy to lose in a refactor.
/// </summary>
public sealed class UpdateAffordanceTests
{
    [Fact]
    public void TheOverlayOffersTheRestartOncePastTheDownload()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "WhatsNewOverlay.razor"));

        // Ready → the restart, and it is the primary action.
        Assert.Contains("AutoUpdateManager.IsInstallerReady", overlay, StringComparison.Ordinal);
        Assert.Contains("$\"Restart & update to v{ReleaseLabel(AutoUpdateManager.PendingVersion)}\"", overlay, StringComparison.Ordinal);
        Assert.Contains("class=\"btn btn-primary\" @onclick=\"RestartAndUpdateAsync\"", overlay, StringComparison.Ordinal);
        Assert.Contains("AutoUpdateManager.ApplyUpdateNowAsync(UpdateApplyTrigger.Button)", overlay, StringComparison.Ordinal);

        // Downloading → the progress, and nothing to press.
        Assert.Contains("$\"Downloading… {percent}%\"", overlay, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"btn btn-primary\" disabled>@DownloadingLabel</button>", overlay, StringComparison.Ordinal);

        // Nothing staged → the plain re-check, kept as a secondary.
        Assert.Contains("\"Check again\"", overlay, StringComparison.Ordinal);
        Assert.Contains("class=\"btn btn-secondary\" @onclick=\"UpdateNowAsync\"", overlay, StringComparison.Ordinal);

        // The old label promised an install and only re-checked.
        Assert.DoesNotContain("\"Update now\"", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// A gated restart leaves the button pressable: the user closes ARK and presses it again.
    /// Only the in-flight call disables it.
    /// </summary>
    [Fact]
    public void TheRestartButtonComesBackAfterAGatedAttempt()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "WhatsNewOverlay.razor"));

        var method = overlay.IndexOf("private async Task RestartAndUpdateAsync()", StringComparison.Ordinal);
        Assert.True(method > 0);
        var body = overlay[method..overlay.IndexOf("private async Task UpdateNowAsync()", method, StringComparison.Ordinal)];

        Assert.Contains("_applying = true;", body, StringComparison.Ordinal);
        Assert.Contains("_applying = false;", body, StringComparison.Ordinal);
        Assert.Contains("finally", body, StringComparison.Ordinal);
        Assert.Contains("disabled=\"@_applying\"", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBellKeepsItsDotWhileAnUpdateIsWaiting()
    {
        var indicator = File.ReadAllText(ComponentPath("Shared", "NotificationIndicator.razor"));

        Assert.Contains("private bool UpdateReady => AutoUpdateManager.IsInstallerReady;", indicator, StringComparison.Ordinal);
        Assert.Contains("Unread > 0 || UnseenRelease is not null || UpdateReady", indicator, StringComparison.Ordinal);
        Assert.Contains("ready — restart to install", indicator, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayOffersTheSameRestart()
    {
        var tray = File.ReadAllText(CrosshairPath("CrosshairOverlayWindow.Tray.cs"));

        // "&&" because AppendMenu reads a single & as the mnemonic prefix.
        Assert.Contains("$\"Restart && update (v{updateLabel})\"", tray, StringComparison.Ordinal);
        Assert.Contains("CmdApplyUpdate", tray, StringComparison.Ordinal);
        Assert.Contains("_updateReadyLabel()", tray, StringComparison.Ordinal);
        // The item exists only while something is staged.
        Assert.Contains("if (!string.IsNullOrWhiteSpace(updateLabel))", tray, StringComparison.Ordinal);

        var native = File.ReadAllText(CrosshairPath("CrosshairOverlayWindow.Native.cs"));
        Assert.Contains("private const int CmdApplyUpdate = 1004;", native, StringComparison.Ordinal);

        // Wired to the manager in the platform shell, so the crosshair service keeps no update state.
        var shell = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Platforms", "Windows", "App.xaml.cs"));
        Assert.Contains("crosshair.SetUpdateReadyLabel(label)", shell, StringComparison.Ordinal);
        Assert.Contains("updates.ApplyUpdateNowAsync(RazorReaper.Services.UpdateApplyTrigger.Tray)", shell, StringComparison.Ordinal);
        Assert.Contains("updates.StateChanged += SyncLabel;", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartupPassAppliesAStagedInstallerBeforeItChecks()
    {
        var manager = File.ReadAllText(ManagerPath());

        var method = manager.IndexOf("public async Task RunStartupCheckAsync(", StringComparison.Ordinal);
        Assert.True(method > 0);
        var body = manager[method..manager.IndexOf("public async Task CheckNowAsync(", method, StringComparison.Ordinal)];

        var failureReport = body.IndexOf("ReportPreviousInstallFailure();", StringComparison.Ordinal);
        var restore = body.IndexOf("RestoreStagedInstaller()", StringComparison.Ordinal);
        var apply = body.IndexOf("TryApply(UpdateApplyTrigger.Startup)", StringComparison.Ordinal);
        var check = body.IndexOf("await CheckAndInstallAsync(", StringComparison.Ordinal);

        Assert.True(failureReport >= 0, "The previous install's exit code is read first.");
        Assert.True(restore > failureReport);
        Assert.True(apply > failureReport);
        Assert.True(check > apply, "A staged installer is applied before the app looks for a newer one.");
    }

    [Fact]
    public void CleanupAsksThePolicyInsteadOfDeletingOnSight()
    {
        var manager = File.ReadAllText(ManagerPath());

        Assert.Contains("UpdateApplyPolicy.KeepStagedInstaller(", manager, StringComparison.Ordinal);
        // The 1.4.8 loop: delete whatever is staged on every launch, then download it again.
        Assert.DoesNotContain("Directory.Delete(TempDir, recursive: true)", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("private void CleanupStaleInstaller()", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void QuittingDoesNotTurnIntoASilentInstall()
    {
        var manager = File.ReadAllText(ManagerPath());

        var method = manager.IndexOf("public bool LaunchPendingInstaller()", StringComparison.Ordinal);
        Assert.True(method > 0);
        var body = manager[method..manager.IndexOf("public void ResetPendingInstaller()", method, StringComparison.Ordinal)];

        Assert.Contains("if (!installRequested)", body, StringComparison.Ordinal);
        // Set when the handoff is asked for, and only then.
        Assert.Contains("installRequested = true;", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedHandoffLeavesTheInstallerStagedForTheNextStart()
    {
        var manager = File.ReadAllText(ManagerPath());

        var method = manager.IndexOf("public void ResetPendingInstaller()", StringComparison.Ordinal);
        Assert.True(method > 0);
        var body = manager[method..manager.IndexOf("public Version? DetectVersionUpgrade()", method, StringComparison.Ordinal)];

        Assert.Contains("installRequested = false;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences.Remove(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("isInstallerReady = false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrchestratorRecordsAFailedInstallForTheNextStart()
    {
        var manager = File.ReadAllText(ManagerPath());

        Assert.Contains("set RR_CODE=%ERRORLEVEL%", manager, StringComparison.Ordinal);
        Assert.Contains("if \"\"%RR_CODE%\"\"==\"\"0\"\" goto relaunch", manager, StringComparison.Ordinal);
        // Redirect first: `echo 2>file` would be parsed as a stream redirect, not a digit.
        Assert.Contains(">\"\"{markerPath}\"\" echo %RR_CODE%", manager, StringComparison.Ordinal);
        Assert.Contains("private const string FailureMarkerFileName = \"update-failed.txt\";", manager, StringComparison.Ordinal);

        var report = manager.IndexOf("private void ReportPreviousInstallFailure()", StringComparison.Ordinal);
        Assert.True(report > 0);
        var body = manager[report..manager.IndexOf("private bool HasExhaustedAttempts(", report, StringComparison.Ordinal)];
        Assert.Contains("File.Delete(markerPath);", body, StringComparison.Ordinal);
        Assert.Contains("[\"exit_code\"]", body, StringComparison.Ordinal);
        Assert.Contains("failures >= MaxInstallAttemptsPerVersion", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateSaysWhatToDoAndLeavesTheUpdateStaged()
    {
        var manager = File.ReadAllText(ManagerPath());

        Assert.Contains(
            "\"Close ARK (or stop the running macro) first, then restart to update.\"",
            manager,
            StringComparison.Ordinal);

        var gate = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Services", "Implementations", "UpdateActivityGate.cs"));

        // The flags the rest of the app already keeps — no second answer to the same question.
        Assert.Contains("gameIni.IsArkRunning()", gate, StringComparison.Ordinal);
        Assert.Contains("autoClicker.IsRunning", gate, StringComparison.Ordinal);
        Assert.Contains("AutomationScriptBase.AnyRunning", gate, StringComparison.Ordinal);
        Assert.Contains("SynthesizedInput.AnyActive", gate, StringComparison.Ordinal);
        Assert.Contains("macros.Runners.Any(runner => runner.State != MacroRunnerState.Idle)", gate, StringComparison.Ordinal);

        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "MauiProgram.cs"));
        Assert.Contains("services.AddSingleton<IUpdateActivityGate, UpdateActivityGate>();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewTelemetryRowsAreEmittedWithoutPersonalData()
    {
        var manager = File.ReadAllText(ManagerPath());

        Assert.Contains("\"update_download\"", manager, StringComparison.Ordinal);
        Assert.Contains("\"update_install\"", manager, StringComparison.Ordinal);
        Assert.Contains("\"update_applied\"", manager, StringComparison.Ordinal);

        // update_applied is reported where the upgrade is noticed, at the next start.
        var detect = manager.IndexOf("public Version? DetectVersionUpgrade()", StringComparison.Ordinal);
        var body = manager[detect..manager.IndexOf("private async Task DownloadInstallerAsync(", detect, StringComparison.Ordinal)];
        Assert.Contains("[\"from\"]", body, StringComparison.Ordinal);
        Assert.Contains("[\"to\"]", body, StringComparison.Ordinal);

        // Version strings, counters and a status word — nothing that identifies a machine.
        foreach (var forbidden in new[] { "installerPath", "Environment.UserName", "MachineName", "install_id" })
        {
            var track = manager.IndexOf("private void TrackDownload(", StringComparison.Ordinal);
            var end = manager.IndexOf("private static string Label(", track, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, manager[track..end], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A failed install used to be a log line and a telemetry row: the user pressed "Restart &amp;
    /// update", watched the app come back on the old version, and found the same "ready — restart
    /// to install" sentence waiting. It is a warning and a visible state now.
    /// </summary>
    [Fact]
    public void AFailedInstallIsSaidOutLoud()
    {
        var manager = File.ReadAllText(ManagerPath());

        var report = manager.IndexOf("private void ReportPreviousInstallFailure()", StringComparison.Ordinal);
        Assert.True(report > 0);
        var body = manager[report..manager.IndexOf("private Version? RecordedFailedVersion()", report, StringComparison.Ordinal)];

        Assert.Contains("notifications.ShowWarning(failureMessage);", body, StringComparison.Ordinal);
        Assert.Contains("could not be installed (installer exit code", body, StringComparison.Ordinal);
        Assert.Contains("Restart & update to try again.", body, StringComparison.Ordinal);
        Assert.Contains("installFailureMessage = failureMessage;", body, StringComparison.Ordinal);
        Assert.Contains("statusMessage = failureMessage;", body, StringComparison.Ordinal);
        Assert.Contains("OnStateChanged();", body, StringComparison.Ordinal);

        // The failure outranks "ready — restart to install" wherever that line is set again, so
        // the startup pass cannot paint over it with the sentence that was already wrong.
        Assert.Contains("private string ReadyMessage(Version? staged)", manager, StringComparison.Ordinal);
        Assert.Contains(
            "=> installFailureMessage ?? $\"Update v{Label(staged)} is ready — restart to install.\";",
            manager,
            StringComparison.Ordinal);
        Assert.Contains("statusMessage = ReadyMessage(staged);", manager, StringComparison.Ordinal);

        var contract = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Services", "IAutoUpdateManager.cs"));
        Assert.Contains("string? InstallFailureMessage { get; }", contract, StringComparison.Ordinal);
    }

    /// <summary>
    /// The What's new view turns that message into a real state — the same panel in the warning
    /// tone — and leaves the actions alone, so "Restart &amp; update" is still the retry.
    /// </summary>
    [Fact]
    public void TheOverlayShowsTheFailureAndKeepsTheRestartButton()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "WhatsNewOverlay.razor"));

        Assert.Contains("var installFailure = AutoUpdateManager.InstallFailureMessage;", overlay, StringComparison.Ordinal);
        Assert.Contains("|| check?.HasUpdate == true || installFailure is not null;", overlay, StringComparison.Ordinal);
        Assert.Contains("<strong>Update failed</strong>", overlay, StringComparison.Ordinal);
        Assert.Contains("<span>@installFailure</span>", overlay, StringComparison.Ordinal);
        Assert.Contains(
            "class=\"whats-new-update @(installFailure is not null ? \"is-warning\" : \"\")\"",
            overlay,
            StringComparison.Ordinal);

        // The failed state is inside the panel that carries the actions, so the primary button
        // is still rendered by the IsInstallerReady branch below it.
        var panel = overlay.IndexOf("<div class=\"whats-new-update ", StringComparison.Ordinal);
        var actions = overlay.IndexOf("whats-new-update-actions", panel, StringComparison.Ordinal);
        var failed = overlay.IndexOf("<strong>Update failed</strong>", panel, StringComparison.Ordinal);
        Assert.True(failed > panel && failed < actions, "The failed state sits above the actions, not instead of them.");
        Assert.Contains("@onclick=\"RestartAndUpdateAsync\"", overlay[actions..], StringComparison.Ordinal);

        // Reuses the tone .whats-new-status.is-warning already carries — no new colour.
        var css = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "whats-new-overlay.css"));
        var rule = css.IndexOf(".whats-new-update.is-warning {", StringComparison.Ordinal);
        Assert.True(rule > 0);
        Assert.Contains("rgba(var(--accent-orange-rgb)", css[rule..css.IndexOf('}', rule)], StringComparison.Ordinal);
    }

    /// <summary>Must-fix C, where the app reads it: the policy is asked, with the recorded
    /// failure among the facts, instead of the manager retrying on its own.</summary>
    [Fact]
    public void TheManagerTellsThePolicyWhichBuildAlreadyFailed()
    {
        var manager = File.ReadAllText(ManagerPath());

        Assert.Contains("LastFailedVersion: RecordedFailedVersion()", manager, StringComparison.Ordinal);
        Assert.Contains("Preferences.Get(PrefKeyFailedVersion, \"\")", manager, StringComparison.Ordinal);

        var policy = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Services", "UpdateApplyPolicy.cs"));
        Assert.Contains("Version? LastFailedVersion = null", policy, StringComparison.Ordinal);
        Assert.Contains("input.Trigger == UpdateApplyTrigger.Startup", policy, StringComparison.Ordinal);
        Assert.Contains("input.LastFailedVersion == input.StagedVersion", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Never in the middle of a session" was a promise the app cannot keep: a mandatory release
    /// restarts whenever the gate is clear, session or not. What it does keep is the gate itself,
    /// and both places that describe the flow have to say that and not more.
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("installer/RazorReaper.iss")]
    public void TheUpdateGuaranteeIsWrittenDownPrecisely(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.DoesNotContain("never in the middle of a session", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no longer interrupts a live session out of nowhere", text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("while ARK or a macro is running", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mandatory", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("as soon as that gate is clear", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next start", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The UAC prompt is expected and stays; where it comes from has to be written down next to
    /// the settings that cause it, because the obvious "fix" is to change them.
    /// </summary>
    [Fact]
    public void TheInstallerDocumentsWhyItStillAsksForAdmin()
    {
        var iss = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "RazorReaper.iss"));

        Assert.Contains("DefaultDirName={autopf}", iss, StringComparison.Ordinal);
        Assert.Contains("UAC", iss, StringComparison.Ordinal);
        Assert.Contains("per-user install", iss, StringComparison.Ordinal);
        Assert.Contains("AutoUpdateManager.cs", iss, StringComparison.Ordinal);
    }

    private static string ManagerPath()
        => Path.Combine(RepositoryRoot(), "RazorReaper", "Services", "Implementations", "AutoUpdateManager.cs");

    private static string ComponentPath(string folder, string file)
        => Path.Combine(RepositoryRoot(), "RazorReaper", "Components", folder, file);

    private static string CrosshairPath(string file)
        => Path.Combine(RepositoryRoot(), "RazorReaper", "Services", "Implementations", "Crosshair", file);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
