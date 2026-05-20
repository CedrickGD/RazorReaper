using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Service for managing ARK INI configuration presets.
/// </summary>
public interface IIniPresetService
{
    /// <summary>
    /// Gets all available INI presets.
    /// </summary>
    /// <returns>A list of all available INI presets.</returns>
    List<IniPreset> GetAllPresets();

    /// <summary>
    /// Gets a specific preset by name.
    /// </summary>
    /// <param name="name">The preset name.</param>
    /// <returns>The matching preset, or null if not found.</returns>
    IniPreset? GetPresetByName(string name);

    /// <summary>
    /// Gets the image path for a preset.
    /// </summary>
    /// <param name="presetName">The preset name.</param>
    /// <returns>The relative path to the preset's image.</returns>
    string GetPresetImagePath(string presetName);

    /// <summary>
    /// Adds a custom preset and persists it for future sessions.
    /// </summary>
    /// <param name="preset">The preset to add.</param>
    /// <returns>True if the preset was saved successfully; otherwise, false.</returns>
    Task<bool> AddCustomPresetAsync(IniPreset preset);

    /// <summary>
    /// Removes a custom preset and updates persisted storage.
    /// </summary>
    /// <param name="name">The preset name.</param>
    /// <returns>True if the preset was removed; otherwise, false.</returns>
    Task<bool> RemoveCustomPresetAsync(string name);

    /// <summary>
    /// Replaces the preview image for a preset with a user-provided image. Stored in
    /// %LOCALAPPDATA%\RazorReaper\PresetImages so it survives app updates.
    /// </summary>
    /// <param name="presetName">The preset name.</param>
    /// <param name="sourceStream">Read-only stream containing the image bytes.</param>
    /// <param name="extension">File extension including the leading dot (e.g. ".jpg", ".png", ".webp").</param>
    /// <returns>True if the override was saved; otherwise, false.</returns>
    Task<bool> SetPresetImageAsync(string presetName, Stream sourceStream, string extension);

    /// <summary>
    /// Removes the user-provided preview image override for a preset so it reverts to the bundled one.
    /// </summary>
    /// <param name="presetName">The preset name.</param>
    /// <returns>True if an override existed and was removed; otherwise, false.</returns>
    bool ResetPresetImage(string presetName);

    /// <summary>
    /// Returns true when the preset currently uses a user-uploaded image override.
    /// </summary>
    /// <param name="presetName">The preset name.</param>
    bool HasCustomImage(string presetName);

    /// <summary>
    /// Returns the name of the most-recently applied preset, or null if none has been remembered
    /// (first run, or persistence file missing/unreadable). Used by the editor to restore the
    /// hero preview between sessions.
    /// </summary>
    string? GetLastAppliedPresetName();

    /// <summary>
    /// Records the name of a preset that was just applied to the live INI. Persisted to
    /// LocalAppData so the next session can restore it.
    /// </summary>
    /// <param name="presetName">The preset name. Empty or whitespace clears the record.</param>
    void SetLastAppliedPresetName(string presetName);
}
