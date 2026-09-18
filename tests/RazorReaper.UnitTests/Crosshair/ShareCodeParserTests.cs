using RazorReaper.Models;
using RazorReaper.Services.Implementations;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// CS2 / CS:GO share-code decoding, checked against the six published round-trip vectors from
/// akiver/csgo-sharecode (src/index.test.ts). Those vectors are the closest thing the format has
/// to a specification: each one is a real code paired with the cvar values the reference
/// implementation decodes it to, and it encodes back to the same string.
/// </summary>
public class ShareCodeParserTests
{
    private static CrosshairProfile Basis() => new()
    {
        Name = "basis",
        Type = CrosshairType.Circle,
        Color = "#123456",
        Size = 99,
        Thickness = 9,
        Gap = 42,
        Opacity = 7,
        OutlineThickness = 6,
        ShowDot = true,
        DotSize = 11,
    };

    private static CrosshairProfile Parse(string code)
    {
        var result = CrosshairCodeParsers.TryParse(code, Basis());
        Assert.Null(result.Error);
        Assert.Equal(CrosshairCodeFormat.SourceShareCode, result.Format);
        Assert.NotNull(result.Profile);
        return result.Profile!;
    }

    // ─── Published vectors ────────────────────────────────────────────────────

    [Fact]
    public void Vector1_CustomPurple_TStyle_WithOutlineAndDot()
    {
        // gap -1.3, outline 2, rgb(175,81,213), alpha 137, colour 5 (custom), thickness 1.2,
        // dot on, alpha on, t-style on, length 4.6, outline enabled.
        var p = Parse("CSGO-Cn37R-YE7vo-pLCAL-aURmZ-z6zkG");

        Assert.Equal("#AF51D5", p.Color);
        Assert.Equal(CrosshairType.TStyle, p.Type);
        Assert.True(p.ShowDot);
        Assert.Equal(2, p.OutlineThickness);                       // outline 2.0
        Assert.Equal(9, p.Size);                                   // 4.6 × 2 = 9.2 → 9
        Assert.Equal(2, p.Thickness);                              // 1.2 × 2 = 2.4 → 2
        Assert.Equal(4, p.Gap);                                    // -1.3 → -1, +5
        Assert.Equal(54, p.Opacity);                               // 137/255 = 53.7 %
    }

    [Fact]
    public void Vector2_GreenPreset_NoOutline_NoDot()
    {
        // gap 1, outline 1 (disabled), rgb(50,250,50), alpha 200 but usealpha off, colour 1,
        // thickness 0.5, length 5, no dot, no t-style.
        var p = Parse("CSGO-LibdP-VCVEd-ESayK-rSivi-2UBtG");

        Assert.Equal("#00FF00", p.Color);                          // preset 1 = green, not the RGB
        Assert.Equal(CrosshairType.Cross, p.Type);
        Assert.False(p.ShowDot);
        Assert.Equal(0, p.OutlineThickness);                       // drawoutline off
        Assert.Equal(10, p.Size);                                  // 5 × 2
        Assert.Equal(1, p.Thickness);                              // 0.5 × 2
        Assert.Equal(6, p.Gap);                                    // 1 + 5
        Assert.Equal(100, p.Opacity);                              // usealpha off → opaque
    }

    [Fact]
    public void Vector3_CustomColour_DotOn_AlphaOn()
    {
        var p = Parse("CSGO-9JzcN-4dZtA-DdXis-8qz5T-rCnkP");

        Assert.Equal("#32FA32", p.Color);                          // colour 5 → custom rgb(50,250,50)
        Assert.True(p.ShowDot);
        Assert.Equal(CrosshairType.Cross, p.Type);
        Assert.Equal(78, p.Opacity);                               // alpha 200 with usealpha on
        Assert.Equal(10, p.Size);
    }

