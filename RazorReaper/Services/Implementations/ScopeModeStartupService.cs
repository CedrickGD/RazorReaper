using Microsoft.Extensions.Logging;
using RazorReaper.Utilities;

namespace RazorReaper.Services.Implementations;

public sealed class ScopeModeStartupService : IScopeModeStartupService
{
    private const string ScopeDisabledSuffix = ".rrscopeoff";

    private static readonly string[] ScopeFilePatterns =
    {
        "*scope*.uasset",
        "*scope*.uexp",
        "*scope*.ubulk"
    };

    private readonly ILogger<ScopeModeStartupService> _logger;

    public ScopeModeStartupService(ILogger<ScopeModeStartupService> logger)
    {
        _logger = logger;
    }

    public Task ApplySavedScopeModeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var scopeMode = Preferences.Get(ScopeModeConstants.ScopeModePreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(scopeMode))
            {
                return Task.CompletedTask;
            }

            var arkInstallPath = ResolveArkPath();
            if (string.IsNullOrEmpty(arkInstallPath))
            {
                _logger.LogDebug("Skipped startup scope apply: ARK path not found.");
                return Task.CompletedTask;
            }

            var scopeRootPath = ResolveScopeRootPath(arkInstallPath);
            if (string.IsNullOrEmpty(scopeRootPath))
            {
                _logger.LogDebug("Skipped startup scope apply: scope root path not found.");
                return Task.CompletedTask;
            }

            if (scopeMode.Equals(ScopeModeConstants.ScopeModeDisabled, StringComparison.OrdinalIgnoreCase))
            {
                var result = DisableScopeFiles(scopeRootPath, cancellationToken);
                _logger.LogInformation(
                    "Startup scope apply mode=disabled (renamed={Renamed}, skipped={Skipped}, failed={Failed})",
                    result.renamed,
                    result.skipped,
                    result.failed);
            }
            else if (scopeMode.Equals(ScopeModeConstants.ScopeModeEnabled, StringComparison.OrdinalIgnoreCase))
            {
                var result = RestoreScopeFiles(scopeRootPath, cancellationToken);
                _logger.LogInformation(
                    "Startup scope apply mode=enabled (restored={Restored}, skipped={Skipped}, failed={Failed})",
                    result.restored,
                    result.skipped,
                    result.failed);
            }
            else
            {
                _logger.LogDebug("Skipped startup scope apply: unknown mode '{Mode}'.", scopeMode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply saved scope mode at startup.");
        }

        return Task.CompletedTask;
    }

    private static string ResolveArkPath()
    {
        var customPath = Preferences.Get(ScopeModeConstants.CustomArkInstallPathPreferenceKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(customPath) && IsValidArkPath(customPath))
        {
            return customPath;
        }

        return ArkUtilities.FindArkPath() ?? string.Empty;
    }

    private static string ResolveScopeRootPath(string arkInstallPath)
    {
        var preferred = Path.Combine(arkInstallPath, "ShooterGame", "Content", "PrimalEarth");
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        return Directory.Exists(arkInstallPath) ? arkInstallPath : string.Empty;
    }

    private static bool IsValidArkPath(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var executablePath = Path.Combine(path, "ShooterGame", "Binaries", "Win64", "ShooterGame.exe");
        return File.Exists(executablePath);
    }

    private static (int renamed, int skipped, int failed) DisableScopeFiles(string rootPath, CancellationToken cancellationToken)
    {
        var renamed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var filePath in EnumerateActiveScopeFiles(rootPath))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var renamedPath = filePath + ScopeDisabledSuffix;
            try
            {
                if (File.Exists(renamedPath))
                {
                    skipped++;
                    continue;
                }

                File.Move(filePath, renamedPath);
                renamed++;
            }
            catch
            {
                failed++;
            }
        }

        return (renamed, skipped, failed);
    }

    private static (int restored, int skipped, int failed) RestoreScopeFiles(string rootPath, CancellationToken cancellationToken)
    {
        var restored = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var filePath in EnumerateDisabledScopeFiles(rootPath))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var originalPath = filePath.Substring(0, filePath.Length - ScopeDisabledSuffix.Length);
            try
            {
                if (File.Exists(originalPath))
                {
                    skipped++;
                    continue;
                }

                File.Move(filePath, originalPath);
                restored++;
            }
            catch
            {
                failed++;
            }
        }

        return (restored, skipped, failed);
    }

    private static IEnumerable<string> EnumerateActiveScopeFiles(string rootPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in ScopeFilePatterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var filePath in files)
            {
                if (filePath.EndsWith(ScopeDisabledSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(filePath))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDisabledScopeFiles(string rootPath)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(rootPath, $"*{ScopeDisabledSuffix}", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName.Contains("scope", StringComparison.OrdinalIgnoreCase))
            {
                yield return filePath;
            }
        }
    }
}
