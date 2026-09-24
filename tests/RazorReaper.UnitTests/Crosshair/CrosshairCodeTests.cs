using System.Buffers.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RazorReaper.Models;
using RazorReaper.Services.Implementations;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// The promise of a RazorReaper crosshair code is that what you paste is what was copied, pixel
/// for pixel. So every check here renders both sides with the real renderer and compares the
/// buffers, and every way a pasted text can be wrong is refused with a reason, never a profile.
/// </summary>
public class CrosshairCodeTests
{
    // Animated looks differ per frame; the same frames on both sides must still match.
    private static readonly double[] Phases = [0.0, 0.3, 0.75];

    [Fact]
    public void EveryBuiltInPresetRoundTripsToTheSameProfileAndTheSamePixels()
    {
        foreach (var preset in CrosshairBuiltInPresets.All)
        {
            var code = CrosshairCode.Encode(preset);

            Assert.NotNull(code);
            Assert.StartsWith(CrosshairCode.Prefix, code);
            Assert.True(code.Length < 400, $"{preset.Name}: {code.Length} characters");
            AssertRoundTrip(preset, code);
        }
    }

    [Fact]
    public void RandomValidProfilesRoundTripToTheSameProfileAndTheSamePixels()
    {
        var random = new Random(20260924);

        for (var i = 0; i < 150; i++)
        {
            var profile = RandomProfile(random);
            var code = CrosshairCode.Encode(profile);

            Assert.NotNull(code);
            // Discord's message limit — the place these codes are posted.
            Assert.True(code.Length < 2000, $"#{i} ({profile.Type}, grid {profile.PixelGridSize}): {code.Length} characters");
            AssertRoundTrip(profile, code);
        }
    }

    [Fact]
    public void TheCodeCarriesTheLookAndNeverThePlaceOrThePicture()
    {
        var mine = CrosshairBuiltInPresets.All[0].Clone();
        mine.MonitorDeviceName = @"\\.\DISPLAY2";
        mine.OffsetX = 40;
        mine.OffsetY = -12;
        mine.ImagePath = @"C:\Users\someone\crosshair.png";
        mine.ImageScale = 250;

        // The same look placed elsewhere is the same code.
        Assert.Equal(CrosshairCode.Encode(CrosshairBuiltInPresets.All[0]), CrosshairCode.Encode(mine));

        var local = new CrosshairProfile { MonitorDeviceName = @"\\.\DISPLAY1", OffsetX = 3, OffsetY = 4, ImagePath = "mine.png", ImageScale = 80 };
        var decoded = CrosshairCode.Decode(CrosshairCode.Encode(mine), local).Profile!;

        Assert.Equal(local.MonitorDeviceName, decoded.MonitorDeviceName);
        Assert.Equal((3, 4), (decoded.OffsetX, decoded.OffsetY));
        Assert.Equal(("mine.png", 80), (decoded.ImagePath, decoded.ImageScale));
    }

    [Fact]
    public void AnImageCrosshairHasNoCode()
    {
        var image = new CrosshairProfile { Type = CrosshairType.Image, ImagePath = "a.png" };

        Assert.False(CrosshairCode.CanEncode(image));
        Assert.Null(CrosshairCode.Encode(image));
        Assert.Equal(CrosshairCodeError.Damaged, CrosshairCode.Decode(Code("""{"v":1,"t":4}"""), new()).Error);
    }

    [Fact]
    public void ACodeCopiedOutOfDiscordStillReads()
    {
        var code = CrosshairCode.Encode(CrosshairBuiltInPresets.All[2])!;

        foreach (var pasted in new[] { $"  {code}\n", $"`{code}`", $"```{code}```" })
        {
            Assert.Equal(CrosshairCodeError.None, CrosshairCode.Decode(pasted, new()).Error);
        }
    }

