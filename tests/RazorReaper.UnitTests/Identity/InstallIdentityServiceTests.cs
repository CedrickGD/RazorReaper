using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Http;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Identity;

public sealed class InstallIdentityServiceTests
{
    private const string InstallId = "d85b1407-351d-4694-9392-03acc5870eb1";
    private const string HardwareId = "5734B40BB3DF5517866D578B18438B61";
    private const string RegisterUrl = "https://backend.rr-admin-panel.workers.dev/api/install/register";
    private const string UsageStatusUrl = "https://rr-admin-panel.pages.dev/api/usage/status";
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] AllowedHosts = ["backend.rr-admin-panel.workers.dev", "rr-admin-panel.pages.dev"];

    [Fact]
    public async Task SignAsyncReturnsNullUntilTheBackendAcknowledgedTheKey()
    {
        using var harness = new Harness();
        var uri = new Uri("https://rr-admin-panel.pages.dev/api/license/validate");

        var beforeRegistration = await harness.Service.SignAsync(HttpMethod.Post, uri, [], Now);

        Assert.Null(beforeRegistration);
        Assert.False(harness.Service.IsRegistered);
        // The key itself exists already (generated on first use) — only the headers are held back.
        Assert.NotNull(await harness.Service.GetPublicKeyAsync());

        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.Created, """{"ok":true}"""));
        await harness.Service.EnsureRegisteredAsync();

        Assert.NotNull(await harness.Service.SignAsync(HttpMethod.Post, uri, [], Now));
    }

    [Fact]
    public async Task SignAsyncProducesVerifiableHeadersUsingSuppliedTimestamp()
    {
        using var harness = new Harness();
        await harness.RegisterAsync();
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var uri = new Uri("https://backend.rr-admin-panel.workers.dev/api/ingest?q=1");

        var headers = await harness.Service.SignAsync(HttpMethod.Post, uri, body, Now);

        Assert.NotNull(headers);
        Assert.Equal(InstallId, headers.InstallId);
        Assert.Equal(Now.ToUnixTimeSeconds().ToString(), headers.Timestamp);
        var jwk = await harness.Service.GetPublicKeyAsync();
        Assert.NotNull(jwk);
        using var verifier = InstallRequestSigning.FromJwk(jwk);
        var signingString = InstallRequestSigning.BuildSigningString(HttpMethod.Post, uri, headers.Timestamp, body);
        var signature = InstallRequestSigning.Base64UrlDecode(headers.Signature);
        Assert.Equal(64, signature.Length);
        Assert.True(InstallRequestSigning.Verify(verifier, signingString, signature));
    }

    [Fact]
    public async Task PrivateKeyRoundTripsThroughSecureStoreAsPkcs8()
    {
        var secureStore = new FakeSecureValueStore();
        InstallPublicKeyJwk? first;
        using (var harness = new Harness(secureStore: secureStore))
        {
            first = await harness.Service.GetPublicKeyAsync();
        }

        var stored = secureStore.Peek("rr.install.key");
        Assert.False(string.IsNullOrWhiteSpace(stored));
        using (var imported = ECDsa.Create())
        {
            imported.ImportPkcs8PrivateKey(Convert.FromBase64String(stored!), out _);
            Assert.Equal(256, imported.KeySize);
        }

        using var second = new Harness(secureStore: secureStore);
        var reloaded = await second.Service.GetPublicKeyAsync();

        Assert.NotNull(first);
        Assert.Equal(first, reloaded);
        Assert.Equal(1, secureStore.SetCallCount);
    }

    [Fact]
    public async Task EnsureRegisteredHandles201AndPersistsMarkers()
    {
        using var harness = new Harness();
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.Created,
            """{"ok":true,"install_id":"d85b1407-351d-4694-9392-03acc5870eb1","registered_at":"2026-08-21T12:00:00Z"}"""));

        await harness.Service.EnsureRegisteredAsync();

        Assert.True(harness.Service.IsRegistered);
        var request = Assert.Single(harness.Http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(RegisterUrl, request.Uri?.ToString());
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(InstallId, root.GetProperty("install_id").GetString());
        Assert.Equal(HardwareId, root.GetProperty("hwid").GetString());
        var publicKey = root.GetProperty("public_key");
        Assert.Equal("EC", publicKey.GetProperty("kty").GetString());
        Assert.Equal("P-256", publicKey.GetProperty("crv").GetString());
        Assert.Equal(32, InstallRequestSigning.Base64UrlDecode(publicKey.GetProperty("x").GetString()!).Length);
        Assert.Equal(32, InstallRequestSigning.Base64UrlDecode(publicKey.GetProperty("y").GetString()!).Length);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("app_version").GetString()));
        Assert.False(root.TryGetProperty("license_key", out _));
        Assert.Equal("2026-08-21T12:00:00Z", harness.Preferences.Peek("rr.install.registered_at"));
        Assert.Equal(InstallId, harness.Preferences.Peek("rr.install.registered_id"));
        Assert.Equal(["RazorReaperTelemetry"], harness.Factory.RequestedNames);
    }

    [Fact]
    public async Task EnsureRegisteredHandles200AsIdempotentAndIncludesLicenseKey()
    {
        using var harness = new Harness(licenseKey: "RR-TEST-KEY");
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true}"""));

        await harness.Service.EnsureRegisteredAsync();
        await harness.Service.EnsureRegisteredAsync();

        Assert.True(harness.Service.IsRegistered);
        var request = Assert.Single(harness.Http.Requests);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("RR-TEST-KEY", body.RootElement.GetProperty("license_key").GetString());
        // No registered_at in the response → the clock stamps the marker.
        Assert.Equal(Now.ToString("O"), harness.Preferences.Peek("rr.install.registered_at"));
    }

    [Fact]
    public async Task EnsureRegisteredSkipsNetworkWhenStoredKeyAndMarkerMatch()
    {
        var secureStore = new FakeSecureValueStore();
        var preferences = new FakePreferencesStore();
        using (var first = new Harness(secureStore: secureStore, preferences: preferences))
        {
            first.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.Created, """{"ok":true}"""));
            await first.Service.EnsureRegisteredAsync();
            Assert.Single(first.Http.Requests);
        }

        using var second = new Harness(secureStore: secureStore, preferences: preferences);
        await second.Service.EnsureRegisteredAsync();

        Assert.True(second.Service.IsRegistered);
        Assert.Empty(second.Http.Requests);
    }

    [Fact]
    public async Task EnsureRegisteredHandles409ByRotatingInstallIdAndKeyThenRetriesOnce()
    {
        using var harness = new Harness();
        var firstJwk = await harness.Service.GetPublicKeyAsync();
        var calls = 0;
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(++calls == 1
            ? Json(HttpStatusCode.Conflict, """{"ok":false,"error":"install_id already registered with a different key"}""")
            : Json(HttpStatusCode.Created, """{"ok":true}"""));

        await harness.Service.EnsureRegisteredAsync();

        Assert.True(harness.Service.IsRegistered);
        Assert.Equal(2, harness.Http.Requests.Count);
        Assert.Equal(1, harness.Identity.RotationCount);
        var rotatedId = harness.Identity.GetIdentity().InstallId;
        Assert.NotEqual(InstallId, rotatedId);
        Assert.True(Guid.TryParseExact(rotatedId, "D", out _));

        using var firstBody = JsonDocument.Parse(harness.Http.Requests[0].Body!);
        using var secondBody = JsonDocument.Parse(harness.Http.Requests[1].Body!);
        Assert.Equal(InstallId, firstBody.RootElement.GetProperty("install_id").GetString());
        Assert.Equal(rotatedId, secondBody.RootElement.GetProperty("install_id").GetString());

        var secondJwk = await harness.Service.GetPublicKeyAsync();
        Assert.NotEqual(firstJwk, secondJwk);
        Assert.Equal(secondJwk!.X, secondBody.RootElement.GetProperty("public_key").GetProperty("x").GetString());
        Assert.Equal(rotatedId, harness.Preferences.Peek("rr.install.registered_id"));
        Assert.Equal(2, harness.SecureStore.SetCallCount);
    }

    [Fact]
    public async Task EnsureRegisteredHandles401LikeConflictAndGivesUpAfterSecondRejection()
    {
        using var harness = new Harness();
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"ok":false}"""));

        await harness.Service.EnsureRegisteredAsync();

        Assert.False(harness.Service.IsRegistered);
        Assert.Equal(2, harness.Http.Requests.Count);
        Assert.Equal(1, harness.Identity.RotationCount);
        Assert.Null(harness.Service.RetryTask);
    }

    [Fact]
    public async Task EnsureRegisteredHandles429BySchedulingRetryWithoutThrowing()
    {
        using var harness = new Harness();
        harness.Service.RetryDelays = [TimeSpan.FromHours(1)];
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, """{"ok":false}"""));

        var exception = await Record.ExceptionAsync(() => harness.Service.EnsureRegisteredAsync());

        Assert.Null(exception);
        Assert.False(harness.Service.IsRegistered);
        Assert.Single(harness.Http.Requests);
        Assert.NotNull(harness.Service.RetryTask);
        Assert.False(harness.Service.RetryTask.IsCompleted);
    }

    [Fact]
    public async Task BackgroundRetrySucceedsOnceBackendRecovers()
    {
        using var harness = new Harness();
        harness.Service.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        var calls = 0;
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(++calls < 3
            ? Json(HttpStatusCode.ServiceUnavailable, "")
            : Json(HttpStatusCode.Created, """{"ok":true}"""));

        await harness.Service.EnsureRegisteredAsync();
        Assert.NotNull(harness.Service.RetryTask);
        await harness.Service.RetryTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(harness.Service.IsRegistered);
        Assert.Equal(3, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task NetworkFailureSchedulesRetryInsteadOfThrowing()
    {
        using var harness = new Harness();
        harness.Service.RetryDelays = [TimeSpan.FromHours(1)];
        harness.Http.ResponseFactory = (_, _) => throw new HttpRequestException("offline");

        var exception = await Record.ExceptionAsync(() => harness.Service.EnsureRegisteredAsync());

        Assert.Null(exception);
        Assert.False(harness.Service.IsRegistered);
        Assert.NotNull(harness.Service.RetryTask);
    }

    [Fact]
    public async Task RetryBudgetIsPerProcessNotPerEnsureCall()
    {
        using var harness = new Harness();
        harness.Service.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, """{"ok":false}"""));

        await harness.Service.EnsureRegisteredAsync();
        await harness.Service.RetryTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, harness.Http.Requests.Count); // direct call + two scheduled retries

        await harness.Service.EnsureRegisteredAsync();
        if (harness.Service.RetryTask is { } second)
        {
            await second.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // The second call may try once itself, but must not replay the whole backoff schedule.
        Assert.Equal(4, harness.Http.Requests.Count);
        Assert.False(harness.Service.IsRegistered);
    }

    [Fact]
    public async Task BrokenSecureStoreDisablesSigningAndRegistration()
    {
        var secureStore = new FakeSecureValueStore { Failure = new InvalidOperationException("DPAPI down") };
        using var harness = new Harness(secureStore: secureStore);

        await harness.Service.EnsureRegisteredAsync();
        var headers = await harness.Service.SignAsync(HttpMethod.Get, new Uri("https://rr-admin-panel.pages.dev/api/usage/status"), [], Now);

        // An unpersisted key must never be registered: the next launch would 409 and rotate the
        // install id on every start. Degrade to unsigned instead, once per process.
        Assert.Null(headers);
        Assert.Empty(harness.Http.Requests);
        Assert.False(harness.Service.IsRegistered);
        Assert.True(harness.Service.IsSigningUnavailable);
        Assert.Null(await harness.Service.GetPublicKeyAsync());
        Assert.Null(harness.Service.RetryTask);
        // Only one probe of the store — the "no key" state is cached, not re-tried per request.
        Assert.Equal(1, secureStore.GetCallCount);
    }

    [Fact]
    public async Task UnwritableSecureStoreDisablesSigningWithoutKeepingAnEphemeralKey()
    {
        var secureStore = new FakeSecureValueStore { WriteFailure = new UnauthorizedAccessException("read-only") };
        using var harness = new Harness(secureStore: secureStore);

        await harness.Service.EnsureRegisteredAsync();

        Assert.Empty(harness.Http.Requests);
        Assert.Null(await harness.Service.GetPublicKeyAsync());
        Assert.Null(await harness.Service.SignAsync(HttpMethod.Get, new Uri("https://rr-admin-panel.pages.dev/api/usage/status"), [], Now));
        Assert.True(harness.Service.IsSigningUnavailable);
        Assert.Null(secureStore.Peek("rr.install.key"));
    }

    [Fact]
    public async Task PersistedKeyAndMarkerFromPreviousProcessSignImmediately()
    {
        var secureStore = new FakeSecureValueStore();
        var preferences = new FakePreferencesStore();
        using (var first = new Harness(secureStore: secureStore, preferences: preferences))
        {
            await first.RegisterAsync();
        }

        using var second = new Harness(secureStore: secureStore, preferences: preferences);
        await second.Service.EnsureRegisteredAsync();
        var headers = await second.Service.SignAsync(HttpMethod.Get, new Uri("https://rr-admin-panel.pages.dev/api/usage/status"), [], Now);

        Assert.NotNull(headers);
        Assert.Equal(InstallId, headers.InstallId);
        Assert.Empty(second.Http.Requests);
    }

    [Fact]
    public async Task RetryLoopDoesNotRestartOnceTheBudgetIsExhausted()
    {
        using var harness = new Harness();
        harness.Service.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero];
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, """{"ok":false}"""));

        await harness.Service.EnsureRegisteredAsync();
        var loop = harness.Service.RetryTask;
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, harness.Http.Requests.Count);
        Assert.Equal(1, harness.Logger.Count("gave up"));

        await harness.Service.EnsureRegisteredAsync();

        // The direct attempt is still made, but no second loop starts and nothing gives up twice.
        Assert.Equal(4, harness.Http.Requests.Count);
        Assert.Same(loop, harness.Service.RetryTask);
        Assert.Equal(1, harness.Logger.Count("gave up"));
        Assert.False(harness.Service.IsRegistered);
    }

    [Fact]
    public async Task RejectedSignatureClearsRegistrationAndReRegistersExactlyOnce()
    {
        using var harness = new Harness();
        await harness.RegisterAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var releaseRegister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Http.ResponseFactory = async (request, _) =>
        {
            if (!IsRegister(request))
            {
                return Json(HttpStatusCode.Unauthorized, """{"ok":false,"error":"Invalid install signature."}""");
            }

            await releaseRegister.Task;
            return Json(HttpStatusCode.Created, """{"ok":true}""");
        };

        using var response = await harness.AppClient.GetAsync(UsageStatusUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(harness.Http.Requests[1].HasHeader(SignedRequestHeaders.InstallHeaderName));
        // Cleared synchronously: the very next request goes out unsigned while the re-registration runs.
        Assert.False(harness.Service.IsRegistered);
        Assert.Null(await harness.Service.SignAsync(HttpMethod.Get, new Uri(UsageStatusUrl), [], harness.Time.GetUtcNow()));
        Assert.Null(harness.Preferences.Peek("rr.install.registered_id"));
        Assert.NotNull(harness.Service.ReRegistrationTask);

        releaseRegister.SetResult();
        await harness.AwaitReRegistrationAsync();

        Assert.True(harness.Service.IsRegistered);
        Assert.Equal([RegisterUrl, UsageStatusUrl, RegisterUrl], harness.Http.Requests.Select(r => r.Uri!.ToString()));
        Assert.Equal(InstallId, harness.Preferences.Peek("rr.install.registered_id"));
        Assert.Equal(0, harness.Identity.RotationCount);
        Assert.Equal(1, harness.Logger.Count("rejected the install signature"));
    }

    [Fact]
    public async Task SuccessfulReRegistrationResumesSigning()
    {
        using var harness = new Harness();
        await harness.RecoverFromRejectionAsync();
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}"));

        using var response = await harness.AppClient.GetAsync(UsageStatusUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = harness.Http.Requests[^1];
        Assert.Equal(InstallId, Assert.Single(sent.Headers[SignedRequestHeaders.InstallHeaderName]));
        Assert.True(sent.HasHeader(SignedRequestHeaders.SignatureHeaderName));
    }

    [Fact]
    public async Task SecondRejectionWithinTenMinutesTriggersNoReRegistration()
    {
        using var harness = new Harness();
        await harness.RecoverFromRejectionAsync();
        var firstReRegistration = harness.Service.ReRegistrationTask;
        var requestsBefore = harness.Http.Requests.Count;
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Http.ResponseFactory = RegisterSucceedsOtherwise(HttpStatusCode.Unauthorized);

        using var rejected = await harness.AppClient.GetAsync(UsageStatusUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.True(harness.Http.Requests[^1].HasHeader(SignedRequestHeaders.InstallHeaderName));
        Assert.False(harness.Service.IsRegistered);
        Assert.Same(firstReRegistration, harness.Service.ReRegistrationTask);
        Assert.Null(harness.Service.RetryTask);
        Assert.Equal(requestsBefore + 1, harness.Http.Requests.Count);
        Assert.Equal(1, harness.Logger.Count("throttled"));

        // From here on requests go out unsigned, and a 401 on an unsigned request changes nothing.
        using var unsigned = await harness.AppClient.GetAsync(UsageStatusUrl);

        Assert.False(harness.Http.Requests[^1].HasHeader(SignedRequestHeaders.InstallHeaderName));
        Assert.Same(firstReRegistration, harness.Service.ReRegistrationTask);
        Assert.Equal(requestsBefore + 2, harness.Http.Requests.Count);
        // One rejection during recovery, one throttled — the unsigned 401 logged nothing.
        Assert.Equal(2, harness.Logger.Count("rejected the install signature"));
    }

    [Fact]
    public async Task RejectionAfterTenMinutesReRegistersAgain()
    {
        using var harness = new Harness();
        await harness.RecoverFromRejectionAsync();
        var firstReRegistration = harness.Service.ReRegistrationTask;
        harness.Time.Advance(InstallIdentityService.ReRegistrationWindow);
        harness.Http.ResponseFactory = RegisterSucceedsOtherwise(HttpStatusCode.Unauthorized);

        using var rejected = await harness.AppClient.GetAsync(UsageStatusUrl);

        Assert.NotSame(firstReRegistration, harness.Service.ReRegistrationTask);
        await harness.AwaitReRegistrationAsync();
        Assert.True(harness.Service.IsRegistered);
        Assert.Equal(
            [RegisterUrl, UsageStatusUrl, RegisterUrl, UsageStatusUrl, RegisterUrl],
            harness.Http.Requests.Select(r => r.Uri!.ToString()));
    }

    [Fact]
    public async Task RejectionOfUnsignedRequestIsIgnored()
    {
        using var harness = new Harness();
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"ok":false}"""));

        using var response = await harness.AppClient.GetAsync(UsageStatusUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var sent = Assert.Single(harness.Http.Requests);
        Assert.False(sent.HasHeader(SignedRequestHeaders.InstallHeaderName));
        Assert.False(harness.Service.IsRegistered);
        Assert.Null(harness.Service.ReRegistrationTask);
        Assert.Null(harness.Service.RetryTask);
        Assert.Equal(0, harness.Logger.Count("rejected the install signature"));
    }

    [Fact]
    public async Task RejectionSignedBeforeTheReRegistrationIsIgnored()
    {
        using var harness = new Harness();
        await harness.RegisterAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var uri = new Uri(UsageStatusUrl);
        var first = await harness.Service.SignAsync(HttpMethod.Get, uri, [], harness.Time.GetUtcNow());
        var inFlightTwin = await harness.Service.SignAsync(HttpMethod.Get, uri, [], harness.Time.GetUtcNow());
        harness.Http.ResponseFactory = RegisterSucceedsOtherwise(HttpStatusCode.Unauthorized);

        harness.Service.ReportSignedRequestRejected(uri, first!);
        await harness.AwaitReRegistrationAsync();
        Assert.True(harness.Service.IsRegistered);
        var requestsAfterRecovery = harness.Http.Requests.Count;

        // The twin was signed before the backend acknowledged the key again: its 401 is stale.
        harness.Service.ReportSignedRequestRejected(uri, inFlightTwin!);

        Assert.True(harness.Service.IsRegistered);
        Assert.Equal(requestsAfterRecovery, harness.Http.Requests.Count);
        Assert.Equal(1, harness.Logger.Count("rejected the install signature"));

        // A signature made after the acknowledgement is a real rejection again.
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var fresh = await harness.Service.SignAsync(HttpMethod.Get, uri, [], harness.Time.GetUtcNow());
        harness.Service.ReportSignedRequestRejected(uri, fresh!);

        Assert.False(harness.Service.IsRegistered);
        Assert.Equal(2, harness.Logger.Count("rejected the install signature"));
    }

    [Fact]
    public async Task ReRegistrationAfterRejectionFollowsTheRotatePathOn401()
    {
        using var harness = new Harness();
        await harness.RegisterAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var registerCalls = 0;
        harness.Http.ResponseFactory = (request, _) => Task.FromResult(IsRegister(request)
            ? ++registerCalls == 1
                ? Json(HttpStatusCode.Unauthorized, """{"ok":false,"error":"revoked"}""")
                : Json(HttpStatusCode.Created, """{"ok":true}""")
            : Json(HttpStatusCode.Unauthorized, """{"ok":false}"""));

        using var rejected = await harness.AppClient.GetAsync(UsageStatusUrl);
        await harness.AwaitReRegistrationAsync();

        Assert.True(harness.Service.IsRegistered);
        Assert.Equal(1, harness.Identity.RotationCount);
        var rotatedId = harness.Identity.GetIdentity().InstallId;
        Assert.NotEqual(InstallId, rotatedId);
        Assert.Equal([RegisterUrl, UsageStatusUrl, RegisterUrl, RegisterUrl], harness.Http.Requests.Select(r => r.Uri!.ToString()));

        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        using var resumed = await harness.AppClient.GetAsync(UsageStatusUrl);
        Assert.Equal(rotatedId, Assert.Single(harness.Http.Requests[^1].Headers[SignedRequestHeaders.InstallHeaderName]));
    }

    [Fact]
    public async Task ReRegistrationAfterRejectionUsesTheRetryBudgetOnTransientFailure()
    {
        using var harness = new Harness();
        await harness.RegisterAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        harness.Service.RetryDelays = [TimeSpan.Zero];
        var registerCalls = 0;
        harness.Http.ResponseFactory = (request, _) => Task.FromResult(IsRegister(request)
            ? ++registerCalls == 1
                ? Json(HttpStatusCode.ServiceUnavailable, "")
                : Json(HttpStatusCode.Created, """{"ok":true}""")
            : Json(HttpStatusCode.Unauthorized, """{"ok":false}"""));

        using var rejected = await harness.AppClient.GetAsync(UsageStatusUrl);
        await harness.AwaitReRegistrationAsync();
        Assert.NotNull(harness.Service.RetryTask);
        await harness.Service.RetryTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(harness.Service.IsRegistered);
        Assert.Equal(0, harness.Identity.RotationCount);
        Assert.Equal([RegisterUrl, UsageStatusUrl, RegisterUrl, RegisterUrl], harness.Http.Requests.Select(r => r.Uri!.ToString()));
    }

    private static bool IsRegister(HttpRequestMessage request)
        => string.Equals(request.RequestUri?.AbsolutePath, InstallIdentityService.RegisterPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Register POSTs get a 201; every other request gets <paramref name="status"/>.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RegisterSucceedsOtherwise(HttpStatusCode status)
        => (request, _) => Task.FromResult(IsRegister(request)
            ? Json(HttpStatusCode.Created, """{"ok":true}""")
            : Json(status, """{"ok":false,"error":"Invalid install signature."}"""));

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Harness : IDisposable
    {
        public Harness(
            FakeSecureValueStore? secureStore = null,
            FakePreferencesStore? preferences = null,
            string licenseKey = "")
        {
            SecureStore = secureStore ?? new FakeSecureValueStore();
            Preferences = preferences ?? new FakePreferencesStore();
            Http = new RecordingHttpMessageHandler();
            Factory = new FakeHttpClientFactory(Http);
            Identity = new RotatingClientIdentityService(InstallId, HardwareId);
            Time = new ManualTimeProvider(Now);
            Logger = new RecordingLogger<InstallIdentityService>();
            Service = new InstallIdentityService(
                Identity,
                SecureStore,
                Preferences,
                Factory,
                Options.Create(new AppConfiguration
                {
                    Telemetry = new TelemetrySettings
                    {
                        Enabled = true,
                        Endpoint = "https://backend.rr-admin-panel.workers.dev/api/ingest",
                        RequestTimeoutSeconds = 5
                    },
                    AdminPanel = new AdminPanelSettings { BaseUrl = "https://rr-admin-panel.pages.dev" }
                }),
                new StubLicenseService { CurrentLicenseKey = licenseKey },
                Time,
                Logger);

            // The app's side of the pipeline: requests run through the signing handler into the
            // same recorder the registration client uses, exactly like the production HttpClients.
            var signingHandler = new SignedRequestHandler(() => Service, Time, AllowedHosts, NullLogger<SignedRequestHandler>.Instance)
            {
                InnerHandler = Http
            };
            AppClient = new HttpClient(signingHandler, disposeHandler: false);
        }

        /// <summary>Registers with a 201 and returns the recorder to its caller's control.</summary>
        public async Task RegisterAsync()
        {
            var previous = Http.ResponseFactory;
            Http.ResponseFactory = (_, _) => Task.FromResult(Json(HttpStatusCode.Created, """{"ok":true}"""));
            await Service.EnsureRegisteredAsync();
            Http.ResponseFactory = previous;
            Assert.True(Service.IsRegistered);
        }

        /// <summary>
        /// Registers, has the backend reject one signed request a second later, and waits for the
        /// resulting re-registration (201) to land. Leaves three recorded requests behind.
        /// </summary>
        public async Task RecoverFromRejectionAsync()
        {
            await RegisterAsync();
            Time.Advance(TimeSpan.FromSeconds(1));
            var previous = Http.ResponseFactory;
            Http.ResponseFactory = RegisterSucceedsOtherwise(HttpStatusCode.Unauthorized);
            using var response = await AppClient.GetAsync(UsageStatusUrl);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await AwaitReRegistrationAsync();
            Assert.True(Service.IsRegistered);
            Http.ResponseFactory = previous;
        }

        public async Task AwaitReRegistrationAsync()
        {
            var task = Service.ReRegistrationTask;
            Assert.NotNull(task);
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public FakeSecureValueStore SecureStore { get; }
        public FakePreferencesStore Preferences { get; }
        public RecordingHttpMessageHandler Http { get; }
        public FakeHttpClientFactory Factory { get; }
        public RotatingClientIdentityService Identity { get; }
        public ManualTimeProvider Time { get; }
        public RecordingLogger<InstallIdentityService> Logger { get; }
        public InstallIdentityService Service { get; }
        public HttpClient AppClient { get; }

        public void Dispose()
        {
            AppClient.Dispose();
            Service.Dispose();
            Factory.Dispose();
            Http.Dispose();
        }
    }
}
