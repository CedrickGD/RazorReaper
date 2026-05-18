using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Provides ARK INI presets to the UI: a fixed catalog of built-ins (shipped as embedded
/// resources) plus user-created custom presets persisted to LocalAppData. Also owns the
/// per-preset image override pipeline so the editor can swap thumbnails.
///
/// Implementation is split across partial files so each concern stays readable:
///  • <c>IniPresetService.cs</c> — fields, ctor, preset list CRUD (you are here).
///  • <c>IniPresetService.Images.cs</c> — per-preset image override read/write.
///  • <c>IniPresetService.Persistence.cs</c> — load/save of the user-custom preset JSON.
///
/// The built-in catalog itself lives in <see cref="IniPresetCatalog"/>.
/// </summary>
public partial class IniPresetService : IIniPresetService
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
        _presets = IniPresetCatalog.BuildAll();
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

    private static string ToSlug(string name) =>
        name.Trim().ToLowerInvariant().Replace(" ", "-");
}
