using System.Net;
using System.Reflection;
using System.Text.Json;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Identity;

public sealed class IdentityConsumerTests
{
    private static readonly ClientIdentity SuppliedIdentity = new(
        "d85b1407-351d-4694-9392-03acc5870eb1",
        "5734B40BB3DF5517866D578B18438B61");

    [Fact]
    public async Task IdentityConsumerAccessGateUsesBothValuesFromOneCentralIdentityRecord()
    {
        using var handler = JsonResponseHandler("""{"ok":true,"suspended":false}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var identity = new RecordingClientIdentityService(SuppliedIdentity);
        var service = CreateAccessGateService(httpClient, identity);

        await service.CheckNowAsync();

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        Assert.Equal(SuppliedIdentity.InstallId, body.RootElement.GetProperty("install_id").GetString());
        Assert.Equal(SuppliedIdentity.HardwareId, body.RootElement.GetProperty("hwid").GetString());
        Assert.Equal(1, identity.CallCount);
    }

    [Fact]
    public async Task IdentityConsumerFeedbackUsesBothValuesFromOneCentralIdentityRecord()
    {
        using var handler = JsonResponseHandler("""{"ok":true,"message":"received"}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var identity = new RecordingClientIdentityService(SuppliedIdentity);
        var service = CreateFeedbackService(httpClient, identity);

        var result = await service.SubmitAsync("Useful feedback", "tester");

        Assert.True(result.Success);
        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        Assert.Equal(SuppliedIdentity.InstallId, body.RootElement.GetProperty("install_id").GetString());
        Assert.Equal(SuppliedIdentity.HardwareId, body.RootElement.GetProperty("hwid").GetString());
        Assert.Equal(1, identity.CallCount);
    }

    [Fact]
    public async Task IdentityConsumerTelemetryObtainsOneCentralIdentityRecordPerPayload()
    {
        using var handler = JsonResponseHandler("{}");
        using var factory = CreateHttpClientFactory(handler, out var factoryObject);
        var identity = new RecordingClientIdentityService(SuppliedIdentity);
        var location = new RecordingDeviceLocationService();
        var preferences = CreateTelemetryPreferences();
        var service = CreateTelemetryService(factoryObject, identity, location, preferences);

        await service.TrackEventAsync("process_start");

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        var metrics = body.RootElement.GetProperty("metrics");
        Assert.Equal(SuppliedIdentity.InstallId, metrics.GetProperty("install_id").GetString());
        Assert.False(metrics.GetProperty("rpc_enabled").GetBoolean());
        Assert.Equal("preview-review-user", metrics.GetProperty("discord_user").GetString());
        Assert.Equal(1, identity.CallCount);
        Assert.Equal(1, factory.CreateClientCallCount);
        Assert.Equal(1, location.CallCount);
        Assert.Equal(2, preferences.GetCallCount);
        Assert.Equal(
            [
                IDiscordPresenceService.EnabledPreferenceKey,
                IDiscordPresenceService.ConnectedUserPreferenceKey,
            ],
            preferences.GetKeys);
        Assert.Equal(0, preferences.SetCallCount);
    }

    [Fact]
    public void IdentityConsumerTelemetryConstructorPerformsNoIdentityLocationHttpOrTimerWork()
    {
        using var handler = JsonResponseHandler("{}");
        using var factory = CreateHttpClientFactory(handler, out var factoryObject);
        var identity = new RecordingClientIdentityService(SuppliedIdentity);
        var location = new RecordingDeviceLocationService();
        var preferences = new FakePreferencesStore();

        _ = CreateTelemetryService(factoryObject, identity, location, preferences);

        Assert.Equal(0, identity.CallCount);
        Assert.Equal(0, location.CallCount);
        Assert.Equal(0, factory.CreateClientCallCount);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, preferences.GetCallCount);
        Assert.Empty(preferences.GetKeys);
        Assert.Equal(0, preferences.SetCallCount);
    }

