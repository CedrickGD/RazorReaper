using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Static catalog of the built-in INI presets shipped with the app. Each entry is materialised
/// from an embedded resource under <c>Resources/Presets/</c> (see <c>RazorReaper.csproj</c>'s
/// <c>EmbeddedResource</c> block). To add or edit a preset, drop the .ini in
/// <c>Resources/Presets/</c> and add an entry to <see cref="BuildAll"/>.
/// </summary>
internal static class IniPresetCatalog
{
    public static List<IniPreset> BuildAll()
    {
        return new List<IniPreset>
        {
            BuildPreset("Default",           "default.ini",                "Game default."),
            BuildPreset("Super Hard",        "super-hard.ini",             "Max FPS, minimum visuals."),
            BuildPreset("Hard Black",        "hard-black.ini",             "Dark theme, perf-tuned."),
            BuildPreset("Hard Stalker",      "hard-stalker.ini",           "Long-range PvP visibility."),
            BuildPreset("Soft",              "soft.ini",                   "Balanced look and FPS."),
            BuildPreset("Black Spyglass",    "black-spyglass.ini",         "Dark with Spyglass tweaks."),
            BuildPreset("Contenant Creator", "contenant-creator.ini",      "Content creator tuning."),
            BuildPreset("Stalker",           "stalker.ini",                "Player/dino spotting."),
            BuildPreset("Black",             "black.ini",                  "Black tinted scene."),
            BuildPreset("Hard",              "hard.ini",                   "Raid-grade FPS."),
            BuildPreset("Clear Water Snow North", "clear-water-snow-north.ini", "Snow biome with clear water."),
            BuildPreset("Very Soft",         "very-soft.ini",              "Soft visuals, gentle FPS bump."),
        };
    }

    private static IniPreset BuildPreset(string name, string fileName, string description)
    {
        return new IniPreset
        {
            Name = name,
            Description = description,
            Content = LoadEmbeddedIni(fileName)
        };
    }

    private static string LoadEmbeddedIni(string fileName)
    {
        var asm = typeof(IniPresetCatalog).Assembly;
        var resourceName = "RazorReaper.Presets." + fileName;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Defensive fallback so a missing resource doesn't crash the whole service.
            return $"; ERROR: preset resource '{resourceName}' missing from the assembly.";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
