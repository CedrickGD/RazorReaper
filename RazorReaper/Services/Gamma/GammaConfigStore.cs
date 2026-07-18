using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Gamma;

/// <summary>
/// Loads / saves <see cref="GammaConfig"/> to %LOCALAPPDATA%\RazorReaper\gamma-config.json.
/// Ported from GammaHotkey/Services/ConfigStore.cs (new path + RazorReaper folder). Best-effort:
/// a corrupt or unreadable file falls back to defaults rather than throwing.
/// </summary>
public sealed class GammaConfigStore
{
    private readonly ILogger<GammaConfigStore>? _logger;
    private readonly string _dir;
    private readonly string _path;

    public GammaConfigStore(ILogger<GammaConfigStore>? logger = null)
    {
        _logger = logger;
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper");
        _path = Path.Combine(_dir, "gamma-config.json");
    }

    public string FilePath => _path;

    public GammaConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<GammaConfig>(json, GammaConfig.JsonOptions);
                if (cfg != null)
                    return Normalize(cfg);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Gamma config unreadable — falling back to defaults.");
        }
        return GammaConfig.CreateDefault();
    }

    public void Save(GammaConfig config)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string json = JsonSerializer.Serialize(config, GammaConfig.JsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Saving config is best-effort; never take the app down over it.
            _logger?.LogWarning(ex, "Failed to save gamma config.");
        }
    }

    /// <summary>Repairs ids/names and drops dangling references (handles partial files).</summary>
    private static GammaConfig Normalize(GammaConfig cfg)
    {
        cfg.Presets ??= new List<PresetConfig>();
        cfg.Cycle ??= new CycleConfig();
        cfg.Direct ??= new List<DirectBindingConfig>();
        cfg.SelectedMonitors ??= new List<string>();

        var seenIds = new HashSet<string>();
        foreach (var p in cfg.Presets)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || !seenIds.Add(p.Id))
            {
                p.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(p.Id);
            }
            if (string.IsNullOrWhiteSpace(p.Name))
                p.Name = "Preset";
            p.Value = GammaPresets.Clamp(p.Value);
        }

        if (cfg.Presets.Count == 0)
            cfg.Presets = GammaConfig.CreateDefault().Presets;

        // Drop direct bindings that point at a preset that no longer exists.
        var ids = new HashSet<string>(cfg.Presets.Select(p => p.Id));
        cfg.Direct = cfg.Direct.Where(b => ids.Contains(b.PresetId)).ToList();

        return cfg;
    }
}
