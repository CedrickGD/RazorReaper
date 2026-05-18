using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Static catalog of the built-in crosshair presets shipped with the app. Lives in its own file
/// so the main <see cref="CrosshairService"/> doesn't carry ~130 lines of preset data. Each entry
/// is treated as immutable — callers (the service's <c>GetBuiltInPresets()</c>) hand out fresh
/// clones rather than the source object.
/// </summary>
internal static class CrosshairBuiltInPresets
{
    public static IReadOnlyList<CrosshairProfile> All { get; } = new List<CrosshairProfile>
    {
        new CrosshairProfile
        {
            Id = "builtin-valorant",
            Name = "Valorant",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#00FF66",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 6,
            Thickness = 2,
            Gap = 3,
            Opacity = 100,
            ShowDot = false,
        },
        new CrosshairProfile
        {
            Id = "builtin-cs",
            Name = "CS Classic",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#00FFFF",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 1,
            Gap = 4,
            Opacity = 100,
            ShowDot = false,
        },
        new CrosshairProfile
        {
            Id = "builtin-sniper",
            Name = "Sniper",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#FF1744",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 40,
            Thickness = 1,
            Gap = 6,
            Opacity = 100,
            ShowDot = true,
            DotSize = 1,
        },
        new CrosshairProfile
        {
            Id = "builtin-dot",
            Name = "Tactical Dot",
            IsBuiltIn = true,
            Type = CrosshairType.Dot,
            Color = "#FFEE00",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            DotSize = 3,
            Opacity = 100,
        },
        new CrosshairProfile
        {
            Id = "builtin-circle",
            Name = "Ring",
            IsBuiltIn = true,
            Type = CrosshairType.Circle,
            Color = "#8b5cf6",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 2,
            Opacity = 90,
            ShowDot = true,
            DotSize = 2,
        },
        new CrosshairProfile
        {
            Id = "builtin-t",
            Name = "T-Style",
            IsBuiltIn = true,
            Type = CrosshairType.TStyle,
            Color = "#FFFFFF",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 8,
            Thickness = 2,
            Gap = 4,
            Opacity = 100,
        },
        new CrosshairProfile
        {
            Id = "builtin-rainbow",
            Name = "Rainbow Pulse",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#FF0000",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 3,
            Gap = 5,
            Opacity = 100,
            ShowDot = true,
            DotSize = 2,
            Animation = CrosshairAnimation.Pulse,
            AnimationSpeed = 6,
            Rainbow = true,
        },
        new CrosshairProfile
        {
            Id = "builtin-breath",
            Name = "Breathing Ring",
            IsBuiltIn = true,
            Type = CrosshairType.Circle,
            Color = "#a78bfa",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 14,
            Thickness = 2,
            Opacity = 100,
            Animation = CrosshairAnimation.Breath,
            AnimationSpeed = 3,
        },
    };
}
