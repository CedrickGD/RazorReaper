namespace RazorReaper.Models;

public enum CrosshairType
{
    Cross,
    Dot,
    Circle,
    TStyle,
    Image,
    Pixel
}

public enum CrosshairAnimation
{
    None,
    Pulse,
    Breath,
    Rotate
}

/// <summary>
/// Full description of a crosshair: rendering shape, colors, animation, and where on which monitor it sits.
/// Profiles are JSON-persisted; renderer and editor share this shape, so changes flow directly to the overlay.
/// </summary>
public class CrosshairProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled";
    public bool IsBuiltIn { get; set; }

    public CrosshairType Type { get; set; } = CrosshairType.Cross;

    // #RRGGBB
    public string Color { get; set; } = "#00FF66";
    public string OutlineColor { get; set; } = "#000000";
    public int OutlineThickness { get; set; } = 1;

    public int Size { get; set; } = 16;
    public int Thickness { get; set; } = 2;
    public int Gap { get; set; } = 4;
    public int Opacity { get; set; } = 100;   // 0..100
    public int Rotation { get; set; }         // degrees

    public bool ShowDot { get; set; }
    public int DotSize { get; set; } = 2;

    public bool ShowTopLine { get; set; } = true;
    public bool ShowBottomLine { get; set; } = true;
    public bool ShowLeftLine { get; set; } = true;
    public bool ShowRightLine { get; set; } = true;

    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    public string MonitorDeviceName { get; set; } = "";

    public CrosshairAnimation Animation { get; set; } = CrosshairAnimation.None;
    public int AnimationSpeed { get; set; } = 5;   // 1..10
    public bool Rainbow { get; set; }

    public string? ImagePath { get; set; }
    public int ImageScale { get; set; } = 100;     // 10..400

    // Pixel-art crosshair (CrosshairType.Pixel). PixelGridSize is the grid dimension
    // (NxN cells); PixelArtData is a string of '0'/'1' chars of length PixelGridSize²,
    // row-major. Empty string = "no pixel painted yet" → the renderer falls back to a
    // single centre pixel so the crosshair is still visible.
    public int PixelGridSize { get; set; } = 16;
    public string PixelArtData { get; set; } = "";

    public CrosshairProfile Clone()
    {
        return new CrosshairProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name,
            IsBuiltIn = false,
            Type = Type,
            Color = Color,
            OutlineColor = OutlineColor,
            OutlineThickness = OutlineThickness,
            Size = Size,
            Thickness = Thickness,
            Gap = Gap,
            Opacity = Opacity,
            Rotation = Rotation,
            ShowDot = ShowDot,
            DotSize = DotSize,
            ShowTopLine = ShowTopLine,
            ShowBottomLine = ShowBottomLine,
            ShowLeftLine = ShowLeftLine,
            ShowRightLine = ShowRightLine,
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            MonitorDeviceName = MonitorDeviceName,
            Animation = Animation,
            AnimationSpeed = AnimationSpeed,
            Rainbow = Rainbow,
            ImagePath = ImagePath,
            ImageScale = ImageScale,
            PixelGridSize = PixelGridSize,
            PixelArtData = PixelArtData,
        };
    }
}

public sealed record MonitorInfo(string DeviceName, string FriendlyName, int X, int Y, int Width, int Height, bool IsPrimary);
