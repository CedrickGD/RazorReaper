using System.Text.Json;
using System.Text.Json.Serialization;

namespace RazorReaper.Services.Gamma;

// =====================================================================
//  Ported from GammaHotkey/Models/GammaPresets.cs + Triggers.cs + AppConfig.cs
//  Pure serializable models + range constants. No WPF, no UI.
// =====================================================================

/// <summary>The six named gamma levels.</summary>
public enum GammaLevel
{
    Low,
    Mid,
    Normal,
    Higher,
    High,
    Max,
}

/// <summary>Range + default values for the gamma presets.</summary>
public static class GammaPresets
{
    /// <summary>Lowest selectable gamma. Not 0.0 (a black screen) — the slider floor.</summary>
    public const double Min = 0.10;

    /// <summary>Highest selectable gamma (spec: up to 2.5).</summary>
    public const double Max = 2.50;

    /// <summary>Neutral / Windows default. Produces an identity ramp.</summary>
    public const double Default = 1.00;

    /// <summary>Slider / stepper granularity.</summary>
    public const double Step = 0.05;

    public static readonly IReadOnlyList<GammaLevel> AllLevels = new[]
    {
        GammaLevel.Low,
        GammaLevel.Mid,
        GammaLevel.Normal,
        GammaLevel.Higher,
        GammaLevel.High,
        GammaLevel.Max,
    };

    /// <summary>Sensible starting values spanning the full range (Low -> Max climbs).</summary>
    public static double DefaultValue(GammaLevel level) => level switch
    {
        GammaLevel.Low => 0.50,
        GammaLevel.Mid => 0.75,
        GammaLevel.Normal => 1.00,
        GammaLevel.Higher => 1.40,
        GammaLevel.High => 1.90,
        GammaLevel.Max => 2.50,
        _ => 1.00,
    };

    public static string DisplayName(GammaLevel level) => level switch
    {
        GammaLevel.Low => "Low",
        GammaLevel.Mid => "Mid",
        GammaLevel.Normal => "Normal",
        GammaLevel.Higher => "Higher",
        GammaLevel.High => "High",
        GammaLevel.Max => "Max",
        _ => level.ToString(),
    };

    public static double Clamp(double value) => Math.Clamp(value, Min, Max);
}

/// <summary>What kind of physical input fires a trigger.</summary>
public enum TriggerKind
{
    Keyboard,
    Mouse,
}

/// <summary>The mouse buttons we bind. Left/Right are intentionally excluded so normal
/// clicking is never hijacked.</summary>
public enum MouseButton
{
    Middle,
    XButton1, // "Mouse 4" — the back side-button
    XButton2, // "Mouse 5" — the forward side-button
}

/// <summary>
/// A single trigger source: either a keyboard virtual-key (e.g. 0x7C = F13, which a
/// Logitech G HUB Lua script sends), or one of the bindable mouse buttons. Value-equality
/// lets it be used directly as a dictionary / hash-set key.
/// </summary>
public readonly record struct TriggerInput(TriggerKind Kind, int VirtualKey, MouseButton Button)
{
    public static TriggerInput Key(int vk) => new(TriggerKind.Keyboard, vk, default);

    public static TriggerInput Mouse(MouseButton button) => new(TriggerKind.Mouse, 0, button);

    public bool IsEmpty => Kind == TriggerKind.Keyboard && VirtualKey == 0;

    public static readonly TriggerInput None = new(TriggerKind.Keyboard, 0, default);

    /// <summary>Human-friendly label, e.g. "F13", "Mouse 4", "G".</summary>
    public string Describe()
    {
        if (IsEmpty)
            return string.Empty;

        return Kind switch
        {
            TriggerKind.Mouse => Button switch
            {
                MouseButton.Middle => "Mouse 3",
                MouseButton.XButton1 => "Mouse 4",
                MouseButton.XButton2 => "Mouse 5",
                _ => "Mouse",
            },
            _ => KeyNames.DisplayName(VirtualKey),
        };
    }

    public override string ToString() => Describe();
}

/// <summary>Which trigger style is currently active.</summary>
public enum TriggerMode
{
    Cycle,
    Direct,
}

