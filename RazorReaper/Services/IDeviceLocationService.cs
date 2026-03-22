namespace RazorReaper.Services;

public interface IDeviceLocationService
{
    Task<DeviceLocationSnapshot?> GetBestEffortLocationAsync(CancellationToken cancellationToken = default);
}

public sealed record DeviceLocationSnapshot(
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    DateTimeOffset CapturedAtUtc,
    string Source,
    string? SignalSource);
