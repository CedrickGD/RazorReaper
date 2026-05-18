namespace RazorReaper.Services.Implementations;

/// <summary>
/// Describes one font preset the installer can fetch and register: where to download it from,
/// what to expect inside the bundle (file-name prefixes), and what family names should show up
/// in the font collection once installed (used to detect "already installed").
/// </summary>
internal sealed class FontPackageDefinition
{
    public string Id { get; }
    public string DisplayName { get; }
    public string PackageFileName { get; }
    public string DownloadUrl { get; }
    public IReadOnlyList<string> FamilyNames { get; }
    public IReadOnlyList<string> FilePrefixes { get; }

    public FontPackageDefinition(
        string id,
        string displayName,
        string packageFileName,
        string downloadUrl,
        IReadOnlyList<string> familyNames,
        IReadOnlyList<string> filePrefixes)
    {
        Id = id;
        DisplayName = displayName;
        PackageFileName = packageFileName;
        DownloadUrl = downloadUrl;
        FamilyNames = familyNames;
        FilePrefixes = filePrefixes;
    }

    /// <summary>Static catalog of every preset the installer knows about. New presets are added
    /// here; everything else (lookup by id, prefix matching) goes through this list.</summary>
    public static readonly IReadOnlyList<FontPackageDefinition> All = new[]
    {
        new FontPackageDefinition(
            "jetbrains-mono-nerd",
            "JetBrains Mono Nerd Font",
            "JetBrainsMono.zip",
            "https://github.com/ryanoasis/nerd-fonts/releases/latest/download/JetBrainsMono.zip",
            new[]
            {
                "JetBrainsMono Nerd Font",
                "JetBrainsMono Nerd Font Mono",
                "JetBrainsMono Nerd Font Propo"
            },
            new[]
            {
                "JetBrainsMonoNerdFont",
                "JetBrainsMonoNerdFontMono",
                "JetBrainsMonoNerdFontPropo"
            }),
        new FontPackageDefinition(
            "space-grotesk",
            "Space Grotesk",
            "SpaceGrotesk[wght].ttf",
            "https://github.com/google/fonts/raw/main/ofl/spacegrotesk/SpaceGrotesk%5Bwght%5D.ttf",
            new[] { "Space Grotesk" },
            new[] { "SpaceGrotesk" }),
        new FontPackageDefinition(
            "manrope",
            "Manrope",
            "Manrope[wght].ttf",
            "https://github.com/google/fonts/raw/main/ofl/manrope/Manrope%5Bwght%5D.ttf",
            new[] { "Manrope" },
            new[] { "Manrope" }),
        new FontPackageDefinition(
            "fira-code-nerd",
            "Fira Code Nerd Font",
            "FiraCode.zip",
            "https://github.com/ryanoasis/nerd-fonts/releases/latest/download/FiraCode.zip",
            new[]
            {
                "FiraCode Nerd Font",
                "FiraCode Nerd Font Mono",
                "FiraCode Nerd Font Propo"
            },
            new[]
            {
                "FiraCodeNerdFont",
                "FiraCodeNerdFontMono",
                "FiraCodeNerdFontPropo"
            })
    };

    public static FontPackageDefinition? FindById(string presetId)
    {
        return All.FirstOrDefault(preset =>
            string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));
    }

    public bool MatchesFilePrefix(string fileName)
    {
        foreach (var prefix in FilePrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
