using System.Globalization;
using System.Numerics;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>Which share-code dialect a pasted string was recognised as.</summary>
internal enum CrosshairCodeFormat
{
    Unknown,
    Valorant,
    SourceShareCode,
    Rust,
}

/// <summary>
/// Outcome of a code import. Failures carry a message that names the field that could not be
/// read, because "couldn't recognise that code" tells the user nothing about what to fix.
/// </summary>
/// <param name="Format">The dialect the input was recognised as, even when parsing then failed.</param>
/// <param name="Profile">The imported profile on success, otherwise null.</param>
/// <param name="Error">A user-facing, single-sentence explanation on failure, otherwise null.</param>
internal sealed record CrosshairCodeParseResult(CrosshairCodeFormat Format, CrosshairProfile? Profile, string? Error)
{
    public bool Success => Profile != null;

    public static CrosshairCodeParseResult Ok(CrosshairCodeFormat format, CrosshairProfile profile)
        => new(format, profile, null);

    public static CrosshairCodeParseResult Fail(CrosshairCodeFormat format, string error)
        => new(format, null, error);
}

/// <summary>
/// Decoders for the crosshair share formats the editor accepts.
///
/// <para><b>Valorant profile code</b> — e.g. <c>0;P;c;5;u;FFFFFFFF;h;0;0l;4;0o;2;0a;1;1b;0</c>.
/// A semicolon-separated token stream: an optional leading profile index, then general keys, then
/// the <c>P</c> (primary), <c>A</c> (ADS) and <c>S</c> (sniper) sections. Inside a section a key is
/// either a bare letter (primary settings) or a digit-prefixed pair where <c>0</c> is the inner
/// line and <c>1</c> the outer line — <c>0l</c> is inner length, <c>1t</c> outer thickness.
/// Key meanings follow genesy/crosshair-codes (<c>src/codegenerator.ts</c>), whose mapping enums
/// are the most complete public description of the format.</para>
///
/// <para><b>CS2 / CS:GO share code</b> — e.g. <c>CSGO-LibdP-VCVEd-ESayK-rSivi-2UBtG</c>.
/// 25 characters in five groups, base-57 over an alphabet that omits the ambiguous glyphs, decoded
/// big-endian into 18 bytes with a checksum in byte 0. The byte layout below is the one implemented
/// identically by akiver/csgo-sharecode (<c>src/index.ts</c>, with six round-tripped test vectors)
/// and by saul's C# decoder gist — the two agree field for field, and this file is tested against
/// akiver's published vectors.</para>
///
/// <para><b>Rust</b> — Rust's own crosshair export produces a code too, but Facepunch has never
/// published its encoding and there is no reference decoder to check an implementation against, so
/// guessing at it would silently produce a wrong crosshair. Rust codes are reported as unsupported
/// with a pointer at the Crosshair X import, which does work for Rust.</para>
///
/// Unrecognised keys are ignored rather than zeroed, so a code from a newer game build still
/// imports the fields we do understand.
/// </summary>
internal static class CrosshairCodeParsers
{
    /// <summary>Detect the format and parse, layering onto <paramref name="basis"/>.</summary>
    public static CrosshairCodeParseResult TryParse(string code, CrosshairProfile basis)
    {
        if (string.IsNullOrWhiteSpace(code))
            return CrosshairCodeParseResult.Fail(CrosshairCodeFormat.Unknown, "Paste a crosshair code first.");

        var trimmed = code.Trim();

        if (trimmed.StartsWith("CSGO", StringComparison.OrdinalIgnoreCase))
            return TryParseShareCode(trimmed, basis);

        // A Valorant code is the only one built out of semicolon-separated tokens.
        if (trimmed.Contains(';'))
            return TryParseValorantCode(trimmed, basis);

        return CrosshairCodeParseResult.Fail(
            CrosshairCodeFormat.Unknown,
            "Not a crosshair code we can read. Expected a Valorant profile code (semicolon-separated, "
            + "e.g. \"0;P;c;5;0l;4;…\") or a CS2/CS:GO share code starting with \"CSGO-\". "
            + "Rust's own export codes are not supported — import the Crosshair X workshop file instead.");
    }

    /// <summary>Legacy shape kept for callers that only care about the profile.</summary>
    public static CrosshairProfile? TryParseOrNull(string code, CrosshairProfile basis)
        => TryParse(code, basis).Profile;

    private static CrosshairProfile NewImport(CrosshairProfile basis, string name)
    {
        var profile = basis.Clone();
        profile.Id = Guid.NewGuid().ToString("N");
        profile.IsBuiltIn = false;
        profile.Name = name;
        profile.Type = CrosshairType.Cross;
        profile.Animation = CrosshairAnimation.None;
        profile.Rainbow = false;
        profile.ImagePath = null;
        profile.Rotation = 0;
        return profile;
    }

