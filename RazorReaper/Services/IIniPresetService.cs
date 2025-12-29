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
}
