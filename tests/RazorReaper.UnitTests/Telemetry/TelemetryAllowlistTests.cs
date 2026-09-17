using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests.Telemetry;

/// <summary>
/// The allowlist is the last thing between an event and the wire, and it fails silently: a name
/// that is not on it is dropped inside SendEventAsync without a log line, so the three update
/// rows the hybrid flow emits looked fine at every call site and never arrived. These drive the
/// real <see cref="TelemetryService"/> against a capturing HTTP handler — the same seam the
/// non-faulting tests use — so an event only passes here if it actually reached the sender.
/// </summary>
public sealed class TelemetryAllowlistTests
{
    [Fact]
    public async Task AnUpdateDownloadEventReachesTheSender()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        await service.TrackEventAsync(
            "update_download",
            TelemetryEventStatus.Ok,
            "Update downloaded.",
            new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["bytes"] = 76_123_456L,
                ["version"] = "1.5.4"
            });

        var request = Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(request);

        // "service" is the wire name for the event (rr.session.v2).
        Assert.Equal("update_download", payload.RootElement.GetProperty("service").GetString());
        Assert.Equal("Update downloaded.", payload.RootElement.GetProperty("message").GetString());

        var metrics = payload.RootElement.GetProperty("metrics");
        Assert.Equal("ok", metrics.GetProperty("status").GetString());
        Assert.Equal("1.5.4", metrics.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("update_check")]
    [InlineData("update_download")]
    [InlineData("update_install")]
    [InlineData("update_applied")]
    public async Task EveryUpdateEventTheManagerEmitsIsOnTheAllowlist(string eventName)
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        await service.TrackEventAsync(eventName, TelemetryEventStatus.Ok, "test");

        var request = Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(request);
        Assert.Equal(eventName, payload.RootElement.GetProperty("service").GetString());
    }

    /// <summary>The allowlist still is one: an event nobody added stays off the wire.</summary>
    [Fact]
    public async Task AnEventThatIsNotAllowedIsDropped()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        await service.TrackEventAsync("update_rollback", TelemetryEventStatus.Ok, "not a thing");

        Assert.Empty(handler.Requests);
    }

    private static TelemetryService CreateService(HttpMessageHandler handler)
    {
        var configuration = new AppConfiguration();
        configuration.Telemetry.Enabled = true;
        configuration.Telemetry.Endpoint = "https://example.invalid/api/ingest";

        return new TelemetryService(
            new SingleHandlerHttpClientFactory(handler),
            Options.Create(configuration),
            new NullDeviceLocationService(),
            new StubClientIdentityService(),
            new InMemoryPreferencesStore(),
            NullLogger<TelemetryService>.Instance);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
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
