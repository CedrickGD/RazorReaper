using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests.Services;

/// <summary>
/// StoreLinks.cs's own doc comment names the failure this guards against: "Buy Premium" and
/// "Buy / renew" are the same shop, so the address is written once, and a second copy is the one
/// that goes stale when the shop moves — exactly what DiscordPresenceService's own ShopUrl
/// constant was. This scans every shipped .cs file for the literal host and fails the moment a
/// second copy appears anywhere but StoreLinks.cs itself.
/// </summary>
public sealed class StoreLinksTests
{
    private const string StoreHost = "rr.sellhub.cx";

    [Fact]
    public void TheStoreHostLiteralAppearsInExactlyOneSourceFileOutsideTests()
    {
        var appRoot = Path.Combine(RepositoryRoot(), "RazorReaper");

        var hits = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => Path.GetRelativePath(appRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relativePath => File.ReadAllText(Path.Combine(appRoot, relativePath)).Contains(StoreHost, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Services/StoreLinks.cs"], hits);
    }

    private static bool IsBuildOutput(string path)
        => path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
