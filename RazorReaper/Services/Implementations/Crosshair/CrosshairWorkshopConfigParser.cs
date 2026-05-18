using System.Text.Json;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Best-effort parser for crosshair config files found inside Crosshair-X workshop bundles.
/// Accepts either JSON (any structure — keys are flattened) or simple key=value/INI text.
/// Each recognised key is applied as a clamped mutation on the supplied profile; unknown keys
/// are silently ignored so a future-proofed workshop bundle still imports cleanly.
///
/// Sibling to <see cref="CrosshairCodeParsers"/>, which handles in-game share codes
/// (Valorant / CS2). The two formats have no overlap.
/// </summary>
internal static class CrosshairWorkshopConfigParser
{
    /// <summary>Read <paramref name="configPath"/> and apply any recognised settings to
    /// <paramref name="profile"/> in place. Silent on parse failure — returns false so the
    /// caller can log without aborting the broader workshop import.</summary>
    public static bool TryApplyConfig(string configPath, CrosshairProfile profile, out Exception? error)
    {
        error = null;
        try
        {
            var text = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Try JSON first; fall back to simple key=value/INI parsing.
            Dictionary<string, string>? kv = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                kv = FlattenJson(doc.RootElement);
            }
            catch { /* not JSON — fine */ }

            kv ??= ParseKeyValue(text);

            if (kv.TryGetValue("color", out var color)) profile.Color = NormalizeColor(color) ?? profile.Color;
            if (kv.TryGetValue("outlinecolor", out var oc)) profile.OutlineColor = NormalizeColor(oc) ?? profile.OutlineColor;
            if (kv.TryGetValue("outline", out var ot) && int.TryParse(ot, out var oti)) profile.OutlineThickness = Math.Clamp(oti, 0, 6);
            if (kv.TryGetValue("size", out var sz) && int.TryParse(sz, out var szi)) profile.Size = Math.Clamp(szi, 1, 200);
            if (kv.TryGetValue("length", out var ln) && int.TryParse(ln, out var lni)) profile.Size = Math.Clamp(lni, 1, 200);
            if (kv.TryGetValue("thickness", out var th) && int.TryParse(th, out var thi)) profile.Thickness = Math.Clamp(thi, 1, 20);
            if (kv.TryGetValue("gap", out var gp) && int.TryParse(gp, out var gpi)) profile.Gap = Math.Clamp(gpi, 0, 100);
            if (kv.TryGetValue("opacity", out var op) && int.TryParse(op, out var opi)) profile.Opacity = Math.Clamp(opi, 0, 100);
            if (kv.TryGetValue("rotation", out var rt) && int.TryParse(rt, out var rti)) profile.Rotation = ((rti % 360) + 360) % 360;
            if (kv.TryGetValue("dot", out var dt) && bool.TryParse(dt, out var dtb)) profile.ShowDot = dtb;
            if (kv.TryGetValue("centerdot", out var cd) && bool.TryParse(cd, out var cdb)) profile.ShowDot = cdb;
            if (kv.TryGetValue("dotsize", out var ds) && int.TryParse(ds, out var dsi)) profile.DotSize = Math.Clamp(dsi, 1, 50);
            if (kv.TryGetValue("type", out var ty)) profile.Type = ParseType(ty) ?? profile.Type;
            if (kv.TryGetValue("style", out var st)) profile.Type = ParseType(st) ?? profile.Type;
            if (kv.TryGetValue("name", out var nm) && !string.IsNullOrWhiteSpace(nm)) profile.Name = nm.Trim();
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static Dictionary<string, string> FlattenJson(JsonElement el)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(el);
        return dict;

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(prop.Value);
                    else
                        dict[prop.Name] = prop.Value.ToString() ?? "";
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Walk(item);
            }
        }
    }

    private static Dictionary<string, string> ParseKeyValue(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
            var eq = line.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            dict[key] = value;
        }
        return dict;
    }

    private static string? NormalizeColor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("#")) return raw;
        if (raw.Length is 6 or 8 && raw.All(c => Uri.IsHexDigit(c))) return "#" + raw;
        // rgb(r,g,b)
        if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var inner = raw[(raw.IndexOf('(') + 1)..raw.LastIndexOf(')')];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        return null;
    }

    private static CrosshairType? ParseType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "cross" or "classic" or "default" or "plus" => CrosshairType.Cross,
            "dot" or "point" => CrosshairType.Dot,
            "circle" or "ring" or "o" => CrosshairType.Circle,
            "t" or "tstyle" or "t-style" => CrosshairType.TStyle,
            "image" or "custom" or "png" => CrosshairType.Image,
            _ => null
        };
    }
}
