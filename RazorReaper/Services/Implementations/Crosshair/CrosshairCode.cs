using System.Buffers.Text;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>Why a pasted code was refused. The page words each one; nothing here is user text.</summary>
internal enum CrosshairCodeError
{
    None,
    /// <summary>A CS2 (<c>CSGO-…</c>) or Valorant (<c>0;P;…</c>) code. Recognised only to say so — never decoded.</summary>
    GameCode,
    /// <summary>No <c>RR&lt;n&gt;-</c> prefix: not a RazorReaper code at all.</summary>
    NotACode,
    /// <summary>A later format (<c>RR2-</c>, or a <c>v</c> above ours) that this build cannot read.</summary>
    NewerVersion,
    /// <summary>Our prefix, but truncated, oversized, not base64url/DEFLATE/JSON, or a value of the wrong kind.</summary>
    Damaged,
}

/// <param name="Profile">The decoded look on success, otherwise null.</param>
/// <param name="Error"><see cref="CrosshairCodeError.None"/> on success.</param>
internal sealed record CrosshairCodeResult(CrosshairProfile? Profile, CrosshairCodeError Error);

/// <summary>
/// RazorReaper's own crosshair code: <c>RR1-</c> + unpadded base64url of raw-DEFLATE-compressed
/// compact JSON. Keys are short and never renamed, <c>v</c> is the schema version, and a field that
/// equals the model's default is left out — so a built-in preset is a few dozen characters.
///
/// <para><b>Look</b> — in the code, because the renderer reads it to draw the pixels: type,
/// colour, outline colour and thickness, size, thickness, gap, opacity, rotation, centre dot and
/// its size, the four line toggles, animation and speed, rainbow, pixel grid size and cells.</para>
///
/// <para><b>Local</b> — never in the code, because it belongs to one PC rather than to the shared
/// look: Id, Name, IsBuiltIn, the monitor, the X/Y offset, and the image path and scale. Decoding
/// keeps these from the profile the user already has. An image crosshair has no code at all: the
/// picture is a file on this PC and is not in the code.</para>
///
/// <para>Decoding trusts nothing: the text and the inflated bytes are both capped (a zip bomb
/// stops at the cap), every number is clamped to the editor's range, colours must be hex and
/// pixel cells 0/1, unknown keys are ignored and a missing key is the default. Every failure is
/// a <see cref="CrosshairCodeResult"/>, never an exception. The JSON closes itself, so a code cut
/// short either fails or — when only the stream's last padding bits went — still reads as the
/// whole look, never as part of one.</para>
/// </summary>
internal static class CrosshairCode
{
    public const string Prefix = "RR1-";
    private const int Version = 1;

    // A built-in preset is under 100 characters; a fully painted 64×64 grid, the largest look
    // there is, stays well inside both caps.
    private const int MaxCodeLength = 4000;
    private const int MaxJsonBytes = 8 * 1024;

