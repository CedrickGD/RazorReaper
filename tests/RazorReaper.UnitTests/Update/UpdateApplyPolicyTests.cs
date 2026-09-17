using RazorReaper.Services;

namespace RazorReaper.UnitTests.Update;

/// <summary>
/// The hybrid update flow in one table: downloading is automatic, restarting is not. These pin
/// the four things that must never regress — a finished download does not close the app, a
/// mandatory release does, a live game or macro stops both, and the next start applies what is
/// waiting instead of throwing it away (which is what 1.4.8 did on every single launch).
/// </summary>
public sealed class UpdateApplyPolicyTests
{
    private static readonly Version Running = new(1, 5, 3);
    private static readonly Version Staged = new(1, 5, 4);

    private static UpdateApplyInputs Inputs(
        UpdateApplyTrigger trigger,
        bool mandatory = false,
        bool ark = false,
        bool macro = false,
        bool complete = true,
        Version? staged = null,
        Version? lastFailed = null)
        => new(
            StagedVersion: staged ?? Staged,
            RunningVersion: Running,
            InstallerComplete: complete,
            IsMandatory: mandatory,
            ArkRunning: ark,
            MacroRunning: macro,
            Trigger: trigger,
            LastFailedVersion: lastFailed);

    [Fact]
    public void AFinishedDownloadStaysReadyAndNeverRestartsByItself()
    {
        Assert.Equal(
            UpdateApplyDecision.StayReady,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Download)));
    }

    [Fact]
    public void AFinishedDownloadStaysReadyEvenWithNothingInTheWay()
    {
        var decision = UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Download, ark: false, macro: false));

        Assert.NotEqual(UpdateApplyDecision.Apply, decision);
        Assert.Equal(UpdateApplyDecision.StayReady, decision);
    }

    [Fact]
    public void AMandatoryReleaseRestartsAsSoonAsTheGateIsClear()
    {
        Assert.Equal(
            UpdateApplyDecision.Apply,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Download, mandatory: true)));

        Assert.Equal(
            UpdateApplyDecision.Apply,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Mandatory, mandatory: true)));
    }

    [Theory]
    [InlineData(UpdateApplyTrigger.Button)]
    [InlineData(UpdateApplyTrigger.Tray)]
    [InlineData(UpdateApplyTrigger.Startup)]
    public void AskingForItApplies(UpdateApplyTrigger trigger)
    {
        Assert.Equal(UpdateApplyDecision.Apply, UpdateApplyPolicy.Decide(Inputs(trigger)));
    }

    [Theory]
    [InlineData(UpdateApplyTrigger.Button)]
    [InlineData(UpdateApplyTrigger.Tray)]
    [InlineData(UpdateApplyTrigger.Startup)]
    [InlineData(UpdateApplyTrigger.Mandatory)]
    public void ARunningGameHoldsEveryTriggerBack(UpdateApplyTrigger trigger)
    {
        Assert.Equal(
            UpdateApplyDecision.Gated,
            UpdateApplyPolicy.Decide(Inputs(trigger, mandatory: true, ark: true)));
    }

    [Theory]
    [InlineData(UpdateApplyTrigger.Button)]
    [InlineData(UpdateApplyTrigger.Mandatory)]
    public void ARunningMacroHoldsEveryTriggerBack(UpdateApplyTrigger trigger)
    {
        Assert.Equal(
            UpdateApplyDecision.Gated,
            UpdateApplyPolicy.Decide(Inputs(trigger, mandatory: true, macro: true)));
    }

    /// <summary>Gated is not "give up": the installer is still staged, so the caller keeps the
    /// action offered and the mandatory retry has something to come back to.</summary>
    [Fact]
    public void AGatedUpdateIsNotDiscarded()
    {
        var decision = UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Button, ark: true));

        Assert.NotEqual(UpdateApplyDecision.NoUpdate, decision);
        Assert.Equal(UpdateApplyDecision.Gated, decision);
    }

    [Fact]
    public void StartupAppliesACompleteInstallerForANewerVersion()
    {
        Assert.Equal(
            UpdateApplyDecision.Apply,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Startup)));
    }

    [Fact]
    public void APartialFileIsNeverRun()
    {
        Assert.Equal(
            UpdateApplyDecision.NoUpdate,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Startup, complete: false)));
    }

    [Theory]
    [InlineData("1.5.3")]
    [InlineData("1.5.2")]
    public void AnInstallerForTheRunningBuildOrOlderIsNotAnUpdate(string staged)
    {
        Assert.Equal(
            UpdateApplyDecision.NoUpdate,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Startup, staged: Version.Parse(staged))));
    }

    [Fact]
    public void NothingStagedIsNotAnUpdate()
    {
        var inputs = new UpdateApplyInputs(
            StagedVersion: null,
            RunningVersion: Running,
            InstallerComplete: true,
            IsMandatory: true,
            ArkRunning: false,
            MacroRunning: false,
            Trigger: UpdateApplyTrigger.Startup);

        Assert.Equal(UpdateApplyDecision.NoUpdate, UpdateApplyPolicy.Decide(inputs));
    }

    // ---- After a failed install --------------------------------------------------
    // The orchestrator's marker says this exact installer already ran and came back non-zero.
    // Handing it to the same unattended path again is the same attempt with the same answer:
    // a UAC prompt and a restart nobody asked for, on every launch, until the second failure
    // throws the file away. The retry is the user's to ask for.

    [Fact]
    public void TheStartupPassDoesNotRetryAnInstallerThatAlreadyFailed()
    {
        Assert.Equal(
            UpdateApplyDecision.StayReady,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Startup, lastFailed: Staged)));
    }

    [Theory]
    [InlineData(UpdateApplyTrigger.Button)]
    [InlineData(UpdateApplyTrigger.Tray)]
    public void TheUserCanStillRetryAnInstallerThatFailed(UpdateApplyTrigger trigger)
    {
        Assert.Equal(
            UpdateApplyDecision.Apply,
            UpdateApplyPolicy.Decide(Inputs(trigger, lastFailed: Staged)));
    }

    /// <summary>The mandatory path is the other exemption: it is not waiting for anyone.</summary>
    [Theory]
    [InlineData(UpdateApplyTrigger.Mandatory)]
    [InlineData(UpdateApplyTrigger.Startup)]
    public void AMandatoryReleaseStillAppliesAfterAFailedInstall(UpdateApplyTrigger trigger)
    {
        Assert.Equal(
            UpdateApplyDecision.Apply,
            UpdateApplyPolicy.Decide(Inputs(trigger, mandatory: true, lastFailed: Staged)));
    }

    /// <summary>A failure recorded against another build says nothing about this installer.</summary>
    [Fact]
    public void AFailureOnADifferentVersionLeavesTheStartupPassAlone()
    {
        Assert.Equal(
            UpdateApplyDecision.Apply,
            UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Startup, lastFailed: new Version(1, 5, 2))));
    }

    /// <summary>Held back, not thrown away: the button has to have something to press.</summary>
    [Fact]
    public void AFailedInstallerIsNotDiscardedByThePolicy()
    {
        var decision = UpdateApplyPolicy.Decide(Inputs(UpdateApplyTrigger.Startup, lastFailed: Staged));

        Assert.NotEqual(UpdateApplyDecision.NoUpdate, decision);
        Assert.NotEqual(UpdateApplyDecision.Apply, decision);
    }

    // ---- Cleanup ----------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CleanupKeepsACompleteInstallerForANewerVersion()
    {
        Assert.True(UpdateApplyPolicy.KeepStagedInstaller(
            Staged,
            Running,
            installerComplete: true,
            stagedAt: Now.AddDays(-1),
            now: Now));
    }

    [Fact]
    public void CleanupDeletesAPartialFile()
    {
        Assert.False(UpdateApplyPolicy.KeepStagedInstaller(
            Staged,
            Running,
            installerComplete: false,
            stagedAt: Now,
            now: Now));
    }

    [Theory]
    [InlineData("1.5.3")]
    [InlineData("1.4.9")]
    public void CleanupDeletesAnInstallerWeHaveAlreadyOutgrown(string staged)
    {
        Assert.False(UpdateApplyPolicy.KeepStagedInstaller(
            Version.Parse(staged),
            Running,
            installerComplete: true,
            stagedAt: Now,
            now: Now));
    }

    [Fact]
    public void CleanupDeletesAnInstallerPastItsShelfLife()
    {
        var stagedAt = Now - UpdateApplyPolicy.StagedInstallerLifetime - TimeSpan.FromMinutes(1);

        Assert.False(UpdateApplyPolicy.KeepStagedInstaller(Staged, Running, true, stagedAt, Now));
    }

    [Fact]
    public void CleanupKeepsAnInstallerRightOnTheLimit()
    {
        var stagedAt = Now - UpdateApplyPolicy.StagedInstallerLifetime;

        Assert.True(UpdateApplyPolicy.KeepStagedInstaller(Staged, Running, true, stagedAt, Now));
    }

    /// <summary>A clock that moved backwards must not read as "staged in the future, so stale".</summary>
    [Fact]
    public void CleanupSurvivesAClockThatWentBackwards()
    {
        Assert.True(UpdateApplyPolicy.KeepStagedInstaller(Staged, Running, true, Now.AddDays(3), Now));
    }

    [Fact]
    public void CleanupDeletesAnInstallerWithNoRecordedVersion()
    {
        Assert.False(UpdateApplyPolicy.KeepStagedInstaller(null, Running, true, Now, Now));
    }

    // ---- Telemetry trigger names -------------------------------------------------

    [Theory]
    [InlineData(UpdateApplyTrigger.Button, false, "button")]
    [InlineData(UpdateApplyTrigger.Startup, false, "startup")]
    [InlineData(UpdateApplyTrigger.Tray, false, "tray")]
    [InlineData(UpdateApplyTrigger.Mandatory, true, "mandatory")]
    [InlineData(UpdateApplyTrigger.Download, true, "mandatory")]
    [InlineData(UpdateApplyTrigger.Download, false, "download")]
    public void TheTelemetryTriggerNamesAreStable(UpdateApplyTrigger trigger, bool mandatory, string expected)
    {
        Assert.Equal(expected, UpdateApplyPolicy.TriggerName(trigger, mandatory));
    }
}