    // ─── Valorant ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Preset colours of Valorant's crosshair colour dropdown, index 0..7. Index 8 means "custom",
    /// in which case the <c>u</c> key carries an RRGGBBAA hex value.
    /// </summary>
    private static readonly string[] ValorantPresetColors =
    {
        "#FFFFFF", // 0 white
        "#00FF00", // 1 green
        "#7FFF00", // 2 yellow green
        "#DFFF00", // 3 green yellow
        "#FFFF00", // 4 yellow
        "#00FFFF", // 5 cyan
        "#FF00FF", // 6 pink
        "#FF0000", // 7 red
    };

    public static CrosshairCodeParseResult TryParseValorantCode(string code, CrosshairProfile basis)
    {
        const CrosshairCodeFormat fmt = CrosshairCodeFormat.Valorant;
        if (string.IsNullOrWhiteSpace(code))
            return CrosshairCodeParseResult.Fail(fmt, "Paste a Valorant crosshair code first.");

        var tokens = code.Split(';');

        // section → key → value. First occurrence wins: a few codes in the wild are two profiles
        // concatenated, and Valorant itself reads the first one.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "";        // "" = the general keys before the first section marker
        string? profileName = null;
        var sawSection = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0) continue;

            if (token is "P" or "A" or "S")
            {
                section = token;
                sawSection = true;
                continue;
            }
            if (token == "NAME")
            {
                if (i + 1 < tokens.Length) profileName = tokens[++i].Trim('"');
                continue;
            }

            // The very first token is the profile index (0..4) and carries no value.
            if (i == 0 && token.Length <= 2 && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                continue;

            if (i + 1 >= tokens.Length)
                return CrosshairCodeParseResult.Fail(fmt, $"Valorant code ends after \"{token}\" with no value for it.");

            var key = $"{section}:{token}";
            var value = tokens[++i];
            if (!values.ContainsKey(key)) values[key] = value;
        }

        if (!sawSection)
            return CrosshairCodeParseResult.Fail(
                fmt, "Valorant code has no \"P\" section — copy the whole string from Settings › Crosshair › Export profile code.");

        string? Primary(string key) => values.TryGetValue($"P:{key}", out var v) ? v : null;

        var profile = NewImport(basis, profileName is { Length: > 0 } ? profileName : "Valorant import");

        // ── colour. `c` selects the dropdown entry; 8 means "custom", and only then does `u`
        // (RRGGBBAA) apply. A code that carries `u` with no `c` at all comes from a generator that
        // omits defaults — honour the custom colour there rather than silently importing white.
        var colorIndex = Primary("c");
        var custom = Primary("u");
        if (colorIndex != null)
        {
            if (!int.TryParse(colorIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ci) || ci is < 0 or > 8)
                return CrosshairCodeParseResult.Fail(fmt, $"Valorant code: colour \"c\" is \"{colorIndex}\", expected 0-8.");
            if (ci < ValorantPresetColors.Length) profile.Color = ValorantPresetColors[ci];
        }
        if (custom != null && (colorIndex == null || colorIndex == "8"))
        {
            var hex = ParseValorantHex(custom);
            if (hex == null)
                return CrosshairCodeParseResult.Fail(fmt, $"Valorant code: custom colour \"u\" is \"{custom}\", expected 6 or 8 hex digits.");
            profile.Color = hex;
        }

        // ── outline. `h` toggles it (Valorant default: on), `t` is its thickness. Reading `t`
        // without honouring `h` used to draw an outline on codes that explicitly turned it off.
        var outlinesOn = Primary("h") != "0";
        var outlineThickness = 1;
        var outlineRaw = Primary("t");
        if (outlineRaw != null)
        {
            if (!int.TryParse(outlineRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out outlineThickness))
                return CrosshairCodeParseResult.Fail(fmt, $"Valorant code: outline thickness \"t\" is \"{outlineRaw}\", expected a whole number.");
        }
        profile.OutlineThickness = outlinesOn ? Math.Clamp(outlineThickness, 1, 8) : 0;

        // ── inner lines. These are what our Cross renders: `0l` length, `0t` thickness,
        // `0o` offset from the centre, `0a` opacity, `0b` visibility. The outer line pair (`1…`)
        // has no equivalent in our model and is ignored.
        if (!TryReadInt(values, "P:0l", "inner line length \"0l\"", out var innerLen, out var err))
            return CrosshairCodeParseResult.Fail(fmt, err!);
        if (innerLen.HasValue) profile.Size = Math.Clamp(innerLen.Value, 1, 150);

        if (!TryReadInt(values, "P:0t", "inner line thickness \"0t\"", out var innerThick, out err))
            return CrosshairCodeParseResult.Fail(fmt, err!);
        if (innerThick.HasValue) profile.Thickness = Math.Clamp(innerThick.Value, 1, 20);

