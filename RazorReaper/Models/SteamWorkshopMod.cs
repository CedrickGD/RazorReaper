namespace RazorReaper.Models;

/// <summary>
/// Represents an installed ARK: Survival Evolved Steam Workshop mod.
/// </summary>
public sealed class SteamWorkshopMod
{
    /// <summary>
    /// Gets or sets the Steam Workshop published file ID.
    /// </summary>
    public string WorkshopId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display title. Falls back to a generic name if unknown.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Steam library root path that contains this installation.
    /// </summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the on-disk folder path for this mod content.
    /// </summary>
    public string ContentPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the mod appears to have been installed locally (UTC).
    /// </summary>
    public DateTime? InstalledAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the local filesystem last write time (UTC).
    /// </summary>
    public DateTime? LocalLastUpdatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets Steam-reported update time from metadata (UTC).
    /// </summary>
    public DateTime? SteamLastUpdatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets Steam-reported mod size in bytes when available.
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets an optional preview image URL reported by Steam metadata.
    /// </summary>
    public string? PreviewUrl { get; set; }

    /// <summary>
    /// Gets or sets whether the title/details came from Steam metadata.
    /// </summary>
    public bool HasSteamMetadata { get; set; }

    /// <summary>
    /// Gets the best available "updated" timestamp (Steam metadata preferred).
    /// </summary>
    public DateTime? EffectiveLastUpdatedUtc => SteamLastUpdatedAtUtc ?? LocalLastUpdatedAtUtc;
}
