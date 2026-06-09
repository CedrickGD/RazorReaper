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
    private readonly IActivityService _activity;
    private readonly ITelemetryService _telemetry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CustomLabSettings _current = new();
    private bool _loaded;

    public CustomLabSettingsService(
        ILogger<CustomLabSettingsService> logger,
        IActivityService activity,
        ITelemetryService telemetry)
    {
        _logger = logger;
        _activity = activity;
        _telemetry = telemetry;
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
                    InvalidateStaleAcceptanceLocked();
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

    public Task SaveAsync() => SaveLockedAsync();

    private async Task SaveLockedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await PersistAsync();
        }
        finally
        {
            _gate.Release();
        }
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

    public Task SetAcceptedAsync(bool accepted) =>
        MutateAsync(s =>
        {
            s.Accepted = accepted;
            if (accepted)
            {
                s.AcceptedAt = DateTimeOffset.UtcNow;
                s.AcceptedAppVersion = CustomLabSettings.RequiredAcceptanceVersion;
            }
            else
            {
                s.AcceptedAt = null;
                s.AcceptedAppVersion = null;
            }
        },
        s => s.Accepted != accepted,
        () =>
        {
            _activity.AddActivity(accepted ? "Custom Lab: accepted Read Me" : "Custom Lab: revoked Read Me acceptance",
                                  accepted ? "info" : "warning");
            _ = _telemetry.TrackEventAsync("custom_lab.accepted",
                metrics: new Dictionary<string, object?> { ["value"] = accepted });
        });

    public Task SetMasterEnabledAsync(bool enabled) =>
        MutateAsync(s => s.MasterEnabled = enabled, s => s.MasterEnabled != enabled, () =>
        {
            _activity.AddActivity(enabled ? "Custom Lab enabled" : "Custom Lab disabled",
                                  enabled ? "warning" : "info");
            _ = _telemetry.TrackEventAsync("custom_lab.master_toggled",
                metrics: new Dictionary<string, object?> { ["value"] = enabled });
        });

    public Task SetGuardArkProcessAsync(bool guard) =>
        MutateAsync(s => s.GuardArkProcess = guard, s => s.GuardArkProcess != guard, null);

    public Task ResetAcknowledgementAsync() => SetAcceptedAsync(false);

    public Task MarkSkyInjectedAsync() =>
        MutateAsync(s => s.LastSkyInjectAt = DateTimeOffset.UtcNow, _ => true, null);

    public Task MarkSkyRestoredAsync() =>
        MutateAsync(s => s.LastSkyRestoreAt = DateTimeOffset.UtcNow, _ => true, null);

    public Task ClearSkyTimestampsAsync() =>
        MutateAsync(s =>
        {
            s.LastSkyInjectAt = null;
            s.LastSkyRestoreAt = null;
        }, s => s.LastSkyInjectAt is not null || s.LastSkyRestoreAt is not null, null);

    private async Task MutateAsync(Action<CustomLabSettings> apply, Func<CustomLabSettings, bool> changed, Action? sideEffect)
    {
        await _gate.WaitAsync();
        bool didChange;
        try
        {
            didChange = changed(_current);
            if (didChange)
            {
                apply(_current);
                await PersistAsync();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (didChange)
        {
            try { sideEffect?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Settings side-effect threw"); }
            Changed?.Invoke();
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

    // Caller must hold _gate.
    private void InvalidateStaleAcceptanceLocked()
    {
        if (!_current.Accepted) return;
        if (_current.AcceptedAppVersion == CustomLabSettings.RequiredAcceptanceVersion) return;

        // Read Me content has changed since the user last accepted. Re-lock the gate so
        // they have to re-read and re-accept. Master toggle stays as it was but feature
        // tabs will lock because they require Accepted == true.
        _logger.LogInformation(
            "Custom Lab acceptance invalidated: stamped {Stamped} != required {Required}",
            _current.AcceptedAppVersion ?? "(null)",
            CustomLabSettings.RequiredAcceptanceVersion);
        _current.Accepted = false;
        _current.AcceptedAt = null;
        _current.AcceptedAppVersion = null;
    }
}
