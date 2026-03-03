using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Service for discovering installed ARK Steam Workshop mods.
/// </summary>
public interface ISteamWorkshopService
{
    /// <summary>
    /// Scans Steam libraries for installed ARK workshop mods and metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A scan result with discovered mods and warnings.</returns>
    Task<SteamWorkshopScanResult> ScanInstalledArkModsAsync(CancellationToken cancellationToken = default);
}
