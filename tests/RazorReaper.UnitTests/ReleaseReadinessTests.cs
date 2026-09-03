using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests;

public sealed class ReleaseReadinessTests
{
    [Fact]
    public void VersionMetadataIsAlignedFor150()
    {
        var root = RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "RazorReaper", "RazorReaper.csproj"));
        var installer = File.ReadAllText(Path.Combine(root, "installer", "RazorReaper.iss"));
        var manifest = File.ReadAllText(Path.Combine(root, "update.xml"));

        Assert.Contains("<ApplicationDisplayVersion>1.5.0</ApplicationDisplayVersion>", project, StringComparison.Ordinal);
        Assert.Contains("<ApplicationVersion>14</ApplicationVersion>", project, StringComparison.Ordinal);
        Assert.Contains("#define MyAppVersion \"1.5.0\"", installer, StringComparison.Ordinal);
        Assert.Contains("<version>1.5.0.0</version>", manifest, StringComparison.Ordinal);
        Assert.Contains("/releases/download/v1.5.0/RazorReaper-Setup.exe", manifest, StringComparison.Ordinal);
        Assert.Contains("/releases/tag/v1.5.0", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("1.4.10", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void DesyncRemovalFailureRemainsTheFinalReportedOutcome()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "RazorReaper",
            "Services",
            "Desync",
            "DesyncService.cs"));

        var deleteCall = source.IndexOf("var del = await RunNetshAsync", StringComparison.Ordinal);
        var restoredNotice = source.IndexOf("Desync reverted — traffic restored.", deleteCall, StringComparison.Ordinal);
        Assert.True(deleteCall >= 0);
        Assert.True(restoredNotice > deleteCall);

        var failureBranch = source[deleteCall..restoredNotice];
        Assert.Contains("Desync failed: firewall rule removal", failureBranch, StringComparison.Ordinal);
        Assert.Contains("traffic may still be blocked", failureBranch, StringComparison.Ordinal);
        Assert.Contains("return;", failureBranch, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
