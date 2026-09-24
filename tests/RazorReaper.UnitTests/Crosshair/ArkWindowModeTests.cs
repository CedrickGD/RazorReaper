using RazorReaper.Services.Implementations;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// What the crosshair page reads to decide whether to warn about ARK Fullscreen: the game's own
/// FullscreenMode, and the per-program "Disable fullscreen optimizations" flag.
/// </summary>
public class ArkWindowModeTests
{
    private const string Ark = @"C:\Steam\steamapps\common\ARK\ShooterGame\Binaries\Win64\ShooterGame.exe";

    [Theory]
    [InlineData("FullscreenMode=0", 0)]
    [InlineData("FullscreenMode=1", 1)]
    [InlineData("FullscreenMode=2", 2)]
    [InlineData("ResolutionSizeX=1920", null)]
    [InlineData("FullscreenMode=nonsense", null)]
    [InlineData("FullscreenMode=3", null)]
    [InlineData("FullscreenMode=-1", null)]
    public void FullscreenModeIsReadFromTheGamesOwnConfig(string line, int? expected)
    {
        // LastConfirmedFullscreenMode sits above it in a real file and must not be read instead.
        var path = Path.Combine(Path.GetTempPath(), "rr-fsmode-" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(path,
            "[/Script/ShooterGame.ShooterGameUserSettings]\r\nLastConfirmedFullscreenMode=1\r\n" + line + "\r\n");
        try
        {
            Assert.Equal(expected, ArkWindowMode.ReadFullscreenMode(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoConfigMeansNoAnswer()
    {
        Assert.Null(ArkWindowMode.ReadFullscreenMode(null));
        Assert.Null(ArkWindowMode.ReadFullscreenMode(Path.Combine(Path.GetTempPath(), "no-ark-here", "GameUserSettings.ini")));
    }

    [Theory]
    [InlineData(Ark, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", true)]
    [InlineData(Ark, "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", true)]
    [InlineData(Ark, "~ disabledxmaximizedwindowedmode", true)]
    [InlineData(Ark, "~ HIGHDPIAWARE", false)]
    [InlineData(Ark, null, false)]
    [InlineData(@"C:\Games\Other.exe", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", false)]
    [InlineData(@"C:\ARK\ShooterGame\Binaries\Win64\ShooterGameServer.exe", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", false)]
    public void OnlyTheFlagOnShooterGameCounts(string program, string? layers, bool expected)
        => Assert.Equal(expected, ArkWindowMode.DisablesFullscreenOptimizations(program, layers));
}
