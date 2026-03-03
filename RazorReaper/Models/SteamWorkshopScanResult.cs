namespace RazorReaper.Models;

/// <summary>
/// Result payload for an ARK Steam Workshop scan.
/// </summary>
public sealed class SteamWorkshopScanResult
{
    /// <summary>
    /// Gets or sets whether a Steam install path was detected.
    /// </summary>
    public bool SteamDetected { get; set; }

    /// <summary>
    /// Gets or sets the detected Steam install path, if any.
    /// </summary>
    public string? SteamPath { get; set; }

    /// <summary>
    /// Gets or sets the Steam library roots scanned.
    /// </summary>
    public List<string> LibrariesScanned { get; set; } = new();

    /// <summary>
    /// Gets or sets the installed mods that were discovered.
    /// </summary>
    public List<SteamWorkshopMod> Mods { get; set; } = new();

    /// <summary>
    /// Gets or sets scan warnings (missing files, parse issues, etc.).
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Gets or sets when the scan finished in UTC.
    /// </summary>
    public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets how many mods were enriched from Steam's API.
    /// </summary>
    public int MetadataResolvedCount { get; set; }
}
