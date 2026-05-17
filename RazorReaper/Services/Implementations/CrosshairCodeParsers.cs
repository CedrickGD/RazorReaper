using System.Globalization;
using System.Numerics;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Best-effort decoders for two community crosshair-code formats:
///  • Valorant in-game crosshair string  (e.g. <c>0;P;c;5;u;FFFFFFFF;h;0;m;1;0l;4;0o;2;…</c>)
///  • CS2 / CSGO share code               (e.g. <c>CSGO-aBcDe-FgHiJ-kLmNo-pQrSt-uVwXy</c>)
///
/// Neither format is officially documented. The implementations here cover the fields the
/// editor exposes (color, outline, length, thickness, gap, dot, t-style); rarer flags
/// (dynamic split, deadzone, etc.) are ignored. If an unfamiliar field shows up,
/// we keep the user's existing setting for it rather than zeroing.
/// </summary>
internal static class CrosshairCodeParsers
{
    /// <summary>Detect format and parse, layering onto <paramref name="basis"/>. Returns null if unrecognised.</summary>
    public static CrosshairProfile? TryParse(string code, CrosshairProfile basis)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();

        if (trimmed.StartsWith("CSGO-", StringComparison.OrdinalIgnoreCase))
            return TryParseCsgoShareCode(trimmed, basis);

        // Valorant codes always include `;P;` (primary) somewhere; usually start with `0;P;`.
        if (trimmed.Contains(";P;", StringComparison.Ordinal) || trimmed.StartsWith("P;", StringComparison.Ordinal))
            return TryParseValorantCode(trimmed, basis);

