using RazorReaper.Services;

namespace RazorReaper.UnitTests;

/// <summary>
/// The driver-rejection message (and any other vendor-aware guidance built from it) must name
/// the control panel that actually exists on the user's GPU — never NVIDIA's on an AMD or Intel
/// machine, and neutral wording when the vendor could not be detected.
/// </summary>
public sealed class StretchedResServiceTests
{
    // Mirrors the private DISP_CHANGE_BADMODE Win32 return code in StretchedResService.
    private const int DispChangeBadMode = -2;

    [Theory]
    [InlineData(GpuVendor.Nvidia, "NVIDIA Control Panel")]
    [InlineData(GpuVendor.Amd, "AMD Software: Adrenalin Edition")]
    [InlineData(GpuVendor.Intel, "Intel Graphics Command Center")]
    public void CustomResolutionPathNamesTheDetectedVendorsOwnTool(GpuVendor vendor, string expectedSubstring)
    {
        var path = StretchedResService.DescribeCustomResolutionPath(vendor);

        Assert.Contains(expectedSubstring, path);
    }

    [Fact]
    public void CustomResolutionPathIsNeutralForUnknownVendor()
    {
        var path = StretchedResService.DescribeCustomResolutionPath(GpuVendor.Unknown);

        Assert.DoesNotContain("NVIDIA", path);
        Assert.DoesNotContain("AMD", path);
        Assert.DoesNotContain("Intel", path);
    }

    [Fact]
    public void RejectionMessageForNvidiaPointsToNvidiaControlPanel()
    {
        var message = StretchedResService.DescribeMode(DispChangeBadMode, 1440, 1080, GpuVendor.Nvidia);

        Assert.Contains("NVIDIA Control Panel", message);
        Assert.DoesNotContain("AMD", message);
        Assert.DoesNotContain("Intel", message);
    }

    [Fact]
    public void RejectionMessageForAmdPointsToAdrenalinNotNvidia()
    {
        var message = StretchedResService.DescribeMode(DispChangeBadMode, 1440, 1080, GpuVendor.Amd);

        Assert.Contains("AMD Software: Adrenalin Edition", message);
        Assert.Contains("Custom Resolutions", message);
        Assert.DoesNotContain("NVIDIA", message);
    }

    [Fact]
    public void RejectionMessageForIntelPointsToCommandCenterNotNvidia()
    {
        var message = StretchedResService.DescribeMode(DispChangeBadMode, 1440, 1080, GpuVendor.Intel);

        Assert.Contains("Intel Graphics Command Center", message);
        Assert.Contains("Custom Resolutions", message);
        Assert.DoesNotContain("NVIDIA", message);
    }

    [Fact]
    public void RejectionMessageForUnknownVendorStaysNeutral()
    {
        var message = StretchedResService.DescribeMode(DispChangeBadMode, 1440, 1080, GpuVendor.Unknown);

        Assert.DoesNotContain("NVIDIA", message);
        Assert.DoesNotContain("AMD", message);
        Assert.DoesNotContain("Intel", message);
        Assert.Contains("GPU control panel", message);
    }

    [Fact]
    public void RejectionMessageStillIncludesTheRequestedResolution()
    {
        var message = StretchedResService.DescribeMode(DispChangeBadMode, 1600, 1080, GpuVendor.Amd);

        Assert.Contains("1600", message);
        Assert.Contains("1080", message);
    }
}
