namespace RazorReaper.Services;

/// <summary>
/// Ensures the app UI font is available on the current machine.
/// </summary>
public interface IFontInstaller
{
    /// <summary>
    /// Ensures all bundled font presets are installed for the current user if missing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task EnsurePresetFontsInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a specific font preset is installed for the current user if missing.
    /// </summary>
    /// <param name="presetId">The font preset identifier.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task EnsureFontInstalledAsync(string presetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a specific font preset is already installed.
    /// </summary>
    /// <param name="presetId">The font preset identifier.</param>
    bool IsFontInstalled(string presetId);
}