    private static readonly CrosshairProfile Defaults = new();
    private static readonly Regex VersionPrefix = new(@"^RR(\d{1,6})-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // The renderer's own CrosshairColor.ParseHex rules: #rgb, #rrggbb or #aarrggbb, the # optional.
    private static readonly Regex HexColour = new(@"^#?(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\z", RegexOptions.CultureInvariant);
    private static readonly Regex PixelCells = new(@"^[01]{0,4096}\z", RegexOptions.CultureInvariant);

    // Ranges are the editor's (Crosshair.razor); PixelGridSize uses CrosshairProfile.Clone's.
    // Enums travel as their number, which is why their member order may never change.
    private static readonly (string Key, Func<CrosshairProfile, int> Get, Action<CrosshairProfile, int> Set, int Min, int Max)[] Ints =
    [
        ("t", p => (int)p.Type, (p, v) => p.Type = (CrosshairType)v, 0, (int)CrosshairType.Pixel),
        ("ot", p => p.OutlineThickness, (p, v) => p.OutlineThickness = v, 0, 8),
        ("s", p => p.Size, (p, v) => p.Size = v, 1, 150),
        ("th", p => p.Thickness, (p, v) => p.Thickness = v, 1, 20),
        ("g", p => p.Gap, (p, v) => p.Gap = v, -10, 60),
        ("op", p => p.Opacity, (p, v) => p.Opacity = v, 0, 100),
        ("r", p => p.Rotation, (p, v) => p.Rotation = v, 0, 359),
        ("ds", p => p.DotSize, (p, v) => p.DotSize = v, 1, 30),
        ("a", p => (int)p.Animation, (p, v) => p.Animation = (CrosshairAnimation)v, 0, (int)CrosshairAnimation.Rotate),
        ("as", p => p.AnimationSpeed, (p, v) => p.AnimationSpeed = v, 1, 10),
        ("pg", p => p.PixelGridSize, (p, v) => p.PixelGridSize = v, 4, 64),
    ];

    private static readonly (string Key, Func<CrosshairProfile, bool> Get, Action<CrosshairProfile, bool> Set)[] Bools =
    [
        ("d", p => p.ShowDot, (p, v) => p.ShowDot = v),
        ("lt", p => p.ShowTopLine, (p, v) => p.ShowTopLine = v),
        ("lb", p => p.ShowBottomLine, (p, v) => p.ShowBottomLine = v),
        ("ll", p => p.ShowLeftLine, (p, v) => p.ShowLeftLine = v),
        ("lr", p => p.ShowRightLine, (p, v) => p.ShowRightLine = v),
        ("rb", p => p.Rainbow, (p, v) => p.Rainbow = v),
    ];

    // Fallback is what an invalid stored value is written as, so a code handed out always pastes
    // back: white is what the renderer draws for a colour it cannot parse.
    private static readonly (string Key, Func<CrosshairProfile, string?> Get, Action<CrosshairProfile, string> Set, Regex Valid, string Fallback)[] Strings =
    [
        ("c", p => p.Color, (p, v) => p.Color = v, HexColour, "#FFFFFF"),
        ("o", p => p.OutlineColor, (p, v) => p.OutlineColor = v, HexColour, "#FFFFFF"),
        ("px", p => p.PixelArtData, (p, v) => p.PixelArtData = v, PixelCells, ""),
    ];

    /// <summary>An image crosshair cannot be a code: the picture is not in it.</summary>
    public static bool CanEncode(CrosshairProfile profile) => profile.Type != CrosshairType.Image;

    /// <summary>The code for <paramref name="profile"/>'s look, or null for an image crosshair.</summary>
    public static string? Encode(CrosshairProfile profile)
    {
        if (!CanEncode(profile)) return null;

        var json = new JsonObject { ["v"] = Version };
        foreach (var f in Ints)
        {
            var value = Math.Clamp(f.Get(profile), f.Min, f.Max);
            if (value != f.Get(Defaults)) json[f.Key] = value;
        }
        foreach (var f in Bools)
        {
            if (f.Get(profile) != f.Get(Defaults)) json[f.Key] = f.Get(profile);
        }
        foreach (var f in Strings)
        {
            var value = f.Get(profile) ?? "";
            if (!f.Valid.IsMatch(value)) value = f.Fallback;
            if (value != f.Get(Defaults)) json[f.Key] = value;
        }

        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize))
        {
            deflate.Write(Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        return Prefix + Base64Url.EncodeToString(packed.ToArray());
    }

    /// <summary>
    /// Read a pasted code. The look comes from the code; the local fields (monitor, offset,
    /// image) are copied from <paramref name="local"/> so a shared crosshair lands where the
    /// user's own one sits.
    /// </summary>
    public static CrosshairCodeResult Decode(string? code, CrosshairProfile local)
    {
        // Discord wraps a code posted as `code` in backticks; a copy often takes them along.
        var text = (code ?? "").Trim().Trim('`').Trim();

        var prefix = VersionPrefix.Match(text);
        if (!prefix.Success)
        {
            // A CS2 share code or a Valorant profile code (fields split by ';', which base64url
            // never contains). Recognised only to say so — never decoded.
            var gameCode = text.StartsWith("CSGO-", StringComparison.OrdinalIgnoreCase) || text.Contains(';');
            return Fail(gameCode ? CrosshairCodeError.GameCode : CrosshairCodeError.NotACode);
        }
        var codeVersion = int.Parse(prefix.Groups[1].Value);
        if (codeVersion != Version)
            return Fail(codeVersion > Version ? CrosshairCodeError.NewerVersion : CrosshairCodeError.Damaged);
        if (text.Length > MaxCodeLength) return Fail(CrosshairCodeError.Damaged);

        try
        {
            var packed = Base64Url.DecodeFromChars(text.AsSpan(prefix.Length));
            using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            var buffer = new byte[MaxJsonBytes + 1];
            var length = inflate.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (length > MaxJsonBytes) return Fail(CrosshairCodeError.Damaged);

            using var doc = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 2 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v)
                || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var schema))
                return Fail(CrosshairCodeError.Damaged);
            if (schema != Version)
                return Fail(schema > Version ? CrosshairCodeError.NewerVersion : CrosshairCodeError.Damaged);

            var profile = new CrosshairProfile
            {
                MonitorDeviceName = local.MonitorDeviceName,
                OffsetX = local.OffsetX,
                OffsetY = local.OffsetY,
                ImagePath = local.ImagePath,
                ImageScale = local.ImageScale,
            };

            foreach (var f in Ints)
            {
                if (!root.TryGetProperty(f.Key, out var e)) continue;
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var n)) return Fail(CrosshairCodeError.Damaged);
                f.Set(profile, Math.Clamp(n, f.Min, f.Max));
            }
            foreach (var f in Bools)
            {
                if (!root.TryGetProperty(f.Key, out var e)) continue;
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Fail(CrosshairCodeError.Damaged);
                f.Set(profile, e.GetBoolean());
            }
            foreach (var f in Strings)
            {
                if (!root.TryGetProperty(f.Key, out var e)) continue;
                if (e.ValueKind != JsonValueKind.String || !f.Valid.IsMatch(e.GetString()!)) return Fail(CrosshairCodeError.Damaged);
                f.Set(profile, e.GetString()!);
            }

            // The clamp lets 4 through; an image look has no picture to show.
            if (profile.Type == CrosshairType.Image) return Fail(CrosshairCodeError.Damaged);
            return new CrosshairCodeResult(profile, CrosshairCodeError.None);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            return Fail(CrosshairCodeError.Damaged);
        }
    }

    private static CrosshairCodeResult Fail(CrosshairCodeError error) => new(null, error);
}
