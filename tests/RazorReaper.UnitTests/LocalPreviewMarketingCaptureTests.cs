using RazorReaper.Services;
using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests;

public sealed class LocalPreviewMarketingCaptureTests
{
    [Fact]
    public void PreviewUsesDeterministicSanitizedHomeSnapshotWithoutCallingProductionFactory()
    {
        var snapshot = LocalPreviewMarketingPolicy.ResolveHomeSnapshot(
            new StubRunMode(true),
            () => throw new InvalidOperationException("Production device state must not be read."));

        Assert.Equal("Operator", snapshot.UserName);
        Assert.Equal("Preview network", snapshot.NetworkName);
        Assert.Equal("System details hidden", snapshot.CpuName);
        Assert.Equal("System details hidden", snapshot.GpuName);
        Assert.Equal("System details hidden", snapshot.MotherboardName);
        Assert.Equal(string.Empty, snapshot.ArkInstallPath);
        Assert.Equal(string.Empty, snapshot.ConfigPath);
    }

    [Fact]
    public void NormalModePreservesProductionHomeSnapshot()
    {
        var expected = new LocalPreviewHomeSnapshot(
            "Real user",
            "Real network",
            "Real CPU",
            "Real GPU",
            "Real board",
            "C:\\ARK",
            "C:\\ARK\\Config");

        var actual = LocalPreviewMarketingPolicy.ResolveHomeSnapshot(
            new StubRunMode(false),
            () => expected);

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void VersionAndAutomationRegistrationRemainProductionOnly(
        bool isPreview,
        bool expectedProductionSurface)
    {
        var runMode = new StubRunMode(isPreview);

        Assert.Equal(expectedProductionSurface, LocalPreviewMarketingPolicy.ShouldShowVersion(runMode));
        Assert.Equal(expectedProductionSurface, LocalPreviewMarketingPolicy.ShouldRegisterAutomationScripts(runMode));
        Assert.Equal(expectedProductionSurface, LocalPreviewMarketingPolicy.ShouldRunWindowTitleClock(runMode));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SidebarStatusIsHiddenOnlyInLocalPreview(
        bool isPreview,
        bool expectedVisible)
    {
        var visible = LocalPreviewMarketingPolicy.ShouldShowSidebarStatus(
            new StubRunMode(isPreview));

        Assert.Equal(expectedVisible, visible);
    }

    [Fact]
    public void PreviewUsesDeterministicWindowTitleWithoutReadingTheClock()
    {
        var title = LocalPreviewMarketingPolicy.ResolveWindowTitle(
            new StubRunMode(true),
            () => throw new InvalidOperationException("Preview must not read the production clock."));

        Assert.Equal("Razor Reaper", title);
    }

    [Fact]
    public void NormalModePreservesProductionWindowTitle()
    {
        var title = LocalPreviewMarketingPolicy.ResolveWindowTitle(
            new StubRunMode(false),
            () => "Razor Reaper — 14:37");

        Assert.Equal("Razor Reaper — 14:37", title);
    }

    [Fact]
    public void PreviewUsesTruthfulNeutralIniBuilderStatus()
    {
        var status = LocalPreviewMarketingPolicy.ResolveIniBuilderStatus(
            new StubRunMode(true),
            "error",
            "ARK installation not found",
            "ARK install not found — make sure Steam and ARK are installed.");

        Assert.Equal("online", status.State);
        Assert.Equal("Preview workspace", status.Label);
        Assert.Equal("Sample presets are shown; no ARK files are read or changed.", status.Description);
    }

    [Fact]
    public void NormalModePreservesProductionIniBuilderStatus()
    {
        var status = LocalPreviewMarketingPolicy.ResolveIniBuilderStatus(
            new StubRunMode(false),
            "warning",
            "ARK is running",
            "Close ARK before applying.");

        Assert.Equal("warning", status.State);
        Assert.Equal("ARK is running", status.Label);
        Assert.Equal("Close ARK before applying.", status.Description);
    }

    private sealed class StubRunMode(bool isLocalPreview) : IAppRunMode
    {
        public bool IsLocalPreview { get; } = isLocalPreview;
    }
}