    [Fact]
    public void Vector4_DiffersFromVector3_OnlyInStyleAndRecoil()
    {
        // Same crosshair as vector 3 apart from style 2 and followRecoil — neither of which our
        // model carries, so the imported profile must be identical.
        var a = Parse("CSGO-9JzcN-4dZtA-DdXis-8qz5T-rCnkP");
        var b = Parse("CSGO-fCUBz-CBHss-a74RP-SEdO8-mvZpG");

        Assert.Equal(a.Color, b.Color);
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.Thickness, b.Thickness);
        Assert.Equal(a.Gap, b.Gap);
        Assert.Equal(a.Opacity, b.Opacity);
        Assert.Equal(a.ShowDot, b.ShowDot);
        Assert.Equal(a.Type, b.Type);
    }

    [Fact]
    public void Vector5_LongArms_NegativeGap_OutlineOn()
    {
        // gap -2.2, outline 1, colour 1 (green), thickness 0.6, length 10, outline enabled,
        // alpha 200 with usealpha on, no dot.
        var p = Parse("CSGO-WsnnD-eHaMw-QNDf9-oxuDh-ydOUD");

        Assert.Equal("#00FF00", p.Color);
        Assert.Equal(20, p.Size);                                  // 10 × 2
        Assert.Equal(1, p.Thickness);                              // 0.6 × 2 = 1.2 → 1
        Assert.Equal(3, p.Gap);                                    // -2.2 → -2, +5
        Assert.Equal(1, p.OutlineThickness);
        Assert.False(p.ShowDot);
        Assert.Equal(78, p.Opacity);
    }

    [Fact]
    public void Vector6_CustomPink_TStyle_OutlineDisabled_AlphaDisabled()
    {
        // gap -1.2, rgb(232,88,227), colour 5, thickness 1.5, length 7.2, dot on, t-style on,
        // outline disabled, usealpha off.
        var p = Parse("CSGO-ZrEjo-yASEP-OAdce-Sf44w-rhK5O");

        Assert.Equal("#E858E3", p.Color);
        Assert.Equal(CrosshairType.TStyle, p.Type);
        Assert.True(p.ShowDot);
        Assert.Equal(0, p.OutlineThickness);
        Assert.Equal(14, p.Size);                                  // 7.2 × 2 = 14.4 → 14
        Assert.Equal(3, p.Thickness);                              // 1.5 × 2
        Assert.Equal(4, p.Gap);                                    // -1.2 → -1, +5
        Assert.Equal(100, p.Opacity);
    }

    // ─── Accepted input shapes ────────────────────────────────────────────────

    [Theory]
    [InlineData("CSGO-LibdP-VCVEd-ESayK-rSivi-2UBtG")]
    [InlineData("csgo-LibdP-VCVEd-ESayK-rSivi-2UBtG")]
    [InlineData("  CSGO-LibdP-VCVEd-ESayK-rSivi-2UBtG  ")]
    [InlineData("CSGOLibdPVCVEdESayKrSivi2UBtG")]
    public void AcceptsTheUsualCopyPasteVariants(string code)
    {
        Assert.Equal("#00FF00", Parse(code).Color);
    }

    [Fact]
    public void LeavesUnrelatedProfileFieldsAlone()
    {
        // The import layers onto the active profile, so per-machine settings must survive.
        var basis = Basis();
        basis.MonitorDeviceName = "\\\\.\\DISPLAY2";
        basis.OffsetX = 12;
        basis.OffsetY = -3;

        var p = CrosshairCodeParsers.TryParse("CSGO-LibdP-VCVEd-ESayK-rSivi-2UBtG", basis).Profile!;

        Assert.Equal("\\\\.\\DISPLAY2", p.MonitorDeviceName);
        Assert.Equal(12, p.OffsetX);
        Assert.Equal(-3, p.OffsetY);
        Assert.False(p.IsBuiltIn);
        Assert.NotEqual(basis.Id, p.Id);
    }

    // ─── Rejected input, with a reason ────────────────────────────────────────

    [Fact]
    public void TooShort_SaysSo()
    {
        var result = CrosshairCodeParsers.TryParse("CSGO-LibdP-VCVEd-ESayK-rSivi-2UBt", Basis());

        Assert.Null(result.Profile);
        Assert.Equal(CrosshairCodeFormat.SourceShareCode, result.Format);
        Assert.Contains("25 characters", result.Error);
        Assert.Contains("found 24", result.Error);
    }

    [Fact]
    public void CharacterOutsideTheAlphabet_NamesTheCharacter()
    {
        // 'l' is one of the glyphs the alphabet deliberately omits.
        var result = CrosshairCodeParsers.TryParse("CSGO-LibdP-VCVEd-ESayK-rSivi-2UBtl", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("\"l\"", result.Error);
    }

    [Fact]
    public void MatchShareCode_IsRejectedAsNotACrosshair()
    {
        // Match codes use the same alphabet and length; only the checksum tells them apart.
        var result = CrosshairCodeParsers.TryParse("CSGO-L9spZ-ihuov-cyhtE-kxbqa-FkBAA", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("checksum", result.Error);
        Assert.Contains("match share code", result.Error);
    }

    [Theory]
    [InlineData("CSGO-12345-12345-12345-12345-12345")]
    [InlineData("CSGO-11111-22222-33333-44444-55555")]
    public void GarbageWithTheRightShape_FailsTheChecksum(string code)
    {
        var result = CrosshairCodeParsers.TryParse(code, Basis());

        Assert.Null(result.Profile);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void NonCode_PointsAtBothSupportedFormatsAndAtRust()
    {
        var result = CrosshairCodeParsers.TryParse("just some text", Basis());

        Assert.Null(result.Profile);
        Assert.Equal(CrosshairCodeFormat.Unknown, result.Format);
        Assert.Contains("Valorant", result.Error);
        Assert.Contains("CSGO-", result.Error);
        Assert.Contains("Rust", result.Error);
        Assert.Contains("Crosshair X", result.Error);
    }

    [Fact]
    public void EmptyInput_AsksForACode()
    {
        Assert.Contains("Paste", CrosshairCodeParsers.TryParse("   ", Basis()).Error);
    }
}
