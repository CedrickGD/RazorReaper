using System.Reflection;
using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests;

public sealed class DiscordPresenceServiceTests
{
    [Fact]
    public void DownloadButtonUsesStablePublicInstallerUrl()
    {
        var field = typeof(DiscordPresenceService).GetField(
            "DownloadUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal("https://dl.razorreaper.app/", field.GetRawConstantValue());
    }
}