        // Last-resort: try Valorant — its parser fails gracefully on garbage.
        return TryParseValorantCode(trimmed, basis);
    }

    // ─── Valorant ─────────────────────────────────────────────────────────────

    public static CrosshairProfile? TryParseValorantCode(string code, CrosshairProfile basis)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var profile = basis.Clone();
        profile.Id = Guid.NewGuid().ToString("N");
        profile.IsBuiltIn = false;
        profile.Name = "Valorant import";
        profile.Type = CrosshairType.Cross;
        profile.Animation = CrosshairAnimation.None;
        profile.Rainbow = false;
        profile.ImagePath = null;

        // Token stream — semicolon separated. Notable shapes:
        //   <profile_idx>;P;<key>;<val>;…;A;<key>;<val>;…;S;<key>;<val>;…
        // - <profile_idx> is one or two digits at the very start (usually 0).
        // - P/A/S are section markers (Primary / Aim Down Sights / Sniper).
        // - <key> may be a bare letter (c, t, h, a, o, d) OR a 2-char "<line><letter>" where the
        //   first char is 0 (inner) or 1 (outer): 0l = inner length, 1t = outer thickness, etc.
        //   Earlier we mistakenly split "0l" into section "0" + key "l" — that's why every inner-
        //   line value (length, thickness, offset = our Size / Thickness / Gap) was silently lost.
        var tokens = code.Split(';');
        char section = 'P';
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool sawPrimary = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (string.IsNullOrWhiteSpace(t)) continue;

            // Profile index at the very start — single digit, just skip it.
            if (i == 0 && t.Length <= 2 && int.TryParse(t, out _)) continue;

            // Section markers
            if (t.Length == 1 && (t[0] is 'P' or 'A' or 'S' or 'D'))
            {
                section = t[0];
                if (section == 'P') sawPrimary = true;
                continue;
            }

            // Key + value pair
            if (i + 1 >= tokens.Length) break;
            var key = t;
            var val = tokens[i + 1];
            i++;
            dict[$"{section}:{key}"] = val;
        }

        // Helpers — prefer Primary section, fall back to plain key, never split "0x" prefixes.
        string? Get(string k) =>
            dict.TryGetValue($"P:{k}", out var v) ? v :
            dict.TryGetValue(k, out var v2) ? v2 : null;
        string? GetIn(char sec, string k) =>
            dict.TryGetValue($"{sec}:{k}", out var v) ? v : null;

        // Custom color: 'u' is 8-char RRGGBBAA. 'c' (0..7 known presets, 8 = "custom").
        var u = Get("u");
        if (!string.IsNullOrEmpty(u) && (u.Length == 8 || u.Length == 6))
        {
            var rgb = u.Length == 8 ? u[..6] : u;
            profile.Color = "#" + rgb.ToUpperInvariant();
        }
        else if (int.TryParse(Get("c"), out var preset))
        {
            profile.Color = ValorantPresetColor(preset);
        }

        // Outline: 'h' = show outline (bool), 't' = outline thickness (int 0..3), 'o' = outline opacity 0..1.
        if (Get("h") == "1") profile.OutlineThickness = Math.Max(profile.OutlineThickness, 1);
        else if (Get("h") == "0") profile.OutlineThickness = 0;
        if (int.TryParse(Get("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ot))
            profile.OutlineThickness = Math.Clamp(ot, 0, 8);

        // Body opacity 'a' is a float 0..1 in Valorant — earlier I parsed it as int 1..10 which made
        // a fully-opaque crosshair (a;1) come out at 10% opacity → near-invisible.
        if (double.TryParse(Get("a"), NumberStyles.Float, CultureInfo.InvariantCulture, out var aFloat))
            profile.Opacity = Math.Clamp((int)Math.Round(aFloat * 100.0), 0, 100);

        // Inner lines — bare keys "0l", "0t", "0o" (NOT section-prefixed).
        if (int.TryParse(Get("0l"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var innerLen))
            profile.Size = Math.Clamp(innerLen, 1, 150);
        if (int.TryParse(Get("0t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var innerThick))
            profile.Thickness = Math.Clamp(innerThick, 1, 20);
        if (int.TryParse(Get("0o"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var innerOffset))
            profile.Gap = Math.Clamp(innerOffset, -10, 60);

        // Inner show ('0g'/'0v' depending on Valorant version) — if explicitly disabled, hide all lines.
        if (Get("0g") == "0" || Get("0v") == "0")
        {
            profile.ShowTopLine = profile.ShowBottomLine = profile.ShowLeftLine = profile.ShowRightLine = false;
        }
        else
        {
            profile.ShowTopLine = profile.ShowBottomLine = profile.ShowLeftLine = profile.ShowRightLine = true;
        }

        // Center dot — Valorant exposes it via 'd' / 'z' or the 'S' (sniper) section. Most exports use 'd'.
        var dotShow = Get("d") ?? GetIn('S', "d");
        if (dotShow == "1") profile.ShowDot = true;
        else if (dotShow == "0") profile.ShowDot = false;
        if (int.TryParse(Get("z"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dotSize))
            profile.DotSize = Math.Clamp(dotSize, 1, 30);

        // Acceptance test — we need at least one recognisable Valorant field, otherwise we'd be
        // returning a defaulted profile for any garbage string.
        bool recognised = sawPrimary || Get("c") != null || Get("0l") != null || Get("u") != null;
        return recognised ? profile : null;
    }

    private static string ValorantPresetColor(int preset) => preset switch
    {
        0 => "#FFFFFF",   // white
        1 => "#00FF00",   // green
        2 => "#7FFF00",   // yellow-green
        3 => "#ADFF2F",   // green-yellow
        4 => "#FFFF00",   // yellow
        5 => "#00FFFF",   // cyan
        6 => "#FF00FF",   // pink
        7 => "#FF0000",   // red
        _ => "#FFFFFF"
    };

    // ─── CS2 / CSGO ───────────────────────────────────────────────────────────

    private const string CsgoAlphabet = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";

    public static CrosshairProfile? TryParseCsgoShareCode(string code, CrosshairProfile basis)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim().Replace("-", "");
        if (trimmed.StartsWith("CSGO", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..];

        // Standard CSGO share codes encode 18 bytes in 25 base-57 chars (CSGO + 5×5 groups).
        if (trimmed.Length < 20 || trimmed.Length > 32) return null;

        BigInteger num = 0;
        for (int i = trimmed.Length - 1; i >= 0; i--)
        {
            var c = trimmed[i];
            var idx = CsgoAlphabet.IndexOf(c);
            if (idx < 0) return null;
            num = num * 57 + idx;
        }

        // Extract 18 bytes. The bigint multiplication encoded the *first* code character into the
        // highest bits of `num`, so the data is logically big-endian: byte[0] (checksum) ends up in
        // the top byte, byte[17] (data tail) in the bottom. Our LE extraction loop reverses that —
        // hence the explicit Array.Reverse to put bytes back in format order.
        var bytes = new byte[18];
        for (int i = 0; i < 18; i++)
        {
            bytes[i] = (byte)(num & 0xFF);
            num >>= 8;
        }
        Array.Reverse(bytes);

        // bytes[0] is the parity byte; payload starts at index 1.
        var profile = basis.Clone();
        profile.Id = Guid.NewGuid().ToString("N");
        profile.IsBuiltIn = false;
        profile.Name = "CSGO import";
        profile.Type = CrosshairType.Cross;
        profile.Animation = CrosshairAnimation.None;
        profile.Rainbow = false;
        profile.ImagePath = null;

        // Field layout (verified against xertioN's share code CSGO-YM3TK-…-TsKjC):
        //   0:   checksum
        //   1:   color-preset bits + use_alpha bit
        //   2:   cl_crosshairalpha
        //   3..5: cl_crosshaircolor_r/_g/_b
        //   6:   cl_crosshair_dynamic_splitdist
        //   7:   cl_crosshair_dynamic_splitalpha_innermod × 10
        //   8:   cl_crosshairgap × 10 (signed)
        //   9..12: more dynamic-split / split-ratio fields (ignored)
        //   13:  bits — 0=drawoutline, 1=t-style, 2=dot, 3=gap_useweapon, 4..7=style
        //   14:  cl_crosshairsize × 10
        //   15:  cl_crosshairthickness × 10
        //   16:  cl_crosshair_outlinethickness × 10
        //   17:  reserved
        var colorPreset = bytes[1] & 0x07;
        var useAlpha = (bytes[1] & 0x10) != 0;
        var alpha = bytes[2];
        var r = bytes[3];
        var g = bytes[4];
        var b = bytes[5];
        var gap = (sbyte)bytes[8] / 10.0;
        var flags = bytes[13];
        var size = bytes[14] / 10.0;
        var thickness = bytes[15] / 10.0;
        var outline = bytes[16] / 10.0;

        bool drawOutline = (flags & 0x01) != 0;
        bool tStyle = (flags & 0x02) != 0;
        bool dot = (flags & 0x04) != 0;

        // Prefer the custom R/G/B when any are non-zero — many pro codes set preset to a sentinel
        // (0/1) but still carry a real custom RGB. Falling back to the preset table for the all-zero
        // case keeps the standard 0..4 presets working too.
        bool hasCustom = r != 0 || g != 0 || b != 0;
        profile.Color = hasCustom
            ? $"#{r:X2}{g:X2}{b:X2}"
            : colorPreset switch
            {
                0 => "#00FF00",
                1 => "#FF0000",
                2 => "#FFFF00",
                3 => "#0000FF",
                4 => "#00FFFF",
                _ => "#FFFFFF",
            };
        // Most codes set cl_crosshair_usealpha=0 with alpha=0 (meaning "ignore"). Treat that as
        // fully opaque rather than invisible.
        profile.Opacity = (useAlpha || alpha > 0)
            ? Math.Clamp((int)Math.Round(Math.Max(alpha, (byte)1) / 255.0 * 100.0), 1, 100)
            : 100;
        profile.Size = Math.Clamp((int)Math.Round(size), 1, 150);
        profile.Thickness = Math.Clamp((int)Math.Round(thickness), 1, 20);
        profile.Gap = Math.Clamp((int)Math.Round(gap), -10, 60);
        profile.OutlineThickness = drawOutline ? Math.Max(1, (int)Math.Round(outline)) : 0;
        profile.ShowDot = dot;
        profile.Type = tStyle ? CrosshairType.TStyle : CrosshairType.Cross;

        return profile;
    }
}
