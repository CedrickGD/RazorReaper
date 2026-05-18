using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Disk-backed persistence for user-created custom INI presets. Reads/writes a single JSON file
/// under <c>LocalAppData/RazorReaper/Presets/</c>. Also owns the input-validation pass
/// (<see cref="TryNormalizePreset"/>) that runs whenever a preset comes in from the UI or from
/// a previously-saved file, so unsanitised data never reaches the in-memory list.
/// </summary>
public partial class IniPresetService
{
    private string GetCustomPresetsPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "RazorReaper", "Presets", CustomPresetsFileName);
    }

    private static string GetCustomImagesDir()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "RazorReaper", CustomImagesFolderName);
    }

    private void LoadCustomPresets()
    {
        try
        {
            if (!File.Exists(_customPresetsPath))
            {
                return;
            }

            var json = File.ReadAllText(_customPresetsPath);
            var presets = JsonSerializer.Deserialize<List<IniPreset>>(json) ?? new List<IniPreset>();

            lock (_presetLock)
            {
                foreach (var preset in presets)
                {
                    if (!TryNormalizePreset(preset, out var normalized))
                    {
                        continue;
                    }

                    normalized.IsCustom = true;

                    if (_presets.Any(p => p.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    _customPresets.Add(normalized);
                    _presets.Add(normalized);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading custom presets");
        }
    }

    private async Task<bool> SaveCustomPresetsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_customPresetsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<IniPreset> snapshot;
            lock (_presetLock)
            {
                snapshot = new List<IniPreset>(_customPresets);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_customPresetsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving custom presets");
            return false;
        }
    }

    private static bool TryNormalizePreset(IniPreset? preset, out IniPreset normalized)
    {
        normalized = new IniPreset();

        if (preset == null)
        {
            return false;
        }

        var name = preset.Name?.Trim() ?? string.Empty;
        var content = preset.Content ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        normalized = new IniPreset
        {
            Name = name,
            Description = preset.Description?.Trim() ?? string.Empty,
            Content = content
        };

        return true;
    }
}
