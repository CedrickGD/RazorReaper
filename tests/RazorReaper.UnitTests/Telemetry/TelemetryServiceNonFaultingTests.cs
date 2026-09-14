using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests.Telemetry;

/// <summary>
/// Every RR-E1003 reporter call site discards the returned task. If TrackEventAsync could fault,
/// that discarded task would resurface on the finalizer thread as another unobserved-task
/// exception and the reporter would feed itself — which is exactly the bug f0b757c fixed before
/// v1.4.8. Its doc comment claims it never faults; these tests hold it to that.
/// </summary>
public sealed class TelemetryServiceNonFaultingTests
{
    [Fact]
    public async Task DoesNotFaultWhenTheEndpointThrows()
    {
        var service = CreateService(new ThrowingHandler(new HttpRequestException("no route to host")));

        await service.TrackEventAsync("app_error", TelemetryEventStatus.Down, "boom", Metrics());
    }

    [Fact]
    public async Task DoesNotFaultWhenTheEndpointTimesOut()
    {
        var service = CreateService(new ThrowingHandler(new TaskCanceledException("timed out")));

        await service.TrackEventAsync("app_error", TelemetryEventStatus.Down, "boom", Metrics());
    }

    [Fact]
    public async Task DoesNotFaultWhenAnIdentityDependencyThrows()
    {
        // A failure while building the metrics has to be swallowed too, not just a send failure.
        var service = CreateService(
            new ThrowingHandler(new HttpRequestException("unused")),
            identity: new ThrowingClientIdentityService());

        await service.TrackEventAsync("app_error", TelemetryEventStatus.Down, "boom", Metrics());
    }

    [Fact]
    public async Task DoesNotFaultOnACanceledToken()
    {
        var service = CreateService(new ThrowingHandler(new HttpRequestException("unused")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.TrackEventAsync(
            "app_error", TelemetryEventStatus.Down, "boom", Metrics(), cts.Token);
    }

    private static IReadOnlyDictionary<string, object?> Metrics()
    {
        return new Dictionary<string, object?>
        {
            ["error_code"] = "RR-E1003",
            ["error_kind"] = "background",
            ["top_frame"] = "RazorReaper.Components.Pages.Home.UpdateResources (Home.razor:1529)",
            ["occurrences"] = 12_345L
        };
    }

    private static TelemetryService CreateService(
        HttpMessageHandler handler,
        IClientIdentityService? identity = null)
    {
        var configuration = new AppConfiguration();
        configuration.Telemetry.Enabled = true;
        configuration.Telemetry.Endpoint = "https://example.invalid/api/ingest";

        return new TelemetryService(
            new SingleHandlerHttpClientFactory(handler),
            Options.Create(configuration),
            new NullDeviceLocationService(),
            identity ?? new StubClientIdentityService(),
            new InMemoryPreferencesStore(),
            NullLogger<TelemetryService>.Instance);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubClientIdentityService : IClientIdentityService
    {
        public ClientIdentity GetIdentity() => new("install-id", "hardware-id");

        public ClientIdentity RotateInstallId() => GetIdentity();
    }

    private sealed class ThrowingClientIdentityService : IClientIdentityService
    {
        public ClientIdentity GetIdentity() => throw new InvalidOperationException("identity unavailable");

        public ClientIdentity RotateInstallId() => throw new InvalidOperationException("identity unavailable");
    }

    private sealed class NullDeviceLocationService : IDeviceLocationService
    {
        public Task<DeviceLocationSnapshot?> GetBestEffortLocationAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DeviceLocationSnapshot?>(null);
        }
    }

    private sealed class InMemoryPreferencesStore : IPreferencesStore
    {
        private readonly Dictionary<string, object?> values = [];

        public T Get<T>(string key, T defaultValue)
        {
            return values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        }

        public void Set<T>(string key, T value) => values[key] = value;

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public bool Remove(string key) => values.Remove(key);
    }
}
