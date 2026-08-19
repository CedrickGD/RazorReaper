namespace RazorReaper.Services.Implementations;

internal sealed record LocalPreviewHomeSnapshot(
    string UserName,
    string NetworkName,
    string CpuName,
    string GpuName,
    string MotherboardName,
    string ArkInstallPath,
    string ConfigPath);

internal sealed record LocalPreviewSurfaceStatus(
    string State,
    string Label,
    string Description);

/// <summary>
/// Keeps marketing captures on the real UI while preventing the preview process from reading or
/// displaying machine-specific state. Normal runs remain on the existing production paths.
/// </summary>
internal static class LocalPreviewMarketingPolicy
{
    private static readonly LocalPreviewHomeSnapshot PreviewHome = new(
        UserName: "Operator",
        NetworkName: "Preview network",
        CpuName: "System details hidden",
        GpuName: "System details hidden",
        MotherboardName: "System details hidden",
        ArkInstallPath: string.Empty,
        ConfigPath: string.Empty);

    private const string PreviewWindowTitle = "Razor Reaper";

    private static readonly LocalPreviewSurfaceStatus PreviewIniBuilderStatus = new(
        State: "online",
        Label: "Preview workspace",
        Description: "Sample presets are shown; no ARK files are read or changed.");

    public static LocalPreviewHomeSnapshot ResolveHomeSnapshot(
        IAppRunMode runMode,
        Func<LocalPreviewHomeSnapshot> productionFactory)
    {
        ArgumentNullException.ThrowIfNull(runMode);
        ArgumentNullException.ThrowIfNull(productionFactory);

        return runMode.IsLocalPreview ? PreviewHome : productionFactory();
    }

    public static bool ShouldShowVersion(IAppRunMode runMode)
        => !runMode.IsLocalPreview;

    public static bool ShouldShowSidebarStatus(IAppRunMode runMode)
        => !runMode.IsLocalPreview;

    public static bool ShouldRegisterAutomationScripts(IAppRunMode runMode)
        => !runMode.IsLocalPreview;

    /// <summary>
    /// Whether the ticking clock behind the window title may run. Preview keeps a fixed title so a
    /// capture taken at any moment looks the same, and so nothing has to read the machine clock.
    /// </summary>
    public static bool ShouldRunWindowTitleClock(IAppRunMode runMode)
    {
        ArgumentNullException.ThrowIfNull(runMode);

        return !runMode.IsLocalPreview;
    }

    /// <summary>
    /// Resolves the window title. In preview the production factory is never invoked at all — not
    /// merely discarded — so a title built from the clock cannot leak into a capture.
    /// </summary>
    public static string ResolveWindowTitle(
        IAppRunMode runMode,
        Func<string> productionFactory)
    {
        ArgumentNullException.ThrowIfNull(runMode);
        ArgumentNullException.ThrowIfNull(productionFactory);

        return runMode.IsLocalPreview ? PreviewWindowTitle : productionFactory();
    }

    public static LocalPreviewSurfaceStatus ResolveIniBuilderStatus(
        IAppRunMode runMode,
        string productionState,
        string productionLabel,
        string productionDescription)
    {
        ArgumentNullException.ThrowIfNull(runMode);

        return runMode.IsLocalPreview
            ? PreviewIniBuilderStatus
            : new LocalPreviewSurfaceStatus(productionState, productionLabel, productionDescription);
    }
}
