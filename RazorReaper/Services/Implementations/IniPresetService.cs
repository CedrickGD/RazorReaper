using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Implementation of IIniPresetService for managing ARK INI configuration presets.
/// </summary>
public class IniPresetService : IIniPresetService
{
    private readonly ILogger<IniPresetService> _logger;
    private readonly ITelemetryService _telemetryService;
    private readonly List<IniPreset> _presets;
    private readonly List<IniPreset> _customPresets = new();
    private readonly string _customPresetsPath;
    private readonly string _customImagesDir;
    private readonly object _presetLock = new();

    private const string CustomPresetsFileName = "custom-ini-presets.json";
    private const string CustomImagesFolderName = "PresetImages";
    private static readonly string[] AllowedImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public IniPresetService(ILogger<IniPresetService> logger, ITelemetryService telemetryService)
    {
        _logger = logger;
        _telemetryService = telemetryService;
        _presets = InitializePresets();
        _customPresetsPath = GetCustomPresetsPath();
        _customImagesDir = GetCustomImagesDir();
        LoadCustomPresets();
    }

    /// <inheritdoc/>
    public List<IniPreset> GetAllPresets()
    {
        try
        {
            _logger.LogDebug("Getting all INI presets");
            lock (_presetLock)
            {
                return new List<IniPreset>(_presets);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all presets");
            return new List<IniPreset>();
        }
    }

    /// <inheritdoc/>
    public IniPreset? GetPresetByName(string name)
    {
        try
        {
            _logger.LogDebug("Getting preset by name: {Name}", name);
            lock (_presetLock)
            {
                return _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preset by name: {Name}", name);
            return null;
        }
    }

    /// <inheritdoc/>
    public string GetPresetImagePath(string presetName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return "/images/presets/default.jpg";
            }

            var slug = ToSlug(presetName);

            // 1) User override wins. We base64-encode it so the WebView can load it without
            //    needing a writable wwwroot or a custom URI handler.
            var overridePath = FindOverrideImagePath(slug);
            if (overridePath != null)
            {
                var dataUrl = TryReadAsDataUrl(overridePath);
                if (dataUrl != null)
                {
                    return dataUrl;
                }
            }

            // 2) Bundled preset image (shipped under wwwroot/images/presets).
            var relativePath = $"/images/presets/{slug}.jpg";
            var physicalPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "presets", $"{slug}.jpg");

            return File.Exists(physicalPath) ? relativePath : "/images/presets/default.jpg";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image path for preset: {PresetName}", presetName);
            return "/images/presets/default.jpg";
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetPresetImageAsync(string presetName, Stream sourceStream, string extension)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName) || sourceStream == null)
            {
                return false;
            }

            var normalizedExt = NormalizeImageExtension(extension);
            if (normalizedExt == null)
            {
                _logger.LogWarning("Rejected preset image override with unsupported extension '{Ext}' for {Preset}", extension, presetName);
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_image_set",
                    TelemetryEventStatus.Degraded,
                    "Unsupported image extension.",
                    new Dictionary<string, object?> { ["preset_name"] = presetName, ["extension"] = extension });
                return false;
            }

            if (!Directory.Exists(_customImagesDir))
            {
                Directory.CreateDirectory(_customImagesDir);
            }

            var slug = ToSlug(presetName);

            // Remove any pre-existing override (could be a different extension).
            DeleteExistingOverrides(slug);

            var destPath = Path.Combine(_customImagesDir, $"{slug}{normalizedExt}");

            await using (var dest = File.Create(destPath))
            {
                await sourceStream.CopyToAsync(dest);
            }

            _ = _telemetryService.TrackEventAsync(
                "ini_preset_image_set",
                TelemetryEventStatus.Ok,
                "Preset image override saved.",
                new Dictionary<string, object?> { ["preset_name"] = presetName });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting custom image for preset {Preset}", presetName);
            _ = _telemetryService.TrackEventAsync(
                "ini_preset_image_set",
                TelemetryEventStatus.Down,
                ex.Message,
                new Dictionary<string, object?> { ["preset_name"] = presetName });
            return false;
        }
    }

    /// <inheritdoc/>
    public bool ResetPresetImage(string presetName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return false;
            }

            var slug = ToSlug(presetName);
            var removed = DeleteExistingOverrides(slug);

            if (removed)
            {
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_image_reset",
                    TelemetryEventStatus.Ok,
                    "Preset image override removed.",
                    new Dictionary<string, object?> { ["preset_name"] = presetName });
            }

            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting custom image for preset {Preset}", presetName);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool HasCustomImage(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return false;
        }

        try
        {
            return FindOverrideImagePath(ToSlug(presetName)) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> AddCustomPresetAsync(IniPreset preset)
    {
        try
        {
            if (!TryNormalizePreset(preset, out var normalized))
            {
                _logger.LogWarning("Custom preset rejected due to invalid data.");
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_add",
                    TelemetryEventStatus.Degraded,
                    "Custom preset rejected due to invalid data.");
                return false;
            }

            normalized.IsCustom = true;

            lock (_presetLock)
            {
                if (_presets.Any(p => p.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Preset name already exists: {Name}", normalized.Name);
                    _ = _telemetryService.TrackEventAsync(
                        "ini_preset_add",
                        TelemetryEventStatus.Degraded,
                        "Custom preset name already exists.",
                        new Dictionary<string, object?> { ["preset_name"] = normalized.Name });
                    return false;
                }

                _customPresets.Add(normalized);
                _presets.Add(normalized);
            }

            var saved = await SaveCustomPresetsAsync();
            if (!saved)
            {
                lock (_presetLock)
                {
                    _customPresets.Remove(normalized);
                    _presets.Remove(normalized);
                }
            }

            _ = _telemetryService.TrackEventAsync(
                "ini_preset_add",
                saved ? TelemetryEventStatus.Ok : TelemetryEventStatus.Down,
                saved ? "Custom preset saved." : "Custom preset save failed.",
                new Dictionary<string, object?> { ["preset_name"] = normalized.Name });

            return saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding custom preset");
            _ = _telemetryService.TrackEventAsync(
                "ini_preset_add",
                TelemetryEventStatus.Down,
                ex.Message);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveCustomPresetAsync(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_remove",
                    TelemetryEventStatus.Degraded,
                    "Custom preset remove rejected due to empty name.");
                return false;
            }

            IniPreset? target = null;

            lock (_presetLock)
            {
                target = _customPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    _ = _telemetryService.TrackEventAsync(
                        "ini_preset_remove",
                        TelemetryEventStatus.Degraded,
                        "Custom preset not found.",
                        new Dictionary<string, object?> { ["preset_name"] = name });
                    return false;
                }

                _customPresets.Remove(target);
                _presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            var saved = await SaveCustomPresetsAsync();
            if (!saved && target != null)
            {
                lock (_presetLock)
                {
                    _customPresets.Add(target);
                    _presets.Add(target);
                }
            }

            _ = _telemetryService.TrackEventAsync(
                "ini_preset_remove",
                saved ? TelemetryEventStatus.Ok : TelemetryEventStatus.Down,
                saved ? "Custom preset removed." : "Custom preset removal failed.",
                new Dictionary<string, object?> { ["preset_name"] = name });

            return saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing custom preset");
            _ = _telemetryService.TrackEventAsync(
                "ini_preset_remove",
                TelemetryEventStatus.Down,
                ex.Message,
                new Dictionary<string, object?> { ["preset_name"] = name });
            return false;
        }
    }

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

    private static string ToSlug(string name) =>
        name.Trim().ToLowerInvariant().Replace(" ", "-");

    private string? FindOverrideImagePath(string slug)
    {
        if (!Directory.Exists(_customImagesDir))
        {
            return null;
        }

        foreach (var ext in AllowedImageExtensions)
        {
            var candidate = Path.Combine(_customImagesDir, $"{slug}{ext}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? TryReadAsDataUrl(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/jpeg"
            };

            var bytes = File.ReadAllBytes(filePath);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read preset image override at {Path}", filePath);
            return null;
        }
    }

    private bool DeleteExistingOverrides(string slug)
    {
        if (!Directory.Exists(_customImagesDir))
        {
            return false;
        }

        var removedAny = false;
        foreach (var ext in AllowedImageExtensions)
        {
            var path = Path.Combine(_customImagesDir, $"{slug}{ext}");
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removedAny = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete preset image override at {Path}", path);
                }
            }
        }
        return removedAny;
    }

    private static string? NormalizeImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var ext = extension.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.'))
        {
            ext = "." + ext;
        }

        if (ext == ".jpeg")
        {
            ext = ".jpg";
        }

        return AllowedImageExtensions.Contains(ext) ? ext : null;
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

    private List<IniPreset> InitializePresets()
    {
        // Each preset's INI content is shipped as an embedded resource under
        // RazorReaper.Presets.<slug>.ini. See RazorReaper.csproj's <EmbeddedResource>
        // block and Resources/Presets/. To add/edit a preset you only need to drop
        // the .ini file in Resources/Presets/ and add an entry below.
        return new List<IniPreset>
        {
            BuildPreset("Default",           "default.ini",                "Game default."),
            BuildPreset("Super Hard",        "super-hard.ini",             "Max FPS, minimum visuals."),
            BuildPreset("Hard Black",        "hard-black.ini",             "Dark theme, perf-tuned."),
            BuildPreset("Hard Stalker",      "hard-stalker.ini",           "Long-range PvP visibility."),
            BuildPreset("Soft",              "soft.ini",                   "Balanced look and FPS."),
            BuildPreset("Black Spyglass",    "black-spyglass.ini",         "Dark with Spyglass tweaks."),
            BuildPreset("Contenant Creator", "contenant-creator.ini",      "Content creator tuning."),
            BuildPreset("Stalker",           "stalker.ini",                "Player/dino spotting."),
            BuildPreset("Black",             "black.ini",                  "Black tinted scene."),
            BuildPreset("Hard",              "hard.ini",                   "Raid-grade FPS."),
            BuildPreset("Clear Water Snow North", "clear-water-snow-north.ini", "Snow biome with clear water."),
            BuildPreset("Very Soft",         "very-soft.ini",              "Soft visuals, gentle FPS bump."),
        };
    }

    private static IniPreset BuildPreset(string name, string fileName, string description)
    {
        return new IniPreset
        {
            Name = name,
            Description = description,
            Content = LoadEmbeddedIni(fileName)
        };
    }

    private static string LoadEmbeddedIni(string fileName)
    {
        var asm = typeof(IniPresetService).Assembly;
        var resourceName = "RazorReaper.Presets." + fileName;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Defensive fallback so a missing resource doesn't crash the whole service.
            return $"; ERROR: preset resource '{resourceName}' missing from the assembly.";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
