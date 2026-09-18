using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests;

/// <summary>
/// The two monitor pickers, read off the page source.
///
/// A picker is only worth anything if what it picks reaches the service. Both halves of that are
/// invisible from inside the component — a dropdown wired to the wrong field still renders, and a
/// display call that forgets its device argument still compiles and still works, on a one-monitor
/// desk, which is every desk a test runs on. So the wiring is pinned here: two pickers, one per
/// section, each storing its own monitor, and no call to the service that leaves the device out
/// and lands back on the primary by default.
/// </summary>
public sealed class StretchedResPageTests
{
    [Fact]
    public void TheModeSectionAndTheCustomSectionEachHaveTheirOwnPicker()
    {
        var page = Page();

        // The element, not the type: "IReadOnlyList<Dropdown.Option>" is not a picker.
        Assert.Equal(2, Regex.Matches(page, @"<Dropdown\s").Count);
        Assert.Contains("Value=\"@stretchedDevice\"", page, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"SelectStretchedMonitor\"", page, StringComparison.Ordinal);
        Assert.Contains("Value=\"@customDevice\"", page, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"SelectCustomMonitor\"", page, StringComparison.Ordinal);

        // Both list the same monitors — a second list would be a second answer to the same question.
        Assert.Equal(2, Regex.Matches(page, "Options=\"MonitorOptions\"").Count);
    }

    /// <summary>
    /// Two labels, not one shared control: the picker has to say what it picks in the reader's
    /// language, and it has to say it above each section rather than once at the top of the page.
    /// </summary>
    [Fact]
    public void BothPickersAreLabelledThroughTheDictionary()
        => Assert.Equal(2, Regex.Matches(Page(), @"T\(""stretchedres\.monitor\.label""\)").Count);

    [Fact]
    public void EachPickerPersistsItsOwnMonitor()
    {
        var page = Page();

        Assert.Contains("SaveDeviceChoice(ResolutionFeature.Stretched, Device(deviceName))", page, StringComparison.Ordinal);
        Assert.Contains("SaveDeviceChoice(ResolutionFeature.Custom, Device(deviceName))", page, StringComparison.Ordinal);
        Assert.Contains("LoadDeviceChoice(ResolutionFeature.Stretched)", page, StringComparison.Ordinal);
        Assert.Contains("LoadDeviceChoice(ResolutionFeature.Custom)", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// An apply reads its device from the section it was pressed in. Wiring both to one field is
    /// the defect the second picker exists to prevent, and it looks right on a single monitor.
    /// </summary>
    [Fact]
    public void AnApplyTakesTheDeviceOfTheSectionItCameFrom()
    {
        var page = Page();

        Assert.Contains("var device = Device(isCustom ? customDevice : stretchedDevice);", page, StringComparison.Ordinal);
        Assert.Contains("Stretched.ApplyResolution(width, height, device)", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every query the page makes names a device. The overloads all default to the primary, so a
    /// forgotten argument is silent: the status card would describe the primary while the picker
    /// above it named another screen.
    /// </summary>
    [Theory]
    [InlineData("GetCurrentResolution")]
    [InlineData("GetNativeResolution")]
    [InlineData("GetGpuInfo")]
    [InlineData("RestoreNative")]
    public void NoDisplayCallLeavesTheMonitorOut(string method)
    {
        var call = Regex.Matches(Page(), Regex.Escape($"Stretched.{method}(") + @"\s*\)");

        Assert.True(call.Count == 0,
            $"Stretched.{method}() is called without a device, which silently means the primary "
            + "display. Pass the monitor the section is aimed at.");
    }

    /// <summary>
    /// The fallback note is what makes an unplugged monitor visible. Without it the page quietly
    /// swaps the target and the user finds out when the wrong screen changes.
    /// </summary>
    [Fact]
    public void AnUnpluggedMonitorIsSaidOutLoudInBothSections()
    {
        var page = Page();

        Assert.Contains("stretchedFellBack", page, StringComparison.Ordinal);
        Assert.Contains("customFellBack", page, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(page, @"T\(""stretchedres\.monitor\.fallback""").Count);
    }

    private static string Page() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "Components", "Pages", "StretchedRes.razor"));

    // [CallerFilePath] is filled in at the call site, so the walk up has to start from this file.
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
