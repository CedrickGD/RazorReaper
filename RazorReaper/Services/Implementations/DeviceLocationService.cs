using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
#if WINDOWS
using Windows.Devices.Geolocation;
#endif

namespace RazorReaper.Services.Implementations;

public sealed class DeviceLocationService : IDeviceLocationService
{
    private static readonly TimeSpan FailedAttemptCooldown = TimeSpan.FromSeconds(30);

    private readonly IOptions<AppConfiguration> options;
    private readonly ILogger<DeviceLocationService> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    private DeviceLocationSnapshot? cachedLocation;
    private DateTimeOffset lastAttemptUtc = DateTimeOffset.MinValue;
    private bool permissionDeniedLogged;
    private bool locationDisabledLogged;
    private bool unsupportedLogged;

    public DeviceLocationService(
        IOptions<AppConfiguration> options,
        ILogger<DeviceLocationService> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public async Task<DeviceLocationSnapshot?> GetBestEffortLocationAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Telemetry;
        if (!settings.Enabled || !settings.CaptureDeviceLocation)
        {
            return cachedLocation;
        }

        var now = DateTimeOffset.UtcNow;
        var refreshWindow = TimeSpan.FromMinutes(Math.Max(1, settings.DeviceLocationRefreshMinutes));
        if (cachedLocation is not null && now - cachedLocation.CapturedAtUtc < refreshWindow)
        {
            return cachedLocation;
        }

        if (cachedLocation is null && now - lastAttemptUtc < FailedAttemptCooldown)
        {
            return null;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (cachedLocation is not null && now - cachedLocation.CapturedAtUtc < refreshWindow)
            {
                return cachedLocation;
            }

            if (cachedLocation is null && now - lastAttemptUtc < FailedAttemptCooldown)
            {
                return null;
            }

            lastAttemptUtc = now;

            var snapshot = await TryGetCurrentLocationAsync(settings, cancellationToken);
            snapshot ??= await TryGetLastKnownLocationAsync(cancellationToken);

            if (snapshot is not null)
            {
                cachedLocation = snapshot;
            }

            return cachedLocation;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<DeviceLocationSnapshot?> TryGetCurrentLocationAsync(
        TelemetrySettings settings,
        CancellationToken cancellationToken)
    {
#if WINDOWS
        var windowsSnapshot = await TryGetWindowsFusedLocationAsync(settings, cancellationToken);
        if (windowsSnapshot is not null)
        {
            return windowsSnapshot;
        }
#endif

        try
        {
            var request = new GeolocationRequest(
                GeolocationAccuracy.Best,
                TimeSpan.FromSeconds(Math.Clamp(settings.DeviceLocationTimeoutSeconds, 3, 30)));
#if IOS || MACCATALYST
            request.RequestFullAccuracy = true;
#endif

            var location = await Geolocation.Default.GetLocationAsync(request, cancellationToken);
            return CreateSnapshot(location, "device_current", null);
        }
        catch (PermissionException ex)
        {
            if (!permissionDeniedLogged)
            {
                permissionDeniedLogged = true;
                logger.LogInformation(ex, "Device location permission was denied; telemetry will fall back to IP-based geo when needed.");
            }
        }
        catch (FeatureNotEnabledException ex)
        {
            if (!locationDisabledLogged)
            {
                locationDisabledLogged = true;
                logger.LogInformation(ex, "Device location services are disabled; telemetry will continue without device coordinates.");
            }
        }
        catch (FeatureNotSupportedException ex)
        {
            if (!unsupportedLogged)
            {
                unsupportedLogged = true;
                logger.LogInformation(ex, "Device geolocation is not supported on this system.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Current device geolocation request failed.");
        }

        return null;
    }

    private async Task<DeviceLocationSnapshot?> TryGetLastKnownLocationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var location = await Geolocation.Default.GetLastKnownLocationAsync();
            return CreateSnapshot(location, "device_last_known", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Last-known device geolocation lookup failed.");
            return null;
        }
    }

    private static DeviceLocationSnapshot? CreateSnapshot(Location? location, string source, string? signalSource)
    {
        if (location is null)
        {
            return null;
        }

        if (!double.IsFinite(location.Latitude) || !double.IsFinite(location.Longitude))
        {
            return null;
        }

        double? accuracyMeters = location.Accuracy is double accuracy && accuracy > 0
            ? Math.Round(accuracy, 1)
            : null;
        var capturedAtUtc = location.Timestamp == default
            ? DateTimeOffset.UtcNow
            : location.Timestamp.ToUniversalTime();

        return new DeviceLocationSnapshot(
            Latitude: location.Latitude,
            Longitude: location.Longitude,
            AccuracyMeters: accuracyMeters,
            CapturedAtUtc: capturedAtUtc,
            Source: source,
            SignalSource: signalSource);
    }

#if WINDOWS
    private async Task<DeviceLocationSnapshot?> TryGetWindowsFusedLocationAsync(
        TelemetrySettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var accessStatus = MainThread.IsMainThread
                ? await Geolocator.RequestAccessAsync().AsTask(cancellationToken)
                : await MainThread.InvokeOnMainThreadAsync(() => Geolocator.RequestAccessAsync().AsTask(cancellationToken));

            if (accessStatus != GeolocationAccessStatus.Allowed)
            {
                if (accessStatus == GeolocationAccessStatus.Denied && !permissionDeniedLogged)
                {
                    permissionDeniedLogged = true;
                    logger.LogInformation("Windows location access was denied; telemetry will fall back to IP-based geo when needed.");
                }

                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.DeviceLocationTimeoutSeconds, 3, 30)));

            var geolocator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.High,
                MovementThreshold = 0,
                ReportInterval = 0
            };

            var position = await geolocator.GetGeopositionAsync().AsTask(timeoutCts.Token);
            return CreateWindowsSnapshot(position);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (!permissionDeniedLogged)
            {
                permissionDeniedLogged = true;
                logger.LogInformation(ex, "Windows location access was denied; telemetry will fall back to IP-based geo when needed.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Windows fused location request timed out.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Windows fused location request failed.");
        }

        return null;
    }

    private static DeviceLocationSnapshot? CreateWindowsSnapshot(Geoposition? position)
    {
        var coordinate = position?.Coordinate;
        if (coordinate is null)
        {
            return null;
        }

        var point = coordinate.Point.Position;

        if (!double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude))
        {
            return null;
        }

        double? accuracyMeters = coordinate.Accuracy > 0
            ? Math.Round(coordinate.Accuracy, 1)
            : null;
        var capturedAtUtc = coordinate.Timestamp == default
            ? DateTimeOffset.UtcNow
            : coordinate.Timestamp.ToUniversalTime();

        return new DeviceLocationSnapshot(
            Latitude: point.Latitude,
            Longitude: point.Longitude,
            AccuracyMeters: accuracyMeters,
            CapturedAtUtc: capturedAtUtc,
            Source: "device_fused",
            SignalSource: NormalizeWindowsSignalSource(coordinate.PositionSource.ToString()));
    }

    private static string? NormalizeWindowsSignalSource(string? value)
    {
        return value switch
        {
            null => null,
            "" => null,
            "WiFi" => "wifi",
            "IPAddress" => "ip",
            _ => value.ToLowerInvariant(),
        };
    }
#endif
}
