using System.Globalization;
using System.Reflection;

namespace RazorReaper.Diagnostics;

/// <summary>
/// Canonical runtime version information for every outbound request and user-facing label.
/// This deliberately reads the app assembly instead of MAUI AppInfo: incremental MAUI builds
/// can retain an older generated package manifest after ApplicationDisplayVersion changes.
/// </summary>
internal static class AppVersionInfo
{
    internal const string ApplicationVersionMetadataKey = "RazorReaper.ApplicationVersion";

    private static readonly Assembly AppAssembly = typeof(AppVersionInfo).Assembly;

    public static Version Version { get; } =
        AppAssembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static string VersionString { get; } = FormatVersion(Version);

    public static string BuildString { get; } = ResolveBuildString();

    public static string UserAgent =>
        $"RazorReaper/{VersionString} (Windows NT 10.0; Win64; x64)";

    internal static string FormatVersion(Version version)
        => version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";

    private static string ResolveBuildString()
    {
        var configuredBuild = AppAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, ApplicationVersionMetadataKey, StringComparison.Ordinal))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(configuredBuild))
        {
            return configuredBuild;
        }

        return Math.Max(0, Version.Revision).ToString(CultureInfo.InvariantCulture);
    }
}
