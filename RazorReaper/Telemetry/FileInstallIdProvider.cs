using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Telemetry;

public sealed class FileInstallIdProvider : IInstallIdProvider
{
    private readonly ILogger<FileInstallIdProvider> _logger;
    private readonly string _telemetryFolder;
    private readonly string _installIdPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InstallIdentity? _cachedIdentity;

    public FileInstallIdProvider(ILogger<FileInstallIdProvider> logger)
    {
        _logger = logger;
        _telemetryFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper",
            "Telemetry");
        _installIdPath = Path.Combine(_telemetryFolder, "install_id.json");
    }

    public async Task<InstallIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedIdentity != null)
        {
            return _cachedIdentity;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedIdentity != null)
            {
                return _cachedIdentity;
            }

            _cachedIdentity = await LoadOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
            return _cachedIdentity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InstallIdentity> LoadOrCreateIdentityAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_installIdPath))
        {
            try
            {
                var rawJson = await File.ReadAllTextAsync(_installIdPath, cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<InstallIdDocument>(rawJson);
                if (document != null
                    && Guid.TryParse(document.InstallId, out var parsedGuid)
                    && parsedGuid != Guid.Empty)
                {
                    return new InstallIdentity(parsedGuid.ToString(), false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read install ID from {Path}. A new ID will be generated.", _installIdPath);
            }
        }

        var newInstallId = Guid.NewGuid().ToString();
        var newDocument = new InstallIdDocument
        {
            InstallId = newInstallId,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        try
        {
            Directory.CreateDirectory(_telemetryFolder);
            var tempPath = $"{_installIdPath}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(newDocument);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _installIdPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist install ID to {Path}.", _installIdPath);
        }

        return new InstallIdentity(newInstallId, true);
    }

    private sealed class InstallIdDocument
    {
        public string InstallId { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
    }
}