    private static RecordingHttpMessageHandler JsonResponseHandler(string json)
        => new()
        {
            ResponseFactory = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            }),
        };

    private static AccessGateService CreateAccessGateService(
        HttpClient httpClient,
        IClientIdentityService identity)
    {
        var constructor = Assert.Single(typeof(AccessGateService).GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Equal(typeof(IClientIdentityService), parameters[1].ParameterType);
        return Assert.IsType<AccessGateService>(constructor.Invoke(
        [
            httpClient,
            identity,
            CreateOptions(parameters[2].ParameterType, telemetryEnabled: false),
            CreateNoOpProxy(parameters[3].ParameterType),
        ]));
    }

    private static FeedbackService CreateFeedbackService(
        HttpClient httpClient,
        IClientIdentityService identity)
    {
        var constructor = Assert.Single(typeof(FeedbackService).GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Equal(typeof(IClientIdentityService), parameters[1].ParameterType);
        return Assert.IsType<FeedbackService>(constructor.Invoke(
        [
            httpClient,
            identity,
            new StubLicenseService(),
            CreateOptions(parameters[3].ParameterType, telemetryEnabled: false),
            CreateNoOpProxy(parameters[4].ParameterType),
            CreateNoOpProxy(parameters[5].ParameterType),
        ]));
    }

    private static TelemetryService CreateTelemetryService(
        object httpClientFactory,
        IClientIdentityService identity,
        IDeviceLocationService location,
        IPreferencesStore preferences)
    {
        var constructor = Assert.Single(typeof(TelemetryService).GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Equal(6, parameters.Length);
        Assert.Equal(typeof(IClientIdentityService), parameters[3].ParameterType);
        Assert.Equal(typeof(IPreferencesStore), parameters[4].ParameterType);
        return Assert.IsType<TelemetryService>(constructor.Invoke(
        [
            httpClientFactory,
            CreateOptions(parameters[1].ParameterType, telemetryEnabled: true),
            location,
            identity,
            preferences,
            CreateNoOpProxy(parameters[5].ParameterType),
        ]));
    }

    private static FakePreferencesStore CreateTelemetryPreferences()
    {
        var preferences = new FakePreferencesStore();
        preferences.Seed(IDiscordPresenceService.EnabledPreferenceKey, false);
        preferences.Seed(IDiscordPresenceService.ConnectedUserPreferenceKey, "preview-review-user");
        preferences.ResetCallCounts();
        return preferences;
    }

    private static object CreateOptions(Type optionsType, bool telemetryEnabled)
    {
        var wrapperType = optionsType.Assembly
            .GetType("Microsoft.Extensions.Options.OptionsWrapper`1", throwOnError: true)!
            .MakeGenericType(typeof(AppConfiguration));
        return Activator.CreateInstance(wrapperType, CreateConfiguration(telemetryEnabled))!;
    }

    private static AppConfiguration CreateConfiguration(bool telemetryEnabled)
        => new()
        {
            AdminPanel = new AdminPanelSettings
            {
                BaseUrl = "https://example.invalid",
                RequestTimeoutSeconds = 3,
                AccessCheckIntervalSeconds = 60,
            },
            Telemetry = new TelemetrySettings
            {
                Enabled = telemetryEnabled,
                Endpoint = "https://example.invalid/telemetry",
                AppName = "razorreaper",
                RequestTimeoutSeconds = 3,
            },
        };

    private static object CreateNoOpProxy(Type interfaceType)
        => DispatchProxy.Create(interfaceType, typeof(NoOpDispatchProxy));

    private static HttpClientFactoryDispatchProxy CreateHttpClientFactory(
        HttpMessageHandler handler,
        out object factoryObject)
    {
        var constructor = Assert.Single(typeof(TelemetryService).GetConstructors());
        var factoryInterface = constructor.GetParameters()[0].ParameterType;
        factoryObject = DispatchProxy.Create(factoryInterface, typeof(HttpClientFactoryDispatchProxy));
        var proxy = Assert.IsAssignableFrom<HttpClientFactoryDispatchProxy>(factoryObject);
        proxy.Initialize(handler);
        return proxy;
    }

    private sealed class RecordingClientIdentityService(ClientIdentity identity) : IClientIdentityService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ClientIdentity GetIdentity()
        {
            Interlocked.Increment(ref _callCount);
            return identity;
        }

        public ClientIdentity RotateInstallId() => identity;
    }

    private sealed class RecordingDeviceLocationService : IDeviceLocationService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DeviceLocationSnapshot?> GetBestEffortLocationAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult<DeviceLocationSnapshot?>(null);
        }
    }

    public class HttpClientFactoryDispatchProxy : DispatchProxy, IDisposable
    {
        private HttpClient? _client;
        private int _createClientCallCount;

        public int CreateClientCallCount => Volatile.Read(ref _createClientCallCount);

        public void Initialize(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler, disposeHandler: false);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(targetMethod);
            Assert.Equal("CreateClient", targetMethod.Name);
            Interlocked.Increment(ref _createClientCallCount);
            return _client ?? throw new InvalidOperationException("Proxy was not initialized.");
        }

        public void Dispose() => _client?.Dispose();
    }

    public class NoOpDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || targetMethod.ReturnType == typeof(void))
            {
                return null;
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private sealed class StubLicenseService : ILicenseService
    {
        public bool IsActivated => false;
        public bool IsPremium => false;
        public bool IsFreeTier => true;
        public string CurrentLicenseKey => string.Empty;
        public string? ExpiresAt => null;
        public string? LicenseType => null;

        public event Action OnLicenseStateChanged
        {
            add { }
            remove { }
        }

        public event Action OnLicenseActivated
        {
            add { }
            remove { }
        }

        public Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey)
            => Task.FromResult((false, "not used"));

        public Task<(bool Success, string Message)> ValidateLicenseAsync()
            => Task.FromResult((false, "not used"));
    }
}
