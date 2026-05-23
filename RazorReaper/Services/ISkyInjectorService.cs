using RazorReaper.Models;

namespace RazorReaper.Services;

public interface ISkyInjectorService
{
    /// <summary>
    /// Discover all ARK sky-texture .uasset files we know how to patch and parse their headers.
    /// </summary>
    Task<IReadOnlyList<SkyTextureInfo>> DiscoverSkyTexturesAsync(CancellationToken ct = default);

    /// <summary>
    /// Patch every discovered sky texture with the user's image/color, backing up the originals
    /// on first inject so <see cref="RestoreAsync"/> can revert.
    /// </summary>
    Task<SkyInjectionResult> InjectAsync(SkyInjectionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Walk the backup folder and copy each .bak back over its original path.
    /// </summary>
    Task<SkyInjectionResult> RestoreAsync(CancellationToken ct = default);

    /// <summary>
    /// True if at least one backup file exists — drives the "Sky injected" status badge.
    /// </summary>
    bool HasBackup();

    /// <summary>
    /// Absolute path of the backup folder. Exposed so the Settings tab can open it / show its size.
    /// </summary>
    string BackupFolderPath { get; }

    /// <summary>
    /// Cumulative byte size and file count of all .bak files in the backup folder. Cheap synchronous
    /// call — the backup set caps at ~25 files at &lt;100MB total, so a full enumeration is fine.
    /// </summary>
    (long Bytes, int Files) GetBackupStats();

    /// <summary>
    /// Delete every .bak file in the backup folder. Returns the number of files removed. Does not
    /// touch the user's ARK install — Restore loses its source after this, so the UI should confirm.
    /// </summary>
    Task<int> ClearBackupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Open the backup folder in File Explorer. No-op if the folder doesn't exist yet.
    /// </summary>
    void OpenBackupFolder();
}
