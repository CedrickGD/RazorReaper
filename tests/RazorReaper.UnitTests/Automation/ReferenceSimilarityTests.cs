using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The comparison every snapshot script fires on. It used to be the mean colour difference, and
/// a member put the consequence in one line: "im just punching the air when using it" (Take All,
/// #rr-chat 2026-09-24). The dark panel behind the Take All button and the dark world behind a
/// closed inventory are a few levels apart per channel — 95 % on that scale — and a black frame
/// from a capture path that cannot see a fullscreen game matched a dark reference outright.
///
/// Every image here is drawn, not captured: a "button" is bright glyph blocks on a dark panel,
/// the "world" is dark noise from a fixed seed, so the numbers are the same on every machine.
/// </summary>
public sealed class ReferenceSimilarityTests
{
    private const int W = 60, H = 24;

    // ─── Fixtures ───────────────────────────────────────────────────────────────

    /// <summary>Dark panel (B30 G30 R34) with thin bright text (210) in four rows — ~6 % of the region, like a button label.</summary>
    private static ScreenCapture Button(int brighten = 0)
    {
        var bgra = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var glyph = y is >= 10 and < 14 && x is >= 10 and < 50 && (x / 4) % 2 == 0;
                var i = (y * W + x) * 4;
                bgra[i] = Clamp((glyph ? 210 : 30) + brighten);
                bgra[i + 1] = Clamp((glyph ? 210 : 30) + brighten);
                bgra[i + 2] = Clamp((glyph ? 210 : 34) + brighten);
                bgra[i + 3] = 255;
            }
        }
        return new ScreenCapture(W, H, bgra);
    }

    /// <summary>Uniform dark: what the panel looks like with no button, or a night sky.</summary>
    private static ScreenCapture Flat(byte level)
    {
        var bgra = new byte[W * H * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = bgra[i + 1] = bgra[i + 2] = level; bgra[i + 3] = 255; }
        return new ScreenCapture(W, H, bgra);
    }

    /// <summary>Dark, textured world: every channel somewhere in 15..54, fixed seed.</summary>
    private static ScreenCapture World(int seed = 7)
    {
        var rng = new Random(seed);
        var bgra = new byte[W * H * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)rng.Next(15, 55);
            bgra[i + 1] = (byte)rng.Next(15, 55);
            bgra[i + 2] = (byte)rng.Next(15, 55);
            bgra[i + 3] = 255;
        }
        return new ScreenCapture(W, H, bgra);
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>The metric this replaces, kept only to show what it said about the same frames.</summary>
    private static double OldMeanDifferenceSimilarity(ScreenCapture a, ScreenCapture b)
    {
        long diff = 0;
        for (var i = 0; i < a.Bgra.Length; i += 4)
        {
            diff += Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
        }
        return 100.0 - diff / (double)(a.Width * a.Height * 3) / 255.0 * 100.0;
    }

    // ─── The button is there ───────────────────────────────────────────────────

    [Fact]
    public void TheSameFrameIsAPerfectMatch()
    {
        Assert.Equal(100, ScreenCapture.Similarity(Button(), Button(), null)!.Value, 6);
    }

    /// <summary>A hover highlight lifts the whole button; the letters are still the letters.</summary>
    [Fact]
    public void ABrighterCopyStillMatches()
    {
        var similarity = ScreenCapture.Similarity(Button(), Button(brighten: 40), null);
        Assert.True(similarity >= 97, $"brightened copy scored {similarity}");
    }

    // ─── The button is not there ───────────────────────────────────────────────

    /// <summary>
    /// The report. Inventory closed, dark world where the panel was: the old metric called it a
    /// match and the script clicked into the world on every tick.
    /// </summary>
    [Fact]
    public void ADarkWorldWhereTheButtonWasIsNotAMatch()
    {
        var reference = Button();
        var world = World();

        Assert.True(OldMeanDifferenceSimilarity(reference, world) >= 90,
            "the fixture no longer reproduces the false positive the old metric gave at the default 90 %");

        var similarity = ScreenCapture.Similarity(reference, world, null);
        Assert.True(similarity < 50, $"world scored {similarity} against the button");
    }

    /// <summary>
    /// The panel with no label on it, or a night sky: one flat dark tone. The old scale put that
    /// at ~95 % against the button; a flat frame now scores nothing, black frames included.
    /// </summary>
    [Fact]
    public void AFlatFrameScoresZero()
    {
        Assert.True(OldMeanDifferenceSimilarity(Button(), Flat(32)) >= 90);
        Assert.Equal(0, ScreenCapture.Similarity(Button(), Flat(32), null));
        Assert.Equal(0, ScreenCapture.Similarity(Button(), Flat(0), null));
    }

    // ─── Nothing to match on ───────────────────────────────────────────────────

    /// <summary>
    /// A black reference — captured while the game was fullscreen — matched every later black
    /// frame at 100 %. It is refused at capture time and, when already on disk, not loaded; here
    /// is the property both of those ask.
    /// </summary>
    [Fact]
    public void ABlankReferenceIsFlatAndComparesToNothing()
    {
        Assert.True(Flat(0).IsFlat);
        Assert.True(Flat(200).IsFlat);
        Assert.False(Button().IsFlat);
        Assert.False(World().IsFlat);

        Assert.Null(ScreenCapture.Similarity(Flat(0), Flat(0), null));
        Assert.Null(ScreenCapture.Similarity(Flat(0), Button(), null));
    }

    /// <summary>
    /// A uniform panel in a warm tint. With the channels pooled into one mean, the tint alone
    /// read as structure: not flat, and two blank frames of it correlated at 100 %.
    /// </summary>
    [Fact]
    public void ATintedBlankPanelIsStillBlank()
    {
        var tinted = new ScreenCapture(W, H, new byte[W * H * 4]);
        for (var i = 0; i < tinted.Bgra.Length; i += 4) { tinted.Bgra[i] = 25; tinted.Bgra[i + 1] = 30; tinted.Bgra[i + 2] = 40; }

        Assert.True(tinted.IsFlat);
        Assert.Null(ScreenCapture.Similarity(tinted, tinted, null));
        Assert.Equal(0, ScreenCapture.Similarity(Button(), tinted, null));
    }

    /// <summary>
    /// "Ignore background" that kept only the flat inside of an element leaves nothing to
    /// correlate. The sampler refuses such a mask; this is the check it asks.
    /// </summary>
    [Fact]
    public void FlatnessIsJudgedOnWhatTheMaskKeeps()
    {
        var panelOnly = new bool[W * H];
        for (var x = 0; x < W; x++) panelOnly[x] = true; // top row: panel only
        var withLabel = new bool[W * H];
        for (var p = 10 * W; p < 14 * W; p++) withLabel[p] = true; // the label rows

        Assert.True(Button().IsFlatUnder(panelOnly));
        Assert.False(Button().IsFlatUnder(withLabel));
        Assert.False(Button().IsFlatUnder(null));
    }

    [Fact]
    public void AnEmptyOrResizedCaptureComparesToNothing()
    {
        Assert.Null(ScreenCapture.Similarity(Button(), new ScreenCapture(0, 0, Array.Empty<byte>()), null));
        Assert.Null(ScreenCapture.Similarity(Button(), new ScreenCapture(W / 2, H, new byte[W / 2 * H * 4]), null));
        Assert.True(new ScreenCapture(0, 0, Array.Empty<byte>()).IsFlat);
    }

    // ─── The mask ──────────────────────────────────────────────────────────────

    /// <summary>
    /// "Ignore background" writes the world behind a HUD element off. With the glyph band kept
    /// and everything else masked, the world outside the band may do what it likes.
    /// </summary>
    [Fact]
    public void MaskedPixelsDoNotCount()
    {
        var reference = Button();
        var live = Button();
        var world = World(seed: 3);
        var mask = new bool[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var inBand = y is >= 8 and < 16;
                mask[y * W + x] = inBand;
                if (inBand) continue;
                var i = (y * W + x) * 4;
                Array.Copy(world.Bgra, i, live.Bgra, i, 4);
            }
        }

        Assert.True(ScreenCapture.Similarity(reference, live, null) < 100);
        Assert.Equal(100, ScreenCapture.Similarity(reference, live, mask)!.Value, 6);
    }

    /// <summary>A mask that keeps only flat panel leaves nothing to match on — null, not "always".</summary>
    [Fact]
    public void AMaskThatKeepsOnlyFlatPanelComparesToNothing()
    {
        var mask = new bool[W * H];
        for (var x = 0; x < W; x++) mask[x] = true; // top row: panel only

        Assert.Null(ScreenCapture.Similarity(Button(), Button(), mask));
        Assert.Null(ScreenCapture.Similarity(Button(), Button(), new bool[W * H]));
    }
}
