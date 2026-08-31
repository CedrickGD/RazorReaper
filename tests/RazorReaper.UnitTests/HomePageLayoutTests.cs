using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests;

public sealed class HomePageLayoutTests
{
    [Fact]
    public void HomePageDoesNotRenderTheUpdateWidget()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Home.razor"));

        Assert.DoesNotContain("content-card update-card", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Updates install themselves and restart the app.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@inject IAutoUpdateManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAutoUpdateStateChanged", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
    }
}
