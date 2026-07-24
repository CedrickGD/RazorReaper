namespace RazorReaper.Services.Automation;

/// <summary>
/// Shared parsing of HotkeyField-style labels ("Alt + 1", "F6", "Space", "5") into a virtual-key
/// code plus modifiers, for automation scripts' bindable start/stop hotkeys. "Win"/"Meta"
/// combinations are rejected — the RegisterHotKey wrapper has no Win-key flag.
/// </summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Tab"] = 0x09,
        ["Backspace"] = 0x08,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E
    };

    public static bool IsModifierName(string token) =>
        token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Control", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Shift", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Win", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Meta", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a single key label ("5", "F", "F6", "Space", or the last token of a combo).</summary>
    public static bool TryParseKey(string? text, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var token = text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrEmpty(token) || IsModifierName(token)) return false;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        if ((token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = 0x70 + fn - 1;
            return true;
        }

        return NamedKeys.TryGetValue(token, out virtualKey);
    }

    /// <summary>Parses a full combination ("Alt + A", "Ctrl + Shift + F5") into modifiers + main key.</summary>
    public static bool TryParseHotkey(string? text, out int virtualKey, out bool ctrl, out bool alt, out bool shift)
    {
        virtualKey = 0;
        ctrl = alt = shift = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string? main = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                ctrl = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                alt = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                shift = true;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                return false;
            else
                main = part;
        }

        return main is not null && TryParseKey(main, out virtualKey);
    }
}
