using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Discovers where Steam is installed locally and which library folders it knows about. Reads
/// the Windows registry for the install path and parses <c>libraryfolders.vdf</c> for any
/// additional libraries. Pure helpers — the orchestrating service owns the logger and accumulates
/// any warnings the caller wants to surface to the user.
/// </summary>
internal static class SteamPathLocator
{
    private static readonly Regex LibraryPathRegex =
        new(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyLibraryPathRegex =
        new(@"^\s*""\d+""\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Returns the Steam install path from the Windows registry, or null when Steam is
    /// not installed (or we're not on Windows).</summary>
    public static string? GetSteamInstallPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var steamPath =
                ReadRegistryString(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") ??
                ReadRegistryString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath") ??
                ReadRegistryString(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath");

            return string.IsNullOrWhiteSpace(steamPath) ? null : NormalizePath(steamPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Enumerate every Steam library folder reachable from <paramref name="steamPath"/>.
    /// The Steam install itself is always included; additional libraries come from
    /// <c>steamapps/libraryfolders.vdf</c>. Returns existing directories only, ordered for
    /// deterministic scanning.</summary>
    public static async Task<List<string>> GetLibraryPathsAsync(
        string steamPath,
        ILogger logger,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            steamPath
        };

        var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            warnings.Add("Steam libraryfolders.vdf was not found. Only the default Steam library was scanned.");
            return libraries.ToList();
        }

        try
        {
            var content = await File.ReadAllTextAsync(libraryFoldersPath, cancellationToken);

            foreach (Match match in LibraryPathRegex.Matches(content))
            {
                var path = NormalizePath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    libraries.Add(path);
                }
            }

            foreach (Match match in LegacyLibraryPathRegex.Matches(content))
            {
                var path = NormalizePath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    libraries.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse libraryfolders.vdf at {Path}", libraryFoldersPath);
            warnings.Add("Failed to parse Steam libraryfolders.vdf. Only default library paths may be shown.");
        }

        return libraries
            .Where(Directory.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Collapse the various forms VDF paths can take (escaped backslashes, forward
    /// slashes from cross-platform tooling) into a canonical absolute path.</summary>
    public static string NormalizePath(string value)
    {
        var normalized = value
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim();

        try
        {
            return Path.GetFullPath(normalized);
        }
        catch
        {
            return normalized;
        }
    }

    private static string? ReadRegistryString(string keyName, string valueName)
        => Registry.GetValue(keyName, valueName, null) as string;
}