/// <summary>One user preset: a stable id, an editable name, a value, and cycle membership.</summary>
public sealed class PresetConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; } = 1.0;
    public bool InCycle { get; set; }
}

public sealed class CycleConfig
{
    public TriggerInput Trigger { get; set; } = TriggerInput.None;
    public bool Wrap { get; set; } = true;
}

public sealed class DirectBindingConfig
{
    public TriggerInput Trigger { get; set; } = TriggerInput.None;
    public string PresetId { get; set; } = string.Empty;
}

/// <summary>Root persisted configuration
/// (written to %LOCALAPPDATA%\RazorReaper\gamma-config.json).</summary>
public sealed class GammaConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<PresetConfig> Presets { get; set; } = new();
    public TriggerMode Mode { get; set; } = TriggerMode.Cycle;
    public CycleConfig Cycle { get; set; } = new();
    public List<DirectBindingConfig> Direct { get; set; } = new();
    public bool ApplyToAllMonitors { get; set; } = true;
    public List<string> SelectedMonitors { get; set; } = new();

    /// <summary>Whether to start listening for triggers automatically on launch.</summary>
    public bool Listening { get; set; }

    public static GammaConfig CreateDefault()
    {
        var cfg = new GammaConfig { Version = CurrentVersion };

        var ids = new Dictionary<GammaLevel, string>();
        foreach (var level in GammaPresets.AllLevels)
        {
            string id = Guid.NewGuid().ToString("N");
            ids[level] = id;
            bool inCycle = level is GammaLevel.Normal or GammaLevel.Higher or GammaLevel.High or GammaLevel.Max;
            cfg.Presets.Add(new PresetConfig
            {
                Id = id,
                Name = GammaPresets.DisplayName(level),
                Value = GammaPresets.DefaultValue(level),
                InCycle = inCycle,
            });
        }

        // Out of the box: F13 cycles Normal -> Higher -> High -> Max.
        cfg.Cycle.Trigger = TriggerInput.Key(KeyNames.VK_F13);

        // ...and a couple of direct examples on F14 / F15.
        cfg.Direct.Add(new DirectBindingConfig { Trigger = TriggerInput.Key(KeyNames.VK_F13 + 1), PresetId = ids[GammaLevel.Normal] });
        cfg.Direct.Add(new DirectBindingConfig { Trigger = TriggerInput.Key(KeyNames.VK_F13 + 2), PresetId = ids[GammaLevel.Max] });

        return cfg;
    }

    public static JsonSerializerOptions JsonOptions { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        opts.Converters.Add(new TriggerInputJsonConverter());
        return opts;
    }
}

/// <summary>
/// Serializes a <see cref="TriggerInput"/> as a tagged object:
/// <c>{ "kind": "keyboard", "vk": 124 }</c> or <c>{ "kind": "mouse", "button": "XButton1" }</c>.
/// </summary>
public sealed class TriggerInputJsonConverter : JsonConverter<TriggerInput>
{
    public override TriggerInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return TriggerInput.None;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for TriggerInput.");

        string kind = "keyboard";
        int vk = 0;
        MouseButton button = MouseButton.XButton1;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string prop = reader.GetString() ?? string.Empty;
            reader.Read();
            switch (prop.ToLowerInvariant())
            {
                case "kind":
                    kind = reader.GetString() ?? "keyboard";
                    break;
                case "vk":
                    vk = reader.GetInt32();
                    break;
                case "button":
                    Enum.TryParse(reader.GetString(), ignoreCase: true, out button);
                    break;
            }
        }

        return kind.Equals("mouse", StringComparison.OrdinalIgnoreCase)
            ? TriggerInput.Mouse(button)
            : TriggerInput.Key(vk);
    }

    public override void Write(Utf8JsonWriter writer, TriggerInput value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Kind == TriggerKind.Mouse)
        {
            writer.WriteString("kind", "mouse");
            writer.WriteString("button", value.Button.ToString());
        }
        else
        {
            writer.WriteString("kind", "keyboard");
            writer.WriteNumber("vk", value.VirtualKey);
        }
        writer.WriteEndObject();
    }
}
