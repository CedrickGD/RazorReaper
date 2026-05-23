namespace RazorReaper.Models;

public enum SkyInjectionMode
{
    Image,
    SolidColor
}

public class SkyInjectionOptions
{
    public SkyInjectionMode Mode { get; set; } = SkyInjectionMode.Image;
    public string? ImagePath { get; set; }
    public string HexColor { get; set; } = "#4488cc";
    public bool FlipVertically { get; set; }
    public int TileSize { get; set; } = 1;
}

public class SkyTextureInfo
{
    public required string Path { get; init; }
    public required SkyTextureKind Kind { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int DataOffset { get; init; }
    public required int DataSize { get; init; }
}

public record SkyInjectionResult(
    int Patched,
    int Skipped,
    IReadOnlyList<string> Errors);
