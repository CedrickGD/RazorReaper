using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation;

/// <summary>
/// The Auto Clicker's start/stop hotkey, and the only place it is stored.
///
/// It used to live in the browser's localStorage, which only a running page can reach — so
/// the hotkeys page could list it but never edit it, and it was the one binding you still had
/// to go hunting for. Preferences is the same store the rest of the Auto Clicker's settings
/// already use (ac.hours, ac.holdms and friends) and any C# can read it, so
/// <see cref="HotkeyRegistry"/> can now own the binding like every other one.
///
/// Both halves are persisted: the display name for the UI, and the Win32 virtual-key code the
/// polling loop compares against, so nothing has to re-resolve the name on a hot path.
/// </summary>
public static class AutoClickerHotkey
{
    public const string DisplayPreferenceKey = "ac.hotkey";
    public const string CodePreferenceKey = "ac.hotkeycode";

    private const string DefaultDisplay = "F6";
    private const int DefaultCode = 0x75;

    /// <summary>Raised after a change so a page showing the hotkey can refresh.</summary>
    public static event Action? Changed;

    public static string Display => Preferences.Get(DisplayPreferenceKey, DefaultDisplay);

    public static int Code => Preferences.Get(CodePreferenceKey, DefaultCode);

    /// <summary>
    /// Stores a key by its display name. A name that resolves to no virtual-key code is
    /// rejected rather than saved, because an unusable binding would silently stop the
    /// hotkey working with nothing on screen to explain it.
    /// </summary>
    public static bool Set(string keyName)
    {
        var name = (keyName ?? "").Trim().ToUpperInvariant();
        var code = CodeFor(name);
        if (code == 0) return false;

        Preferences.Set(DisplayPreferenceKey, name);
        Preferences.Set(CodePreferenceKey, code);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Puts it back to F6, used when a capture produced nothing usable.</summary>
    public static void Reset()
    {
        Preferences.Set(DisplayPreferenceKey, DefaultDisplay);
        Preferences.Set(CodePreferenceKey, DefaultCode);
        Changed?.Invoke();
    }

    /// <summary>True once the hotkey has been stored here, so the one-off import runs once.</summary>
    public static bool HasStoredValue => Preferences.ContainsKey(DisplayPreferenceKey);

    public static int CodeFor(string keyName)
    {
        return keyName switch
        {
            // F Keys
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            "F13" => 0x7C,
            "F14" => 0x7D,
            "F15" => 0x7E,
            "F16" => 0x7F,
            "F17" => 0x80,
            "F18" => 0x81,
            "F19" => 0x82,
            "F20" => 0x83,
            "F21" => 0x84,
            "F22" => 0x85,
            "F23" => 0x86,
            "F24" => 0x87,

            // Letters
            "A" => 0x41,
            "B" => 0x42,
            "C" => 0x43,
            "D" => 0x44,
            "E" => 0x45,
            "F" => 0x46,
            "G" => 0x47,
            "H" => 0x48,
            "I" => 0x49,
            "J" => 0x4A,
            "K" => 0x4B,
            "L" => 0x4C,
            "M" => 0x4D,
            "N" => 0x4E,
            "O" => 0x4F,
            "P" => 0x50,
            "Q" => 0x51,
            "R" => 0x52,
            "S" => 0x53,
            "T" => 0x54,
            "U" => 0x55,
            "V" => 0x56,
            "W" => 0x57,
            "X" => 0x58,
            "Y" => 0x59,
            "Z" => 0x5A,

            // Numbers (top row)
            "0" => 0x30,
            "1" => 0x31,
            "2" => 0x32,
            "3" => 0x33,
            "4" => 0x34,
            "5" => 0x35,
            "6" => 0x36,
            "7" => 0x37,
            "8" => 0x38,
            "9" => 0x39,

            // Numpad
            "NUMPAD0" => 0x60,
            "NUMPAD1" => 0x61,
            "NUMPAD2" => 0x62,
            "NUMPAD3" => 0x63,
            "NUMPAD4" => 0x64,
            "NUMPAD5" => 0x65,
            "NUMPAD6" => 0x66,
            "NUMPAD7" => 0x67,
            "NUMPAD8" => 0x68,
            "NUMPAD9" => 0x69,
            "MULTIPLY" => 0x6A,
            "ADD" => 0x6B,
            "SEPARATOR" => 0x6C,
            "SUBTRACT" => 0x6D,
            "DECIMAL" => 0x6E,
            "DIVIDE" => 0x6F,

            // Special keys
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "RETURN" => 0x0D,
            "ESCAPE" => 0x1B,
            "ESC" => 0x1B,
            "TAB" => 0x09,
            "BACKSPACE" => 0x08,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            "SCROLLLOCK" => 0x91,

            // Modifiers
            "SHIFT" => 0x10,
            "SHIFTLEFT" => 0xA0,
            "SHIFTRIGHT" => 0xA1,
            "CONTROL" => 0x11,
            "CONTROLLEFT" => 0xA2,
            "CONTROLRIGHT" => 0xA3,
            "ALT" => 0x12,
            "ALTLEFT" => 0xA4,
            "ALTRIGHT" => 0xA5,

            // Arrow keys
            "UP" => 0x26,
            "ARROWUP" => 0x26,
            "DOWN" => 0x28,
            "ARROWDOWN" => 0x28,
            "LEFT" => 0x25,
            "ARROWLEFT" => 0x25,
            "RIGHT" => 0x27,
            "ARROWRIGHT" => 0x27,

            // Navigation
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,

            // Symbols
            "SEMICOLON" => 0xBA,
            "EQUAL" => 0xBB,
            "COMMA" => 0xBC,
            "MINUS" => 0xBD,
            "PERIOD" => 0xBE,
            "SLASH" => 0xBF,
            "BACKQUOTE" => 0xC0,
            "BRACKETLEFT" => 0xDB,
            "BACKSLASH" => 0xDC,
            "BRACKETRIGHT" => 0xDD,
            "QUOTE" => 0xDE,

            // Catch-all for single character keys
            _ when keyName.Length == 1 => keyName[0],

            _ => 0
        };
    }
}
