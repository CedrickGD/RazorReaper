namespace RazorReaper.Models;

/// <summary>
/// Represents an INI configuration preset for ARK BaseDeviceProfiles.ini file.
/// </summary>
public class IniPreset
{
    /// <summary>
    /// Gets or sets the preset name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the preset description as its author typed it. Custom presets carry their
    /// own words here; a built-in leaves this empty and names itself through
    /// <see cref="DescriptionKey"/> instead.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Gets or sets the dictionary key a built-in preset is described by, so its line follows a
    /// language switch. Empty for a custom preset, whose description is the user's own text.
    /// </summary>
    public string DescriptionKey { get; set; } = "";

    /// <summary>
    /// Gets or sets the full INI file content for this preset.
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// Gets or sets whether this preset was created by the user.
    /// </summary>
    public bool IsCustom { get; set; }
}