    [Fact]
    public void EveryNumberIsClampedToTheEditorsRangeAndUnknownKeysAreIgnored()
    {
        var decoded = CrosshairCode.Decode(
            Code("""{"v":1,"t":99,"s":9999,"th":0,"g":-500,"op":300,"r":720,"ds":-3,"ot":50,"a":9,"as":0,"pg":1000,"future":"x"}"""),
            new()).Profile!;

        Assert.Equal(CrosshairType.Pixel, decoded.Type);
        Assert.Equal(150, decoded.Size);
        Assert.Equal(1, decoded.Thickness);
        Assert.Equal(-10, decoded.Gap);
        Assert.Equal(100, decoded.Opacity);
        Assert.Equal(359, decoded.Rotation);
        Assert.Equal(1, decoded.DotSize);
        Assert.Equal(8, decoded.OutlineThickness);
        Assert.Equal(CrosshairAnimation.Rotate, decoded.Animation);
        Assert.Equal(1, decoded.AnimationSpeed);
        Assert.Equal(64, decoded.PixelGridSize);
    }

    [Fact]
    public void AMissingKeyIsTheDefault()
    {
        var decoded = CrosshairCode.Decode(Code("""{"v":1}"""), new()).Profile!;
        var defaults = new CrosshairProfile();

        Assert.Equal(Look(defaults), Look(decoded));
    }

