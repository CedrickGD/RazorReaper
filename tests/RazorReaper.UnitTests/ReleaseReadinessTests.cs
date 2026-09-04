using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using RazorReaper.Diagnostics;

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
    public void CompiledVersionMetadataIsAlignedFor150()
    {
        var assembly = typeof(AppVersionInfo).Assembly;
        var mauiVersion = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute =>
                string.Equals(
                    attribute.Key,
                    "Microsoft.Maui.ApplicationModel.AppInfo.Version",
                    StringComparison.Ordinal))
            .Value;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;

        Assert.Equal("1.5.0.0", assembly.GetName().Version?.ToString());
        Assert.Equal("1.5.0.0", fileVersion);
        Assert.Equal("1.5.0", informationalVersion);
        Assert.Equal("1.5.0", AppVersionInfo.VersionString);
        Assert.Equal("14", AppVersionInfo.BuildString);
        Assert.Equal("1.5.0.14", mauiVersion);
        Assert.StartsWith("RazorReaper/1.5.0 ", AppVersionInfo.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCodeDoesNotReadVersionFromMauiAppInfo()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "RazorReaper");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("AppInfo.Current.Version", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
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
