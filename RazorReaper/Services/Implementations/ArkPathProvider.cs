using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using RazorReaper.Configuration;
using System.Text.RegularExpressions;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Implementation of IArkPathProvider for detecting ARK: Survival Evolved installation paths.
/// </summary>
public class ArkPathProvider : IArkPathProvider
{
    private readonly ILogger<ArkPathProvider> _logger;
    private readonly IFileSystemService _fileSystem;
    private readonly AppConfiguration _config;

    public ArkPathProvider(
        ILogger<ArkPathProvider> logger,
        IFileSystemService fileSystem,
        IOptions<AppConfiguration> config)
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _config = config.Value;
    }

    /// <inheritdoc/>
    public string? FindArkPath()
    {
        try
        {
            _logger.LogInformation("Attempting to find ARK installation path");

            string? steamPath = GetSteamInstallPath();
            if (steamPath == null)
            {
                _logger.LogWarning("Steam installation path not found in registry");
                return null;
            }

            _logger.LogDebug("Steam path found: {SteamPath}", steamPath);

            var possiblePaths = GetPossibleArkPaths(steamPath);

            foreach (string arkPath in possiblePaths)
            {
                if (IsValidArkPath(arkPath))
                {
                    _logger.LogInformation("ARK installation found at: {ArkPath}", arkPath);
                    return arkPath;
                }
            }

            _logger.LogWarning("No valid ARK installation found. Returning default path.");
            return Path.Combine(steamPath, "steamapps", "common", "ARK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while searching for ARK installation");
            return null;
        }
    }

    /// <inheritdoc/>
    public string? GetBaseDeviceProfilesPath()
    {
        try
        {
            var arkPath = FindArkPath();
            if (arkPath == null)
            {
                _logger.LogWarning("Cannot get BaseDeviceProfiles path - ARK installation not found");
                return null;
            }

            string configPath = _fileSystem.Combine(arkPath, _config.Ark.ConfigRelativePath);
            _logger.LogDebug("BaseDeviceProfiles path: {ConfigPath}", configPath);
            return configPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting BaseDeviceProfiles path");
            return null;
        }
    }

    /// <inheritdoc/>
    public bool IsValidArkPath(string path)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(path))
            {
                return false;
            }

            string arkExePath = _fileSystem.Combine(path, _config.Ark.ExecutableRelativePath);
            bool isValid = _fileSystem.FileExists(arkExePath);

            if (!isValid)
            {
                _logger.LogDebug("Path {Path} is not a valid ARK installation - executable not found", path);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating ARK path: {Path}", path);
            return false;
        }
    }

    private string? GetSteamInstallPath()
    {
        try
        {
            // Try 64-bit registry first
            string? steamPath = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                "InstallPath",
                null) as string;

            // Fall back to 32-bit registry
            if (steamPath == null)
            {
                steamPath = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                    "InstallPath",
                    null) as string;
            }

            return steamPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Steam installation path from registry");
            return null;
        }
    }

    private List<string> GetPossibleArkPaths(string steamPath)
    {
        var possiblePaths = new List<string>
        {
            Path.Combine(steamPath, "steamapps", "common", "ARK")
        };

        string libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (_fileSystem.FileExists(libraryFoldersPath))
        {
            try
            {
                // libraryfolders.vdf is a small text file (~few KB). A synchronous read avoids
                // sync-over-async deadlock risk in the surrounding synchronous API.
                var vdfContent = File.ReadAllText(libraryFoldersPath);
                var matches = Regex.Matches(vdfContent, @"""path""\s*""([^""]+)""");

                _logger.LogDebug("Found {Count} additional Steam library folders", matches.Count);

                foreach (Match match in matches)
                {
                    string libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");
                    possiblePaths.Add(Path.Combine(libraryPath, "steamapps", "common", "ARK"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse libraryfolders.vdf - will only check default Steam path");
            }
        }

        return possiblePaths;
    }
}
