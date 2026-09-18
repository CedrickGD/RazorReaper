using System.Globalization;
using RazorReaper.Services;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

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
        var path = StretchedResService.DescribeCustomResolutionPath(English(), vendor);

        Assert.Contains(expectedSubstring, path);
    }

    [Fact]
    public void CustomResolutionPathIsNeutralForUnknownVendor()
    {
        var path = StretchedResService.DescribeCustomResolutionPath(English(), GpuVendor.Unknown);

        Assert.DoesNotContain("NVIDIA", path);
        Assert.DoesNotContain("AMD", path);
        Assert.DoesNotContain("Intel", path);
    }

    [Fact]
    public void RejectionMessageForNvidiaPointsToNvidiaControlPanel()
    {
        var message = StretchedResService.DescribeMode(English(), DispChangeBadMode, 1440, 1080, GpuVendor.Nvidia);

        Assert.Contains("NVIDIA Control Panel", message);
        Assert.DoesNotContain("AMD", message);
        Assert.DoesNotContain("Intel", message);
    }

    [Fact]
    public void RejectionMessageForAmdPointsToAdrenalinNotNvidia()
    {
        var message = StretchedResService.DescribeMode(English(), DispChangeBadMode, 1440, 1080, GpuVendor.Amd);

        Assert.Contains("AMD Software: Adrenalin Edition", message);
        Assert.Contains("Custom Resolutions", message);
        Assert.DoesNotContain("NVIDIA", message);
    }

    [Fact]
    public void RejectionMessageForIntelPointsToCommandCenterNotNvidia()
    {
        var message = StretchedResService.DescribeMode(English(), DispChangeBadMode, 1440, 1080, GpuVendor.Intel);

        Assert.Contains("Intel Graphics Command Center", message);
        Assert.Contains("Custom Resolutions", message);
        Assert.DoesNotContain("NVIDIA", message);
    }

    [Fact]
    public void RejectionMessageForUnknownVendorStaysNeutral()
    {
        var message = StretchedResService.DescribeMode(English(), DispChangeBadMode, 1440, 1080, GpuVendor.Unknown);

        Assert.DoesNotContain("NVIDIA", message);
        Assert.DoesNotContain("AMD", message);
        Assert.DoesNotContain("Intel", message);
        Assert.Contains("GPU control panel", message);
    }

    [Fact]
    public void RejectionMessageStillIncludesTheRequestedResolution()
    {
        var message = StretchedResService.DescribeMode(English(), DispChangeBadMode, 1600, 1080, GpuVendor.Amd);

        Assert.Contains("1600", message);
        Assert.Contains("1080", message);
    }

    /// <summary>
    /// And the same sentence in the reader's language. The guidance is the longest thing this
    /// service says and the one most worth getting right — it is read by someone whose display
    /// just refused a resolution — so it is worth proving it is resolved rather than baked in,
    /// and that the vendor's own tool keeps the name that vendor gave it.
    /// </summary>
    [Fact]
    public void TheRejectionMessageFollowsTheLanguageAndKeepsTheVendorsOwnName()
    {
        var localizer = new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));
        var english = StretchedResService.DescribeMode(localizer, DispChangeBadMode, 1440, 1080, GpuVendor.Nvidia);

        localizer.SetLanguage(AppLanguages.German);
        var german = StretchedResService.DescribeMode(localizer, DispChangeBadMode, 1440, 1080, GpuVendor.Nvidia);

        Assert.NotEqual(english, german);
        Assert.Contains("NVIDIA", german);
        Assert.Contains("1440", german);

        // Back to English: a message read out of the dictionary once would still be German.
        localizer.SetLanguage(AppLanguages.English);
        Assert.Equal(english, StretchedResService.DescribeMode(localizer, DispChangeBadMode, 1440, 1080, GpuVendor.Nvidia));
    }

    /// <summary>
    /// A Win32 code with no name of its own still says what happened, in the reader's language.
    /// </summary>
    [Fact]
    public void AnUnnamedDisplayChangeCodeIsStillWorded()
    {
        var localizer = new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

        // Not BADMODE, so this goes down the plain result path rather than the guidance one.
        var message = StretchedResService.DescribeMode(localizer, -42, 1440, 1080, GpuVendor.Unknown);

        Assert.Contains("-42", message);
        Assert.DoesNotContain("stretchedres.", message);
    }

    private static ILocalizer English()
        => new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));
}
