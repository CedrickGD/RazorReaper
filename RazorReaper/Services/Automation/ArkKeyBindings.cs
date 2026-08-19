using System.Text.RegularExpressions;

namespace RazorReaper.Services.Automation;

/// <summary>
/// The ARK actions the automation scripts drive. These are ARK's own <c>ActionName</c> /
/// <c>AxisName</c> strings, not RazorReaper hotkeys.
/// </summary>
public static class ArkActions
{
    /// <summary>Opens a container, station or dino inventory you are looking at.</summary>
    public const string AccessInventory = "AccessInventory";

    /// <summary>Opens your own inventory.</summary>
    public const string ShowMyInventory = "ShowMyInventory";

    /// <summary>Moves the hovered stack between the two open inventories.</summary>
    public const string TransferItem = "TransferItem";

    /// <summary>Generic "use" / interact.</summary>
    public const string Use = "Use";

    /// <summary>Craft-all inside an open station.</summary>
    public const string CraftAll = "CraftAll";

    /// <summary>Forward movement. An axis, not an action.</summary>
    public const string MoveForward = "MoveForward";

    /// <summary>Sprint. Held, not tapped.</summary>
    public const string Run = "Run";
}

/// <summary>
/// Reads which keys the player actually bound in ARK, so script defaults match their game instead
/// of ARK's factory layout.
///
/// This matters more than it looks: a script whose default lands on an unbound key starts happily,
/// reports "running", presses the key every tick and does absolutely nothing — indistinguishable
/// from a broken script. On a real install with a customised layout (Access inventory on F,
/// own inventory on Y, crouch on Left Alt) the stock defaults of C and I are bound to nothing at all.
/// </summary>
public interface IArkKeyBindingService
{
    /// <summary>
    /// The key the player bound to <paramref name="arkAction"/>, in the same label format the
    /// script settings and <see cref="HotkeyParser"/> use ("F", "Y", "0", "Space").
    /// Falls back to ARK's factory binding, then to <paramref name="fallback"/>.
    /// </summary>
    string Resolve(string arkAction, string fallback);

    /// <summary>True when the player's own Input.ini was found and parsed.</summary>
    bool HasPlayerBindings { get; }

    /// <summary>Drops the cache so the next lookup re-reads Input.ini.</summary>
    void Refresh();
}

/// <summary>
/// Pure parsing and key-name translation, split out from the service so it can be tested without
/// an ARK install, a file system or a DI container.
/// </summary>
public static class ArkKeyBindingParser
{
    // ActionMappings=(ActionName="AccessInventory",Key=F,bShift=False,...)
    private static readonly Regex ActionLine = new(
        """ActionMappings\s*=\s*\(\s*ActionName\s*=\s*"(?<name>[^"]+)"\s*,\s*Key\s*=\s*(?<key>[^,)\s]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // AxisMappings=(AxisName="MoveForward",Key=W,Scale=1.000000)
    private static readonly Regex AxisLine = new(
        """AxisMappings\s*=\s*\(\s*AxisName\s*=\s*"(?<name>[^"]+)"\s*,\s*Key\s*=\s*(?<key>[^,)\s]+)\s*,\s*Scale\s*=\s*(?<scale>-?[\d.]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ARK's factory bindings for the actions the scripts care about.</summary>
    public static readonly IReadOnlyDictionary<string, string> StockBindings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ArkActions.AccessInventory] = "E",
            [ArkActions.ShowMyInventory] = "I",
            [ArkActions.TransferItem] = "T",
            [ArkActions.Use] = "E",
            [ArkActions.CraftAll] = "A",
            [ArkActions.MoveForward] = "W",
            [ArkActions.Run] = "LeftShift",
        };

    /// <summary>
    /// Extracts action and axis bindings from Input.ini lines. Later lines win, which matches how
    /// Unreal itself resolves a config file. Gamepad and mouse bindings are ignored — a script can
    /// only synthesize keyboard keys, and keeping a controller binding would silently shadow the
    /// keyboard one the player actually uses.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string>? lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines is null) return result;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var action = ActionLine.Match(line);
            if (action.Success)
            {
                Record(result, action.Groups["name"].Value, action.Groups["key"].Value);
                continue;
            }

            var axis = AxisLine.Match(line);
            if (axis.Success)
            {
                // MoveForward has both W (+1) and S (-1); only the positive direction is "forward".
                if (!double.TryParse(axis.Groups["scale"].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var scale) || scale <= 0)
                {
                    continue;
                }

                Record(result, axis.Groups["name"].Value, axis.Groups["key"].Value);
            }
        }

        return result;
    }

    private static void Record(Dictionary<string, string> into, string name, string rawKey)
    {
        if (!TryTranslateKey(rawKey, out var translated)) return;
        into[name] = translated;
    }

    /// <summary>
    /// Translates an ARK key token into the label the script settings use. Returns false for
    /// anything a script cannot press — gamepad buttons, mouse buttons, "None" — so the caller
    /// keeps its own default rather than storing something unusable.
    /// </summary>
    public static bool TryTranslateKey(string? arkKey, out string label)
    {
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(arkKey)) return false;

        var key = arkKey.Trim();

        if (key.Equals("None", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.StartsWith("Gamepad", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.StartsWith("Global_", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Contains("MouseButton", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Contains("MouseScroll", StringComparison.OrdinalIgnoreCase)) return false;

        if (Named.TryGetValue(key, out var named))
        {
            label = named;
            return true;
        }

        // Single printable character: letters and digits come through as-is.
        if (key.Length == 1 && (char.IsLetterOrDigit(key[0])))
        {
            label = key.ToUpperInvariant();
            return true;
        }

        // Function keys.
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            label = "F" + fn.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static readonly IReadOnlyDictionary<string, string> Named =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Zero"] = "0",
            ["One"] = "1",
            ["Two"] = "2",
            ["Three"] = "3",
            ["Four"] = "4",
            ["Five"] = "5",
            ["Six"] = "6",
            ["Seven"] = "7",
            ["Eight"] = "8",
            ["Nine"] = "9",
            ["SpaceBar"] = "Space",
            ["Space"] = "Space",
            ["Enter"] = "Enter",
            ["BackSpace"] = "Backspace",
            ["Tab"] = "Tab",
            ["Escape"] = "Escape",
            ["Insert"] = "Insert",
            ["Delete"] = "Delete",
            ["Home"] = "Home",
            ["End"] = "End",
            ["PageUp"] = "PageUp",
            ["PageDown"] = "PageDown",
            // Held modifiers are legitimate targets — ARK puts sprint on Left Shift and crouch on
            // Left Alt. Only the side-specific names are mapped: bare "Shift"/"Ctrl"/"Alt" are
            // combo tokens elsewhere and must not turn into standalone keys here.
            ["LeftShift"] = "LeftShift",
            ["RightShift"] = "RightShift",
            ["LeftControl"] = "LeftControl",
            ["RightControl"] = "RightControl",
            ["LeftAlt"] = "LeftAlt",
            ["RightAlt"] = "RightAlt",
            ["Up"] = "Up",
            ["Down"] = "Down",
            ["Left"] = "Left",
            ["Right"] = "Right",
        };
}
