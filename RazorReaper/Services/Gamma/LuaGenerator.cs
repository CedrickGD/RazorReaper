using System.Globalization;
using System.Text;

namespace RazorReaper.Services.Gamma;

/// <summary>One selectable "G HUB key" — an F13..F24 key a Logitech Lua script can send
/// and a normal keyboard can't physically produce.</summary>
public sealed record GhubKeyOption(string Display, int VirtualKey);

/// <summary>
/// Virtual-key helpers: friendly display names, the lowercase key-name strings the G HUB /
/// LGS Lua API expects (for <c>PressKey</c>), and the F13–F24 picker list. Ported from
/// GammaHotkey/Services/KeyNames.cs; <see cref="DisplayName"/> reimplemented without WPF
/// (System.Windows.Input.KeyInterop is unavailable in a MAUI Blazor app).
/// </summary>
public static class KeyNames
{
    // Virtual-key codes we care about by name.
    public const int VK_ESCAPE = 0x1B;
    public const int VK_F13 = 0x7C;
    public const int VK_F24 = 0x87;

    private static readonly int[] ModifierVks =
    {
        0x10, 0x11, 0x12,             // SHIFT, CONTROL, MENU (generic)
        0xA0, 0xA1,                   // LSHIFT, RSHIFT
        0xA2, 0xA3,                   // LCONTROL, RCONTROL
        0xA4, 0xA5,                   // LMENU, RMENU
        0x5B, 0x5C,                   // LWIN, RWIN
        0x14, 0x90, 0x91,             // CAPSLOCK, NUMLOCK, SCROLLLOCK
    };

    /// <summary>True for keys we won't capture on their own (modifiers/locks).</summary>
    public static bool IsModifierOrLock(int vk) => Array.IndexOf(ModifierVks, vk) >= 0;

    /// <summary>The F13–F24 keys offered in the trigger picker for G HUB use.</summary>
    public static IReadOnlyList<GhubKeyOption> GHubKeyOptions { get; } = BuildGhubKeyOptions();

    private static GhubKeyOption[] BuildGhubKeyOptions()
    {
        var list = new List<GhubKeyOption>();
        for (int vk = VK_F13; vk <= VK_F24; vk++)
            list.Add(new GhubKeyOption($"F{13 + (vk - VK_F13)}", vk));
        return list.ToArray();
    }

    /// <summary>Friendly name for the UI, e.g. 124 -> "F13", 0x47 -> "G".</summary>
    public static string DisplayName(int vk)
    {
        if (vk == 0)
            return string.Empty;

        // F13..F24 are our headline keys.
        if (vk is >= VK_F13 and <= VK_F24)
            return $"F{13 + (vk - VK_F13)}";

        // F1..F12
        if (vk is >= 0x70 and <= 0x7B)
            return $"F{1 + (vk - 0x70)}";

        // A..Z
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)vk).ToString();

        // Top-row 0..9
        if (vk is >= 0x30 and <= 0x39)
            return ((char)vk).ToString();

        // Numpad 0..9
        if (vk is >= 0x60 and <= 0x69)
            return $"Num {vk - 0x60}";

        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK 0x{vk:X2}",
        };
    }

    /// <summary>
    /// The lowercase key-name string the G HUB Lua API accepts for <c>PressKey</c>, or
    /// <c>null</c> if the key can't be expressed (so the Lua exporter can skip it).
    /// </summary>
    public static string? GhubKeyName(int vk)
    {
        // F1..F24
        if (vk is >= 0x70 and <= 0x87)
            return $"f{1 + (vk - 0x70)}";

        // A..Z
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)('a' + (vk - 0x41))).ToString();

        // Top-row 0..9
        if (vk is >= 0x30 and <= 0x39)
            return ((char)('0' + (vk - 0x30))).ToString();

        // Numpad 0..9
        if (vk is >= 0x60 and <= 0x69)
            return $"num{vk - 0x60}";

        return vk switch
        {
            0x1B => "escape",
            0x20 => "spacebar",
            0x0D => "enter",
            0x09 => "tab",
            0x08 => "backspace",
            0x2D => "insert",
            0x2E => "delete",
            0x24 => "home",
            0x23 => "end",
            0x21 => "pageup",
            0x22 => "pagedown",
            0x26 => "up",
            0x28 => "down",
            0x25 => "left",
            0x27 => "right",
            _ => null,
        };
    }
}

/// <summary>
/// Generates a Logitech G HUB / LGS Lua script that maps mouse buttons to the keyboard
/// hotkeys the gamma feature listens for. Only keyboard triggers need G HUB; real
/// mouse-button triggers are detected directly. Pure string generation.
/// Ported from GammaHotkey/Services/LuaGenerator.cs.
/// </summary>
public static class LuaGenerator
{
    /// <summary>First mouse button number assigned to a hotkey (4 = "back" side button).</summary>
    private const int FirstButton = 4;

