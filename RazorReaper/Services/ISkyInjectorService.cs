using RazorReaper.Models;

namespace RazorReaper.Services;

public interface ISkyInjectorService
{
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
}
