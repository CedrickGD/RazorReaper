using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace RazorReaper.Utilities
{
    public static class ArkUtilities
    {
        public static string? FindArkPath()
        {
            string? steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;

            if (steamPath == null) return null;

            var possiblePaths = new List<string> { Path.Combine(steamPath, "steamapps", "common", "ARK") };

            string libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFoldersPath))
            {
                try
                {
                    string vdfContent = File.ReadAllText(libraryFoldersPath);
                    var matches = Regex.Matches(vdfContent, @"""path""\s*""([^""]+)""");
                    foreach (Match match in matches)
                    {
                        string libraryPath = match.Groups[1].Value.Replace(@"\\", @"\");
                        possiblePaths.Add(Path.Combine(libraryPath, "steamapps", "common", "ARK"));
                    }
                }
                catch (Exception ex)
                {
                    // If VDF parsing fails, continue with default Steam path only
                    // Common exceptions: IOException, UnauthorizedAccessException, RegexMatchTimeoutException
                    System.Diagnostics.Debug.WriteLine($"Failed to parse Steam library folders VDF: {ex.Message}");
                }
            }

            foreach (string arkPath in possiblePaths)
            {
                string arkExePath = Path.Combine(arkPath, "ShooterGame", "Binaries", "Win64", "ShooterGame.exe");
                if (Directory.Exists(arkPath) && File.Exists(arkExePath))
                {
                    return arkPath;
                }
            }

            // ARK installation not found in any of the possible paths
            return null;
        }

        public static string? GetBaseDeviceProfilesPath()
        {
            var arkPath = FindArkPath();
            if (arkPath == null) return null;
            return Path.Combine(arkPath, "Engine", "Config", "BaseDeviceProfiles.ini");
        }
    }
}