    public static string Generate(GammaConfig cfg)
    {
        var byId = cfg.Presets.ToDictionary(p => p.Id, p => p);

        // Collect the keyboard hotkeys that the ACTIVE mode actually listens for.
        var hotkeys = new List<(int Vk, string Description)>();

        if (cfg.Mode == TriggerMode.Cycle)
        {
            if (cfg.Cycle.Trigger.Kind == TriggerKind.Keyboard && !cfg.Cycle.Trigger.IsEmpty)
            {
                string steps = string.Join(" -> ", cfg.Presets.Where(p => p.InCycle).Select(p => p.Name));
                hotkeys.Add((cfg.Cycle.Trigger.VirtualKey, $"advance gamma cycle ({steps})"));
            }
        }
        else
        {
            foreach (var b in cfg.Direct)
            {
                if (b.Trigger.Kind == TriggerKind.Keyboard && !b.Trigger.IsEmpty
                    && byId.TryGetValue(b.PresetId, out var preset))
                {
                    string v = preset.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    hotkeys.Add((b.Trigger.VirtualKey, $"set gamma to {preset.Name} ({v})"));
                }
            }
        }

        var sb = new StringBuilder();
        AppendHeader(sb);

        sb.AppendLine("local bindings = {");
        sb.AppendLine("    -- [mouseButton] = \"hotkey\",   -- what it does");

        if (hotkeys.Count == 0)
        {
            sb.AppendLine("    -- No keyboard hotkeys are configured yet.");
            sb.AppendLine("    -- Add an F13-F24 trigger in the app, then re-generate this script.");
            sb.AppendLine("    -- Example (sends F13 when you press the back side button):");
            sb.AppendLine("    [4] = \"f13\",");
        }
        else
        {
            int button = FirstButton;
            foreach (var (vk, description) in hotkeys)
            {
                string? key = KeyNames.GhubKeyName(vk);
                if (key == null)
                {
                    sb.AppendLine($"    -- (skipped: \"{KeyNames.DisplayName(vk)}\" can't be sent from G HUB)");
                    continue;
                }
                sb.AppendLine($"    [{button}] = \"{key}\",   -- {description}");
                button++;
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();
        AppendBody(sb);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb)
    {
        sb.AppendLine("-- =====================================================================");
        sb.AppendLine("--  RazorReaper Gamma - generated G HUB / LGS Lua script");
        sb.AppendLine("--  Maps Logitech mouse buttons to the keyboard hotkeys RazorReaper");
        sb.AppendLine("--  listens for, so a button press changes your gamma - no NVIDIA");
        sb.AppendLine("--  Control Panel needed.");
        sb.AppendLine("--");
        sb.AppendLine("--  HOW TO USE");
        sb.AppendLine("--    1. In G HUB: your profile -> Assignments -> Scripting -> create/edit");
        sb.AppendLine("--       a Lua script (Help menu has the scripting API).");
        sb.AppendLine("--    2. Paste this whole script, then Save. Keep that profile active.");
        sb.AppendLine("--    3. Keep RazorReaper running with gamma \"Listening\" turned on.");
        sb.AppendLine("--");
        sb.AppendLine("--  Change the button numbers below to match the buttons you want:");
        sb.AppendLine("--    2=Right  3=Middle  4=Back(side)  5=Forward(side)  6+=extra buttons");
        sb.AppendLine("--  Tip: uncomment the OutputLogMessage line to print arg in the G HUB");
        sb.AppendLine("--  console so you can discover your own mouse's button numbers.");
        sb.AppendLine("-- =====================================================================");
        sb.AppendLine();
    }

    private static void AppendBody(StringBuilder sb)
    {
        sb.AppendLine("local HOLD_MS = 20   -- tiny hold so Windows/the app registers the keypress");
        sb.AppendLine();
        sb.AppendLine("local function sendKey(key)");
        sb.AppendLine("    PressKey(key)");
        sb.AppendLine("    Sleep(HOLD_MS)");
        sb.AppendLine("    ReleaseKey(key)");
        sb.AppendLine("end");
        sb.AppendLine();
        sb.AppendLine("function OnEvent(event, arg, family)");
        sb.AppendLine("    if event == \"PROFILE_ACTIVATED\" then");
        sb.AppendLine("        -- Uncomment ONLY if you bind the left button (button 1):");
        sb.AppendLine("        -- EnablePrimaryMouseButtonEvents(true)");
        sb.AppendLine("        OutputLogMessage(\"RazorReaper gamma script loaded.\\n\")");
        sb.AppendLine("    end");
        sb.AppendLine();
        sb.AppendLine("    if event == \"MOUSE_BUTTON_PRESSED\" then");
        sb.AppendLine("        -- OutputLogMessage(\"button %d\\n\", arg)  -- uncomment to find button numbers");
        sb.AppendLine("        local key = bindings[arg]");
        sb.AppendLine("        if key then");
        sb.AppendLine("            sendKey(key)");
        sb.AppendLine("        end");
        sb.AppendLine("    end");
        sb.AppendLine("end");
    }
}
