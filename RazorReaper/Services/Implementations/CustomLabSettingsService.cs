using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

public class CustomLabSettingsService : ICustomLabSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly ILogger<CustomLabSettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CustomLabSettings _current = new();
    private bool _loaded;

    public CustomLabSettingsService(ILogger<CustomLabSettingsService> logger)
    {
        _logger = logger;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper");
        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "custom-lab.json");
    }

    public CustomLabSettings Current => _current;

    public event Action? Changed;

    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loaded) return;

            if (!File.Exists(_filePath))
            {
                _loaded = true;
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_filePath);
                var loaded = JsonSerializer.Deserialize<CustomLabSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    _current = Migrate(loaded);
                }
                _loaded = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load custom lab settings from {Path}; quarantining", _filePath);
                try
                {
                    var quarantine = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    File.Move(_filePath, quarantine, overwrite: false);
                }
                catch (Exception quarantineEx)
                {
                    _logger.LogWarning(quarantineEx, "Failed to quarantine corrupt custom-lab.json");
                }
                _current = new CustomLabSettings();
                _loaded = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public Task MarkSkyInjectedAsync() =>
        MutateAsync(s => s.LastSkyInjectAt = DateTimeOffset.UtcNow);

    public Task MarkSkyRestoredAsync() =>
        MutateAsync(s => s.LastSkyRestoreAt = DateTimeOffset.UtcNow);

    private async Task MutateAsync(Action<CustomLabSettings> apply)
    {
        await _gate.WaitAsync();
        try
        {
            apply(_current);
            await PersistAsync();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    // Caller must hold _gate.
    private async Task PersistAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            // Atomic-ish on Windows: a power-cut between WriteAllText and Move leaves the previous file intact.
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save custom lab settings to {Path}", _filePath);
        }
    }

    private static CustomLabSettings Migrate(CustomLabSettings loaded)
    {
        // Future migrations: switch on loaded.SchemaVersion and upgrade in place.
        if (loaded.SchemaVersion == CustomLabSettings.CurrentSchemaVersion)
            return loaded;

        // Unknown future version — keep the data but stamp it as current so we don't churn.
        loaded.SchemaVersion = CustomLabSettings.CurrentSchemaVersion;
        return loaded;
    }
}
