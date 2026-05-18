using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Orchestrates the ARK workshop scan: find Steam → enumerate library folders → walk each
/// library's workshop content folder → fuse local-filesystem timestamps with parsed ACF data
/// and Steam Web API enrichment. The heavy lifting lives in three single-purpose helpers
/// (path locator, ACF parser, web API client); this class just composes them.
/// </summary>
public sealed class SteamWorkshopService : ISteamWorkshopService
{
    private const string ArkAppId = "346110";

    private readonly ILogger<SteamWorkshopService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SteamWorkshopService(
        ILogger<SteamWorkshopService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public async Task<SteamWorkshopScanResult> ScanInstalledArkModsAsync(CancellationToken cancellationToken = default)
    {
        var result = new SteamWorkshopScanResult();

        try
        {
            var steamPath = SteamPathLocator.GetSteamInstallPath();
            result.SteamPath = steamPath;

            if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            {
                result.Warnings.Add("Steam install path was not found in Windows registry.");
                return result;
            }

            result.SteamDetected = true;
            var libraries = await SteamPathLocator.GetLibraryPathsAsync(steamPath, _logger, result.Warnings, cancellationToken);
            result.LibrariesScanned.AddRange(libraries);

            var modsById = new Dictionary<string, SteamWorkshopMod>(StringComparer.Ordinal);

            foreach (var libraryPath in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ScanLibraryAsync(libraryPath, modsById, result.Warnings, cancellationToken);
            }

            var mods = modsById.Values.ToList();

            var apiClient = new SteamWorkshopApiClient(_logger, _httpClientFactory);
            result.MetadataResolvedCount = await apiClient.EnrichMissingMetadataAsync(mods, cancellationToken);

            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.Title))
                {
                    mod.Title = $"Workshop Item {mod.WorkshopId}";
                }
            }

            result.Mods = mods
                .OrderByDescending(m => m.EffectiveLastUpdatedUtc ?? DateTime.MinValue)
                .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while scanning installed ARK workshop mods.");
            result.Warnings.Add("Unexpected error while scanning Steam Workshop mods. Check logs for details.");
        }
        finally
        {
            result.ScannedAtUtc = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>Scan a single Steam library: read its ACF metadata then walk each numbered
    /// workshop folder, merging the two sources into the shared <paramref name="modsById"/> map.</summary>
    private async Task ScanLibraryAsync(
        string libraryPath,
        Dictionary<string, SteamWorkshopMod> modsById,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var acfMetadata = await ReadAppWorkshopMetadataAsync(libraryPath, warnings, cancellationToken);
        var workshopPath = Path.Combine(libraryPath, "steamapps", "workshop", "content", ArkAppId);
        if (!Directory.Exists(workshopPath))
        {
            return;
        }

        IEnumerable<string> modDirectories;
        try
        {
            modDirectories = Directory.EnumerateDirectories(workshopPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate workshop content directory: {WorkshopPath}", workshopPath);
            warnings.Add($"Could not read workshop folder: {workshopPath}");
            return;
        }

        foreach (var modDirectory in modDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workshopId = Path.GetFileName(modDirectory);
            if (!IsNumeric(workshopId))
            {
                continue;
            }

            if (!modsById.TryGetValue(workshopId, out var mod))
            {
                mod = new SteamWorkshopMod
                {
                    WorkshopId = workshopId
                };
                modsById[workshopId] = mod;
            }

            ApplyLocalMetadata(mod, libraryPath, modDirectory);

            if (acfMetadata.TryGetValue(workshopId, out var itemMetadata))
            {
                ApplyAcfMetadata(mod, itemMetadata);
            }
        }
    }

    private async Task<Dictionary<string, AppWorkshopAcfParser.WorkshopAcfMetadata>> ReadAppWorkshopMetadataAsync(
        string libraryPath,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var acfPath = Path.Combine(libraryPath, "steamapps", "workshop", $"appworkshop_{ArkAppId}.acf");
        if (!File.Exists(acfPath))
        {
            return new Dictionary<string, AppWorkshopAcfParser.WorkshopAcfMetadata>(StringComparer.Ordinal);
        }

        try
        {
            var content = await File.ReadAllTextAsync(acfPath, cancellationToken);
            return AppWorkshopAcfParser.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading ACF metadata: {AcfPath}", acfPath);
            warnings.Add($"Could not read {Path.GetFileName(acfPath)} in {libraryPath}.");
            return new Dictionary<string, AppWorkshopAcfParser.WorkshopAcfMetadata>(StringComparer.Ordinal);
        }
    }

    private static void ApplyLocalMetadata(SteamWorkshopMod mod, string libraryPath, string contentPath)
    {
        DateTime? localLastUpdated = null;
        DateTime? localInstalledAt = null;

        try
        {
            localLastUpdated = Directory.GetLastWriteTimeUtc(contentPath);
        }
        catch
        {
        }

        try
        {
            localInstalledAt = Directory.GetCreationTimeUtc(contentPath);
        }
        catch
        {
        }

        var shouldReplacePath = !mod.LocalLastUpdatedAtUtc.HasValue ||
                                (localLastUpdated.HasValue && localLastUpdated.Value > mod.LocalLastUpdatedAtUtc.Value);

        if (shouldReplacePath)
        {
            mod.LibraryPath = libraryPath;
            mod.ContentPath = contentPath;
        }

        mod.LocalLastUpdatedAtUtc = PickLatest(mod.LocalLastUpdatedAtUtc, localLastUpdated);
        mod.InstalledAtUtc = PickLatest(mod.InstalledAtUtc, localInstalledAt);
    }

    private static void ApplyAcfMetadata(SteamWorkshopMod mod, AppWorkshopAcfParser.WorkshopAcfMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            mod.Title = metadata.Title;
            mod.HasSteamMetadata = true;
        }

        if (metadata.SizeBytes.HasValue && metadata.SizeBytes > 0)
        {
            mod.SizeBytes = metadata.SizeBytes.Value;
        }

        mod.SteamLastUpdatedAtUtc = PickLatest(mod.SteamLastUpdatedAtUtc, metadata.TimeUpdatedUtc);
        mod.InstalledAtUtc = PickLatest(mod.InstalledAtUtc, metadata.TimeTouchedUtc);
    }

    private static DateTime? PickLatest(DateTime? current, DateTime? candidate)
    {
        if (!candidate.HasValue)
        {
            return current;
        }

        if (!current.HasValue || candidate.Value > current.Value)
        {
            return candidate;
        }

        return current;
    }

    private static bool IsNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