        if (!TryReadInt(values, "P:0o", "inner line offset \"0o\"", out var innerOffset, out err))
            return CrosshairCodeParseResult.Fail(fmt, err!);
        if (innerOffset.HasValue) profile.Gap = Math.Clamp(innerOffset.Value, -10, 60);

        // Body opacity comes from the inner lines' own alpha `0a`. The bare `a` key is the CENTRE
        // DOT's opacity — reading that as the whole crosshair's alpha turned every code with a
        // faint dot into a near-invisible crosshair.
        if (!TryReadDouble(values, "P:0a", "inner line opacity \"0a\"", out var innerAlpha, out err))
            return CrosshairCodeParseResult.Fail(fmt, err!);
        if (innerAlpha.HasValue) profile.Opacity = Math.Clamp((int)Math.Round(innerAlpha.Value * 100.0), 0, 100);

        // `0b` is the inner lines' visibility. `0v` (vertical length) and `0g` (length unlinked)
        // are NOT visibility flags — treating them as such hid every crosshair with `0v;0`.
        var showInner = Primary("0b") != "0";
        profile.ShowTopLine = profile.ShowBottomLine = profile.ShowLeftLine = profile.ShowRightLine = showInner;

        // ── centre dot. `d` toggles, `z` is its thickness (a diameter), our DotSize is a radius.
        var showDot = Primary("d") == "1";
        profile.ShowDot = showDot;
        if (!TryReadDouble(values, "P:z", "centre dot thickness \"z\"", out var dotThickness, out err))
            return CrosshairCodeParseResult.Fail(fmt, err!);
        if (dotThickness.HasValue)
            profile.DotSize = Math.Clamp((int)Math.Round(dotThickness.Value / 2.0, MidpointRounding.AwayFromZero), 1, 30);

        // A code with the lines switched off and the dot on is a dot crosshair; render it as one
        // so the editor shows the right controls.
        if (!showInner && showDot) profile.Type = CrosshairType.Dot;

