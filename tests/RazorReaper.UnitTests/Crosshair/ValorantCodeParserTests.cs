using RazorReaper.Models;
using RazorReaper.Services.Implementations;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// Valorant profile-code decoding. The codes below are real exports taken from the sample set
/// shipped with genesy/crosshair-codes (src/samplecrosshairs.ts); the expected values follow that
/// project's key mapping (src/codegenerator.ts), which is the most complete public description of
/// the format. Each test names the trap it covers, because every one of them was a live bug.
/// </summary>
public class ValorantCodeParserTests
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
        Assert.Equal(CrosshairCodeFormat.Valorant, result.Format);
        Assert.NotNull(result.Profile);
        return result.Profile!;
    }

    // ─── Real sample codes ────────────────────────────────────────────────────

    [Fact]
    public void PlainInnerLines_MapOntoSizeThicknessAndGap()
    {
        var p = Parse("0;P;c;5;h;0;f;0;0t;1;0l;5;0o;1;0a;1;0f;0;1t;3;1l;3;1o;2;1a;0.3;1m;0;1f;0");

        Assert.Equal(CrosshairType.Cross, p.Type);
        Assert.Equal("#00FFFF", p.Color);     // c;5 = cyan
        Assert.Equal(5, p.Size);              // 0l
        Assert.Equal(1, p.Thickness);         // 0t
        Assert.Equal(1, p.Gap);               // 0o
        Assert.Equal(100, p.Opacity);         // 0a;1
        Assert.Equal(0, p.OutlineThickness);  // h;0
        Assert.False(p.ShowDot);
        Assert.True(p.ShowTopLine && p.ShowBottomLine && p.ShowLeftLine && p.ShowRightLine);
    }

    [Fact]
    public void CentreDotOpacity_IsNotTheCrosshairOpacity()
    {
        // `a` is the CENTRE DOT's opacity. Reading it as the body alpha turned this perfectly
        // ordinary red crosshair into a 37 %-opaque, barely visible one.
        var p = Parse("0;P;c;7;t;2;o;1;d;1;z;3;a;0.374;f;0;s;0;0t;10;0l;2;0o;2;0a;1;0f;0;1b;0");

        Assert.Equal(100, p.Opacity);         // from 0a;1, not from a;0.374
        Assert.Equal("#FF0000", p.Color);     // c;7 = red
        Assert.Equal(2, p.OutlineThickness);  // t;2, outlines on by default
        Assert.True(p.ShowDot);               // d;1
        Assert.Equal(2, p.DotSize);           // z;3 is a diameter, DotSize is a radius
        Assert.Equal(10, p.Thickness);        // 0t
        Assert.Equal(2, p.Size);              // 0l
        Assert.Equal(2, p.Gap);               // 0o
    }

    [Fact]
    public void BodyOpacity_ComesFromTheInnerLineAlpha()
    {
        var p = Parse("0;P;c;1;o;1;f;0;0l;5;0a;0.5;0f;0;1b;0");

        Assert.Equal(50, p.Opacity);
        Assert.Equal("#00FF00", p.Color);
    }

    [Fact]
    public void VerticalLengthZero_DoesNotHideTheCrosshair()
    {
        // `0v` is the inner lines' VERTICAL length and `0g` is "length not linked" — neither is a
        // visibility flag. Treating them as one made every crosshair with `0v;0` render nothing.
        var p = Parse("0;s;1;P;c;5;h;0;0l;5;0v;0;0g;1;0a;1;0f;0;1l;0;1v;4;1g;1;1o;2;1a;1;1m;0;1f;0;S;c;5;o;1");

        Assert.True(p.ShowTopLine && p.ShowBottomLine && p.ShowLeftLine && p.ShowRightLine);
        Assert.Equal(5, p.Size);
        Assert.Equal("#00FFFF", p.Color);
    }

    [Fact]
    public void InnerLinesOffWithADot_BecomesADotCrosshair()
    {
        // `0b;0` is the real "hide the inner lines" key.
        var p = Parse("0;s;1;P;c;7;o;1;d;1;0b;0;1b;0;S;c;5;s;0.64;o;1");

        Assert.Equal(CrosshairType.Dot, p.Type);
        Assert.True(p.ShowDot);
        Assert.False(p.ShowTopLine || p.ShowBottomLine || p.ShowLeftLine || p.ShowRightLine);
        Assert.Equal("#FF0000", p.Color);
    }

    [Fact]
    public void PresetColour_WinsOverALeftoverCustomColour()
    {
        // `u` only applies when `c` is 8. This code picks cyan from the dropdown and still carries
        // a stale purple custom value; the game shows cyan, so the import must too.
        var p = Parse("0;p;0;s;1;P;c;5;u;420690FF;o;1;f;0;0t;1;0l;2;0v;3;0o;2;0a;1;0f;0;1b;0;A;o;1;d;1;0l;0;0o;2;0a;1;1b;0");

        Assert.Equal("#00FFFF", p.Color);
        Assert.False(p.ShowDot);  // the d;1 belongs to the ADS section, not to the primary one
    }

    [Fact]
    public void CustomColour_AppliesWhenTheDropdownSaysCustom()
    {
        var p = Parse("0;P;c;8;u;FF99FFFF;h;0;b;1;0l;4;0o;1;0a;1;0f;0;1t;0;1l;0;1o;0;1a;0;1m;0;1f;0");

        Assert.Equal("#FF99FF", p.Color);
        Assert.Equal(0, p.OutlineThickness);
        Assert.Equal(4, p.Size);
        Assert.Equal(1, p.Gap);
    }

    [Fact]
    public void CustomColour_WithoutADropdownIndex_IsStillHonoured()
    {
        var p = Parse("0;s;1;P;u;000000FF;o;1;d;1;0b;0;1b;0;S;s;0.628;o;1");

        Assert.Equal("#000000", p.Color);
        Assert.Equal(CrosshairType.Dot, p.Type);
    }

    [Fact]
    public void GeneralSectionKeys_AreNotReadAsPrimaryOnes()
    {
        // The leading `c;1` is "override all primary crosshairs", not the crosshair colour.
        var p = Parse("0;c;1;P;u;000000FF;h;0;d;1;z;1;f;0;m;1;0t;1;0l;2;0v;5;0o;1;0a;1;0e;0.5;1b;0");

        Assert.Equal("#000000", p.Color);
        Assert.Equal(2, p.Size);
        Assert.Equal(1, p.Thickness);
        Assert.Equal(1, p.Gap);
    }

    [Fact]
    public void LeadingEmptyToken_IsTolerated()
    {
        var p = Parse(";P;c;1;o;1;0l;3;0o;5;0a;1;0f;0;1b;0");

        Assert.Equal("#00FF00", p.Color);
        Assert.Equal(3, p.Size);
        Assert.Equal(5, p.Gap);
    }

    [Fact]
    public void OutlineIsOnByDefault_AndOffWhenTheCodeSaysSo()
    {
        // Valorant's default is outlines on at thickness 1.
        Assert.Equal(1, Parse("0;P;c;1;0l;4;0a;1").OutlineThickness);
        Assert.Equal(0, Parse("0;P;c;1;h;0;t;3;0l;4;0a;1").OutlineThickness);
        Assert.Equal(3, Parse("0;P;c;1;h;1;t;3;0l;4;0a;1").OutlineThickness);
    }

    [Theory]
    [InlineData(0, "#FFFFFF")]
    [InlineData(1, "#00FF00")]
    [InlineData(2, "#7FFF00")]
    [InlineData(3, "#DFFF00")]
    [InlineData(4, "#FFFF00")]
    [InlineData(5, "#00FFFF")]
    [InlineData(6, "#FF00FF")]
    [InlineData(7, "#FF0000")]
    public void PresetColourTable_MatchesTheGame(int index, string expected)
    {
        Assert.Equal(expected, Parse($"0;P;c;{index};0l;4;0a;1").Color);
    }

    [Fact]
    public void NamedProfile_KeepsItsName()
    {
        Assert.Equal("Sharp", Parse("0;P;c;1;0l;4;0a;1;NAME;\"Sharp\"").Name);
        Assert.Equal("Valorant import", Parse("0;P;c;1;0l;4;0a;1").Name);
    }

    [Fact]
    public void LeavesUnrelatedProfileFieldsAlone()
    {
        var basis = Basis();
        basis.MonitorDeviceName = "\\\\.\\DISPLAY3";
        basis.OffsetX = -8;

        var p = CrosshairCodeParsers.TryParse("0;P;c;1;0l;4;0a;1", basis).Profile!;

        Assert.Equal("\\\\.\\DISPLAY3", p.MonitorDeviceName);
        Assert.Equal(-8, p.OffsetX);
        Assert.False(p.IsBuiltIn);
    }

    // ─── Rejected input, with a reason ────────────────────────────────────────

    [Fact]
    public void NoPrimarySection_SaysWhichSectionIsMissing()
    {
        var result = CrosshairCodeParsers.TryParse("0;c;1;s;1", Basis());

        Assert.Null(result.Profile);
        Assert.Equal(CrosshairCodeFormat.Valorant, result.Format);
        Assert.Contains("\"P\" section", result.Error);
    }

    [Fact]
    public void ColourIndexOutOfRange_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;9;0l;4;0a;1", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("colour \"c\"", result.Error);
        Assert.Contains("expected 0-8", result.Error);
    }

    [Fact]
    public void NonNumericLength_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;1;0l;abc;0a;1", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("inner line length \"0l\"", result.Error);
        Assert.Contains("\"abc\"", result.Error);
    }

    [Fact]
    public void NonNumericThickness_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;1;0t;wide;0l;4;0a;1", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("inner line thickness \"0t\"", result.Error);
    }

    [Fact]
    public void NonNumericOffset_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;1;0o;far;0l;4;0a;1", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("inner line offset \"0o\"", result.Error);
    }

    [Fact]
    public void NonNumericOpacity_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;1;0l;4;0a;half", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("inner line opacity \"0a\"", result.Error);
    }

    [Fact]
    public void NonNumericOutlineThickness_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;1;t;thin;0l;4;0a;1", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("outline thickness \"t\"", result.Error);
    }

    [Fact]
    public void BadCustomColour_NamesTheField()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;8;u;NOTHEX;0l;4;0a;1", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("custom colour \"u\"", result.Error);
    }

    [Fact]
    public void TrailingKeyWithNoValue_SaysWhichKey()
    {
        var result = CrosshairCodeParsers.TryParse("0;P;c;1;0l", Basis());

        Assert.Null(result.Profile);
        Assert.Contains("\"0l\"", result.Error);
    }

    [Fact]
    public void DecimalWrittenForAWholeNumberField_IsAccepted()
    {
        // Some generators export "4.0" where the game writes "4".
        var p = Parse("0;P;c;1;0l;4.0;0t;2.0;0a;1");

        Assert.Equal(4, p.Size);
        Assert.Equal(2, p.Thickness);
    }
}