    public static TheoryData<string, string> Refusals()
    {
        var real = CrosshairCode.Encode(CrosshairBuiltInPresets.All[6])!;
        var payload = real[CrosshairCode.Prefix.Length..];

        // The expected error by name: the enum is internal, and a test method's parameters are public.
        return new TheoryData<string, string>
        {
            { "", nameof(CrosshairCodeError.NotACode) },
            { "   ", nameof(CrosshairCodeError.NotACode) },
            { "hello there", nameof(CrosshairCodeError.NotACode) },
            { "XX1-" + payload, nameof(CrosshairCodeError.NotACode) },
            { payload, nameof(CrosshairCodeError.NotACode) },
            // An Arabic-Indic or fullwidth "1" is no version number.
            { "RR١-" + payload, nameof(CrosshairCodeError.NotACode) },
            { "RR１-" + payload, nameof(CrosshairCodeError.NotACode) },
            // A CS2 share code and a Valorant profile code: named, never decoded.
            { "CSGO-O4Jsi-V36wY-rTMGK-9w7qF-jQ8WB", nameof(CrosshairCodeError.GameCode) },
            { "0;P;c;5;h;0;m;1;0l;4;0o;2;0a;1;0f;0;1b;0", nameof(CrosshairCodeError.GameCode) },
            { "RR2-" + payload, nameof(CrosshairCodeError.NewerVersion) },
            { Code("""{"v":2,"s":20}"""), nameof(CrosshairCodeError.NewerVersion) },
            { "RR0-" + payload, nameof(CrosshairCodeError.Damaged) },
            { real[..(real.Length / 2)], nameof(CrosshairCodeError.Damaged) },
            { CrosshairCode.Prefix, nameof(CrosshairCodeError.Damaged) },
            { CrosshairCode.Prefix + "!!!not base64!!!", nameof(CrosshairCodeError.Damaged) },
            { CrosshairCode.Prefix + Base64Url.EncodeToString("not deflate at all"u8), nameof(CrosshairCodeError.Damaged) },
            { CrosshairCode.Prefix + new string('A', 5000), nameof(CrosshairCodeError.Damaged) },
            // Inflates past the cap: a zip bomb stops there instead of filling memory.
            { Code("{\"v\":1,\"px\":\"" + new string('0', 1_000_000) + "\"}"), nameof(CrosshairCodeError.Damaged) },
            { Code("not json"), nameof(CrosshairCodeError.Damaged) },
            { Code("[1,2,3]"), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"s":20}"""), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"v":"1"}"""), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"v":1,"s":"big"}"""), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"v":1,"s":1.5}"""), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"v":1,"d":1}"""), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"v":1,"c":"red"}"""), nameof(CrosshairCodeError.Damaged) },
            { Code("""{"v":1,"px":"0120"}"""), nameof(CrosshairCodeError.Damaged) },
        };
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AnythingButAWholeCodeOfOursIsRefusedWithAReason(string pasted, string expected)
    {
        var result = CrosshairCode.Decode(pasted, new CrosshairProfile());

        Assert.Null(result.Profile);
        Assert.Equal(expected, result.Error.ToString());
    }

    [Fact]
    public void TheCodeKeepsItsLengthInCheck()
    {
        // The largest look there is — a 64×64 grid of noise, which no compressor can shrink — still
        // fits one Discord message, and pastes back.
        var random = new Random(7);
        var largest = new CrosshairProfile
        {
            Type = CrosshairType.Pixel,
            PixelGridSize = 64,
            PixelArtData = new string(Enumerable.Range(0, 64 * 64).Select(_ => random.Next(2) == 0 ? '1' : '0').ToArray()),
        };
        var code = CrosshairCode.Encode(largest)!;

        Assert.True(code.Length < 2000, $"{code.Length} characters");
        AssertRoundTrip(largest, code);
    }

    private static void AssertRoundTrip(CrosshairProfile original, string code)
    {
        var result = CrosshairCode.Decode(code, original);
        Assert.Equal(CrosshairCodeError.None, result.Error);
        var decoded = result.Profile!;

        // Identity is never in a code; everything else — look and the kept local fields — must match.
        decoded.Id = original.Id;
        decoded.Name = original.Name;
        decoded.IsBuiltIn = original.IsBuiltIn;
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(decoded));

        foreach (var phase in Phases)
        {
            Assert.True(SamePixels(original, decoded, phase), $"pixels differ at phase {phase}: {code}");
        }
    }

    private static bool SamePixels(CrosshairProfile a, CrosshairProfile b, double phase)
    {
        using var left = CrosshairRenderer.Render(a, phase, null);
        using var right = CrosshairRenderer.Render(b, phase, null);
        return left.Size == right.Size && Pixels(left).AsSpan().SequenceEqual(Pixels(right));
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static string Look(CrosshairProfile p)
    {
        p.Id = "";
        p.Name = "";
        return JsonSerializer.Serialize(p);
    }

    /// <summary>A code built by hand around <paramref name="json"/>, the way <see cref="CrosshairCode.Encode"/> packs one.</summary>
    private static string Code(string json)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize))
        {
            deflate.Write(Encoding.UTF8.GetBytes(json));
        }
        return CrosshairCode.Prefix + Base64Url.EncodeToString(packed.ToArray());
    }

    private static CrosshairProfile RandomProfile(Random random)
    {
        CrosshairType[] shapes = [CrosshairType.Cross, CrosshairType.Dot, CrosshairType.Circle, CrosshairType.TStyle, CrosshairType.Pixel];
        int[] grids = [8, 16, 24, 32, 64];
        var grid = grids[random.Next(grids.Length)];

        return new CrosshairProfile
        {
            Name = "random",
            Type = shapes[random.Next(shapes.Length)],
            Color = Colour(random),
            OutlineColor = Colour(random),
            OutlineThickness = random.Next(0, 9),
            Size = random.Next(1, 151),
            Thickness = random.Next(1, 21),
            Gap = random.Next(-10, 61),
            Opacity = random.Next(0, 101),
            Rotation = random.Next(4) == 0 ? random.Next(0, 360) : 0,
            ShowDot = random.Next(2) == 0,
            DotSize = random.Next(1, 31),
            ShowTopLine = random.Next(2) == 0,
            ShowBottomLine = random.Next(2) == 0,
            ShowLeftLine = random.Next(2) == 0,
            ShowRightLine = random.Next(2) == 0,
            Animation = (CrosshairAnimation)random.Next(0, 4),
            AnimationSpeed = random.Next(1, 11),
            Rainbow = random.Next(4) == 0,
            MonitorDeviceName = @"\\.\DISPLAY1",
            OffsetX = random.Next(-3000, 3001),
            OffsetY = random.Next(-3000, 3001),
            PixelGridSize = grid,
            PixelArtData = random.Next(5) == 0
                ? ""
                : new string(Enumerable.Range(0, grid * grid).Select(_ => random.Next(3) == 0 ? '1' : '0').ToArray()),
        };
    }

    private static string Colour(Random random) => random.Next(6) switch
    {
        // The editor's own spelling most of the time, and the other spellings the renderer reads.
        0 => $"#{random.Next(0x1000):x3}",
        1 => $"#{(uint)random.NextInt64(0x1_0000_0000):X8}",
        2 => $"{random.Next(0x1000000):X6}",
        _ => $"#{random.Next(0x1000000):X6}",
    };
}
