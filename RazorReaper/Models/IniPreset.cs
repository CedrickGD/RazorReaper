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
    /// Gets or sets the preset description.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Gets or sets the full INI file content for this preset.
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// Gets or sets whether this preset was created by the user.
    /// </summary>
    public bool IsCustom { get; set; }
}
