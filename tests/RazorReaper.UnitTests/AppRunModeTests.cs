using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests;

public sealed class AppRunModeTests
{
    [Theory]
    [InlineData("--local-preview")]
    [InlineData("--LOCAL-PREVIEW")]
    public void ExactFlagAvailabilityMatchesCompilationMode(string argument)
    {
        var mode = new AppRunMode(["RazorReaper.exe", argument]);

#if DEBUG
        Assert.True(mode.IsLocalPreview);
#else
        Assert.False(mode.IsLocalPreview);
#endif
    }

    [Fact]
    public void AbsentFlagDoesNotEnablePreview()
    {
        var mode = new AppRunMode(["RazorReaper.exe"]);

        Assert.False(mode.IsLocalPreview);
    }

    [Theory]
    [InlineData("--local-preview=true")]
    [InlineData("prefix--local-preview")]
    [InlineData("--local-preview-suffix")]
    [InlineData("local-preview")]
    [InlineData(" --local-preview ")]
    public void NearMatchDoesNotEnablePreview(string argument)
    {
        var mode = new AppRunMode(["RazorReaper.exe", argument]);

        Assert.False(mode.IsLocalPreview);
    }
}
