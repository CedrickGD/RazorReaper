using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests;

public sealed class DiscordReleaseWorkflowTests
{
    [Fact]
    public void Version1410ReleaseSkipsTheDiscordAnnouncement()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            ".github",
            "workflows",
            "discord-release.yml"));
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "  notify:\n    if: ${{ github.event_name != 'release' || github.event.release.tag_name != 'v1.4.10' }}\n    runs-on: ubuntu-latest",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains("      - published", normalized, StringComparison.Ordinal);
        Assert.Contains("      - prereleased", normalized, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
    }
}
