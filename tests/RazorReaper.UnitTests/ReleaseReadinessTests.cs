using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using RazorReaper.Diagnostics;

namespace RazorReaper.UnitTests;

public sealed class ReleaseReadinessTests
{
    [Fact]
    public void InstallerMatchesTheBuildAndPublishedManifestIsConsistent()
    {
        var root = RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "RazorReaper", "RazorReaper.csproj"));
        var installer = File.ReadAllText(Path.Combine(root, "installer", "RazorReaper.iss"));
        var manifest = File.ReadAllText(Path.Combine(root, "update.xml"));

        var displayVersion = XDocument.Parse(project).Descendants("ApplicationDisplayVersion").Single().Value;
        Assert.Contains($"#define MyAppVersion \"{displayVersion}\"", installer, StringComparison.Ordinal);
        // The updater stays on the published release until its installer is available.
        var published = Version.Parse(XDocument.Parse(manifest).Descendants("version").Single().Value);
        Assert.True(published <= Version.Parse(displayVersion + ".0"));
        var tag = $"v{published.Major}.{published.Minor}.{published.Build}";
        Assert.Contains($"/releases/download/{tag}/RazorReaper-Setup.exe", manifest, StringComparison.Ordinal);
        Assert.Contains($"/releases/tag/{tag}", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledVersionMetadataMatchesTheProject()
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
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "RazorReaper", "RazorReaper.csproj"));
        var version = project.Descendants("ApplicationDisplayVersion").Single().Value;
        var build = project.Descendants("ApplicationVersion").Single().Value;

        Assert.Equal(version + ".0", assembly.GetName().Version?.ToString());
        Assert.Equal(version + ".0", fileVersion);
        Assert.Equal(version, informationalVersion);
        Assert.Equal(version, AppVersionInfo.VersionString);
        Assert.Equal(build, AppVersionInfo.BuildString);
        Assert.Equal(version + "." + build, mauiVersion);
        Assert.StartsWith($"RazorReaper/{version} ", AppVersionInfo.UserAgent, StringComparison.Ordinal);
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
