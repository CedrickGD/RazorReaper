namespace RazorReaper.Services;

/// <summary>
/// What has to be quiet before the app may close itself and hand over to the installer.
/// Restarting mid-raid is worse than running a build one version behind for another hour, so a
/// live game or a live macro holds the update back until the user is done.
/// </summary>
public interface IUpdateActivityGate
{
    /// <summary>ARK is running.</summary>
    bool IsArkRunning { get; }

    /// <summary>An automation script, the auto clicker or a macro runner is live — or the input
    /// layer is pressing a key right now.</summary>
    bool IsMacroRunning { get; }

    /// <summary>Either of the two. The one the update path actually asks.</summary>
    bool IsBusy => IsArkRunning || IsMacroRunning;
}
