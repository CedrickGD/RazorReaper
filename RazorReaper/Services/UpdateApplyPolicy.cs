namespace RazorReaper.Services;

/// <summary>What asked for the update to be applied. Also the <c>trigger</c> field on the
/// <c>update_install</c> telemetry row.</summary>
public enum UpdateApplyTrigger
{
    /// <summary>The download just finished. Only a mandatory release restarts by itself here.</summary>
    Download,

    /// <summary>"Restart &amp; update" in the What's new view.</summary>
    Button,

    /// <summary>A complete installer was found staged at app start.</summary>
    Startup,

    /// <summary>The manifest said <c>&lt;mandatory&gt;true&lt;/mandatory&gt;</c> — includes the retry
    /// while ARK or a macro was in the way.</summary>
    Mandatory,

    /// <summary>"Restart &amp; update" in the tray menu.</summary>
    Tray
}

/// <summary>What the app should do with a staged installer right now.</summary>
public enum UpdateApplyDecision
{
    /// <summary>Nothing usable is staged — no file, a partial one, or a build we already run.</summary>
    NoUpdate,

    /// <summary>Staged and complete, but nobody asked and the release is not mandatory.</summary>
    StayReady,

    /// <summary>Hand off to the installer now.</summary>
    Apply,

    /// <summary>Would apply, but ARK or a macro is live — stay ready and say so.</summary>
    Gated
}

/// <param name="StagedVersion">Version of the installer on disk, or null when nothing is staged.</param>
/// <param name="RunningVersion">The version of the process asking.</param>
/// <param name="InstallerComplete">The file is fully written (size known and matched).</param>
/// <param name="IsMandatory">The manifest marked the release mandatory.</param>
/// <param name="ArkRunning">ARK is running.</param>
/// <param name="MacroRunning">An automation script, the auto clicker or a macro runner is live.</param>
/// <param name="Trigger">Who is asking.</param>
/// <param name="LastFailedVersion">The version whose installer already came back with a non-zero
/// exit code on this machine, or null when nothing has failed.</param>
public readonly record struct UpdateApplyInputs(
    Version? StagedVersion,
    Version RunningVersion,
    bool InstallerComplete,
    bool IsMandatory,
    bool ArkRunning,
    bool MacroRunning,
    UpdateApplyTrigger Trigger,
    Version? LastFailedVersion = null);

/// <summary>
/// The whole "may we restart into the installer?" decision, with no MAUI, no disk and no clock of
/// its own — so it can be read in one screen and tested without an app host. The manager
/// (<c>AutoUpdateManager</c>) collects the facts; this decides.
///
/// The rule the hybrid flow rests on: downloading stays silent and automatic, but a finished
/// download never restarts the app on its own. It waits for the user, for the next start, or for a
/// release the manifest marks mandatory — and never while ARK or a macro is live, because that
/// restart would land in the middle of a raid. An installer that already failed once is not
/// re-run unattended either: that retry is the user's to ask for.
/// </summary>
public static class UpdateApplyPolicy
{
    /// <summary>
    /// How long a staged installer stays worth keeping. Past this it is cheaper to download the
    /// current build again than to install one that has been superseded twice over.
    /// </summary>
    public static readonly TimeSpan StagedInstallerLifetime = TimeSpan.FromDays(14);

    public static UpdateApplyDecision Decide(in UpdateApplyInputs input)
    {
        if (!input.InstallerComplete)
        {
            return UpdateApplyDecision.NoUpdate;
        }

        if (input.StagedVersion is null || input.StagedVersion <= input.RunningVersion)
        {
            return UpdateApplyDecision.NoUpdate;
        }

        // A finished download is not a reason to close the app. Everything else here is either
        // the user asking, the next start (where nothing is in flight anyway), or a release the
        // manifest insists on.
        var wantsApply = input.Trigger != UpdateApplyTrigger.Download || input.IsMandatory;
        if (!wantsApply)
        {
            return UpdateApplyDecision.StayReady;
        }

        // A recorded failure for exactly this build means the last unattended attempt already
        // ran this installer and it came back non-zero. Running it again at the next start is
        // the same attempt with the same answer — a UAC prompt and a restart the user did not
        // ask for, on every single launch, until the second failure discards the file. So the
        // retry needs the button (or the tray, or a release the manifest insists on); the
        // Startup trigger stands down and leaves the update ready and visible instead.
        if (input.Trigger == UpdateApplyTrigger.Startup
            && !input.IsMandatory
            && input.LastFailedVersion is not null
            && input.LastFailedVersion == input.StagedVersion)
        {
            return UpdateApplyDecision.StayReady;
        }

        if (input.ArkRunning || input.MacroRunning)
        {
            return UpdateApplyDecision.Gated;
        }

        return UpdateApplyDecision.Apply;
    }

    /// <summary>
    /// Whether a staged installer survives the cleanup pass at app start. Only partial files,
    /// builds we already run, and installers left over for more than
    /// <see cref="StagedInstallerLifetime"/> are deleted — a complete installer for a newer
    /// version is the whole point of the hybrid flow and must never be swept away, which is
    /// exactly what 1.4.8 did on every launch (download, delete, download again, forever).
    /// </summary>
    public static bool KeepStagedInstaller(
        Version? stagedVersion,
        Version runningVersion,
        bool installerComplete,
        DateTimeOffset stagedAt,
        DateTimeOffset now)
    {
        if (!installerComplete)
        {
            return false;
        }

        if (stagedVersion is null || stagedVersion <= runningVersion)
        {
            return false;
        }

        // A clock that moved backwards yields a negative age, which is not "too old".
        return now - stagedAt <= StagedInstallerLifetime;
    }

    /// <summary>The <c>trigger</c> value for telemetry. A mandatory release reports as mandatory
    /// however it got here, so the panel can tell a forced restart from one the user asked for.</summary>
    public static string TriggerName(UpdateApplyTrigger trigger, bool isMandatory)
    {
        if (trigger == UpdateApplyTrigger.Download)
        {
            return isMandatory ? "mandatory" : "download";
        }

        return trigger switch
        {
            UpdateApplyTrigger.Button => "button",
            UpdateApplyTrigger.Startup => "startup",
            UpdateApplyTrigger.Mandatory => "mandatory",
            UpdateApplyTrigger.Tray => "tray",
            _ => "unknown"
        };
    }
}
