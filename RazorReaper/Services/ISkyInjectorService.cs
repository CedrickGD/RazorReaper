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
}
