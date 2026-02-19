using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Telemetry;

public sealed class FileTelemetryStateStore : ITelemetryStateStore
{
    private readonly ILogger<FileTelemetryStateStore> _logger;
    private readonly string _telemetryFolder;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileTelemetryStateStore(ILogger<FileTelemetryStateStore> logger)
    {
        _logger = logger;
        _telemetryFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper",
            "Telemetry");
        _statePath = Path.Combine(_telemetryFolder, "telemetry_state.json");
    }

    public async Task<TelemetryState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_statePath))
            {
                return new TelemetryState();
            }

            try
            {
                var rawJson = await File.ReadAllTextAsync(_statePath, cancellationToken).ConfigureAwait(false);
                var state = JsonSerializer.Deserialize<TelemetryState>(rawJson);
                return state ?? new TelemetryState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read telemetry state from {Path}. Using defaults.", _statePath);
                return new TelemetryState();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(TelemetryState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_telemetryFolder);
            var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(state);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _statePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write telemetry state to {Path}.", _statePath);
        }
        finally
        {
            _gate.Release();
        }
    }
}