        return CrosshairCodeParseResult.Ok(fmt, profile);
    }

    private static string? ParseValorantHex(string raw)
    {
        var hex = raw.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length is not (6 or 8)) return null;
        for (var i = 0; i < 6; i++)
        {
            if (!Uri.IsHexDigit(hex[i])) return null;
        }
        return "#" + hex[..6].ToUpperInvariant();
    }

    private static bool TryReadInt(Dictionary<string, string> values, string key, string label, out int? result, out string? error)
    {
        result = null;
        error = null;
        if (!values.TryGetValue(key, out var raw)) return true;
        // Some exporters write whole numbers with a decimal part ("4.0"); accept those.
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            result = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            return true;
        }
        error = $"Valorant code: {label} is \"{raw}\", expected a number.";
        return false;
    }

    private static bool TryReadDouble(Dictionary<string, string> values, string key, string label, out double? result, out string? error)
    {
        result = null;
        error = null;
        if (!values.TryGetValue(key, out var raw)) return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            result = d;
            return true;
        }
        error = $"Valorant code: {label} is \"{raw}\", expected a number.";
        return false;
    }

    // ─── CS2 / CS:GO share code ───────────────────────────────────────────────

    /// <summary>
    /// Base-57 alphabet of the share code. It leaves out the glyph pairs people mistype:
    /// uppercase I, lowercase g and l, and the digits 0 and 1.
    /// </summary>
    private const string ShareCodeAlphabet = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";

    private const int ShareCodeCharCount = 25;
    private const int ShareCodeByteCount = 18;

    /// <summary>
    /// <c>cl_crosshaircolor</c> presets. 5 selects the custom <c>cl_crosshaircolor_r/g/b</c> triple.
    /// </summary>
    private static readonly string[] SourcePresetColors =
    {
        "#FF0000", // 0 red
        "#00FF00", // 1 green
        "#FFFF00", // 2 yellow
        "#0000FF", // 3 blue
        "#00FFFF", // 4 cyan
    };

    public static CrosshairCodeParseResult TryParseShareCode(string code, CrosshairProfile basis)
    {
        const CrosshairCodeFormat fmt = CrosshairCodeFormat.SourceShareCode;

        var body = code.Trim();
        if (body.StartsWith("CSGO", StringComparison.OrdinalIgnoreCase)) body = body[4..];
        body = body.Replace("-", "").Replace(" ", "");

        if (body.Length != ShareCodeCharCount)
            return CrosshairCodeParseResult.Fail(
                fmt, $"CS2 share code: expected {ShareCodeCharCount} characters after \"CSGO-\", found {body.Length}.");

        BigInteger num = 0;
        for (var i = body.Length - 1; i >= 0; i--)
        {
            var c = body[i];
            var idx = ShareCodeAlphabet.IndexOf(c);
            if (idx < 0)
                return CrosshairCodeParseResult.Fail(
                    fmt, $"CS2 share code: \"{c}\" is not a share-code character (I, l, g, 0 and 1 never appear in one).");
            num = num * ShareCodeAlphabet.Length + idx;
        }

        // Big-endian: the first code character carries the most significant digits, so the
        // little-endian extraction below is reversed back into format order.
        var bytes = new byte[ShareCodeByteCount];
        for (var i = 0; i < ShareCodeByteCount; i++)
        {
            bytes[i] = (byte)(num & 0xFF);
            num >>= 8;
        }
        Array.Reverse(bytes);

        // Byte 0 is a checksum over the rest. It is also what tells a crosshair share code apart
        // from a match share code, which uses the same alphabet and length.
        var checksum = 0;
        for (var i = 1; i < ShareCodeByteCount; i++) checksum += bytes[i];
        if (bytes[0] != (byte)(checksum % 256))
            return CrosshairCodeParseResult.Fail(
                fmt, "CS2 share code: checksum doesn't match — the code is mistyped, or it is a match share code rather than a crosshair one.");

        //  1     format version (1)
        //  2     cl_crosshairgap                          signed, ×10
        //  3     cl_crosshair_outlinethickness             ×2
        //  4..6  cl_crosshaircolor_r / _g / _b
        //  7     cl_crosshairalpha
        //  8     cl_crosshair_dynamic_splitdist (low 3 bits) | cl_crosshair_recoil (bit 7)
        //  9     cl_fixedcrosshairgap                      signed, ×10
        // 10     cl_crosshaircolor (low 3 bits) | drawoutline (bit 3) | splitalpha_innermod (high nibble)
        // 11     splitalpha_outermod (low nibble) | maxdist_splitratio (high nibble)
        // 12     cl_crosshairthickness                     ×10
        // 13     cl_crosshairstyle << 1 | dot (bit 4) | gap_useweaponvalue (bit 5)
        //        | usealpha (bit 6) | cl_crosshair_t (bit 7)
        // 14     cl_crosshairsize                          ×10
        // 15..17 reserved, zero
        var gap = (sbyte)bytes[2] / 10.0;
        var outline = bytes[3] / 2.0;
        var r = bytes[4];
        var g = bytes[5];
        var b = bytes[6];
        var alpha = bytes[7];
        var colorPreset = bytes[10] & 0x07;
        var outlineEnabled = (bytes[10] & 0x08) != 0;
        var thickness = bytes[12] / 10.0;
        var centerDot = ((bytes[13] >> 4) & 1) == 1;
        var alphaEnabled = ((bytes[13] >> 4) & 4) == 4;
        var tStyle = ((bytes[13] >> 4) & 8) == 8;
        var length = bytes[14] / 10.0;

        var profile = NewImport(basis, "CS2 import");

        profile.Color = colorPreset < SourcePresetColors.Length
            ? SourcePresetColors[colorPreset]
            : $"#{r:X2}{g:X2}{b:X2}";

        // cl_crosshairalpha only applies when cl_crosshairusealpha is on; otherwise the crosshair
        // is opaque and the stored alpha is a leftover.
        profile.Opacity = alphaEnabled
            ? Math.Clamp((int)Math.Round(alpha / 255.0 * 100.0), 1, 100)
            : 100;

        // Source's crosshair cvars are not pixels — the game scales them with the display. The
        // factors below are the ones the app's own "CS Classic" preset already encodes
        // (cl_crosshairsize 5 / thickness 0.5 / gap -1 → Size 10, Thickness 1, Gap 4), so an
        // imported default CS crosshair lands on top of that preset instead of a third of its size.
        profile.Size = Math.Clamp((int)Math.Round(length * 2.0, MidpointRounding.AwayFromZero), 1, 150);
        profile.Thickness = Math.Clamp((int)Math.Round(thickness * 2.0, MidpointRounding.AwayFromZero), 1, 20);
        profile.Gap = Math.Clamp((int)Math.Round(gap, MidpointRounding.AwayFromZero) + 5, 0, 60);
        profile.OutlineThickness = outlineEnabled
            ? Math.Clamp((int)Math.Round(outline, MidpointRounding.AwayFromZero), 1, 8)
            : 0;
        profile.ShowDot = centerDot;
        profile.Type = tStyle ? CrosshairType.TStyle : CrosshairType.Cross;
        profile.ShowTopLine = profile.ShowBottomLine = profile.ShowLeftLine = profile.ShowRightLine = true;

        return CrosshairCodeParseResult.Ok(fmt, profile);
    }

    // Describe() lived here and built the import summary out of English words. It is worded on
    // the Crosshair page now — this file is a pure decoder with no localizer, and the page was
    // its only caller.
}
