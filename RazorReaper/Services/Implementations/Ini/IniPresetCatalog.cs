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
    /// <remarks>
    /// A preset's name is its identity as well as its label — it addresses the image override,
    /// the last-applied preference and the zip entry, and it is what the community calls the
    /// preset — so it stays as it is in every language, the way the automation scripts' names do.
    /// Only the line under it is translated.
    /// </remarks>
    public static List<IniPreset> BuildAll()
    {
        return new List<IniPreset>
        {
            new()
            {
                Name = "Default",
                DescriptionKey = "inichanger.preset.default.description",
                Content = LoadEmbeddedIni("default.ini")
            },
            new()
            {
                Name = "Super Hard",
                DescriptionKey = "inichanger.preset.super-hard.description",
                Content = LoadEmbeddedIni("super-hard.ini")
            },
            new()
            {
                Name = "Hard Black",
                DescriptionKey = "inichanger.preset.hard-black.description",
                Content = LoadEmbeddedIni("hard-black.ini")
            },
            new()
            {
                Name = "Hard Stalker",
                DescriptionKey = "inichanger.preset.hard-stalker.description",
                Content = LoadEmbeddedIni("hard-stalker.ini")
            },
            new()
            {
                Name = "Soft",
                DescriptionKey = "inichanger.preset.soft.description",
                Content = LoadEmbeddedIni("soft.ini")
            },
            new()
            {
                Name = "Black Spyglass",
                DescriptionKey = "inichanger.preset.black-spyglass.description",
                Content = LoadEmbeddedIni("black-spyglass.ini")
            },
            new()
            {
                Name = "Content Creator",
                DescriptionKey = "inichanger.preset.content-creator.description",
                Content = LoadEmbeddedIni("content-creator.ini")
            },
            new()
            {
                Name = "Stalker",
                DescriptionKey = "inichanger.preset.stalker.description",
                Content = LoadEmbeddedIni("stalker.ini")
            },
            new()
            {
                Name = "Black Semi Hard",
                DescriptionKey = "inichanger.preset.black-semi-hard.description",
                Content = LoadEmbeddedIni("black-semi-hard.ini")
            },
            new()
            {
                Name = "Hard",
                DescriptionKey = "inichanger.preset.hard.description",
                Content = LoadEmbeddedIni("hard.ini")
            },
            new()
            {
                Name = "Clear Water Snow North",
                DescriptionKey = "inichanger.preset.clear-water-snow-north.description",
                Content = LoadEmbeddedIni("clear-water-snow-north.ini")
            },
            new()
            {
                Name = "Semi Soft",
                DescriptionKey = "inichanger.preset.semi-soft.description",
                Content = LoadEmbeddedIni("semi-soft.ini")
            },
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
