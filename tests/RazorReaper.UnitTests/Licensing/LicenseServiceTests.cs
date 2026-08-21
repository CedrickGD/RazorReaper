using System.Net;
using System.Text;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Licensing;

public sealed class LicenseServiceTests
{
    private const string LicenseKeyPref = "RR_LicenseKey";
    private const string ExpiresAtPref = "RR_LicenseExpiresAt";
    private const string LicenseTypePref = "RR_LicenseType";
    private const string LicenseKey = "RR-TEST-1234";

    [Fact]
    public async Task Validate401InvalidInstallSignatureKeepsTheLicenseKeyAndState()
    {
        using var harness = new Harness(activated: true);
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(
            HttpStatusCode.Unauthorized, """{"ok":false,"error":"Invalid install signature."}"""));

        var (success, _) = await harness.Service.ValidateLicenseAsync();

        Assert.False(success);
        Assert.Single(harness.Http.Requests);
        // The signature gate says nothing about the key: registration may still be in flight or
        // the clock skewed. A paying user must not be wiped to Free here.
        Assert.Equal(LicenseKey, harness.Preferences.Peek(LicenseKeyPref));
        Assert.Equal(LicenseKey, harness.Service.CurrentLicenseKey);
        Assert.True(harness.Service.IsActivated);
        Assert.True(harness.Service.IsPremium);
        Assert.Equal(0, harness.Preferences.RemoveCallCount);
    }

    [Fact]
    public async Task Validate401WithoutErrorBodyKeepsTheLicenseKey()
    {
        using var harness = new Harness(activated: true);
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var (success, _) = await harness.Service.ValidateLicenseAsync();

        Assert.False(success);
        Assert.Equal(LicenseKey, harness.Preferences.Peek(LicenseKeyPref));
        Assert.True(harness.Service.IsActivated);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ValidateBackendTroubleKeepsTheLicenseKey(HttpStatusCode status)
    {
        using var harness = new Harness(activated: true);
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(status, """{"ok":false,"error":"Database not available"}"""));

        var (success, _) = await harness.Service.ValidateLicenseAsync();

        Assert.False(success);
        Assert.Equal(LicenseKey, harness.Preferences.Peek(LicenseKeyPref));
        Assert.True(harness.Service.IsActivated);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "License is revoked.")]
    [InlineData(HttpStatusCode.NotFound, "Invalid license key.")]
    public async Task ValidateExplicitRejectionStillClearsTheCachedLicense(HttpStatusCode status, string error)
    {
        using var harness = new Harness(activated: true);
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(status, $$"""{"ok":false,"error":"{{error}}"}"""));

        var (success, message) = await harness.Service.ValidateLicenseAsync();

        Assert.False(success);
        Assert.Equal(error, message);
        Assert.Null(harness.Preferences.Peek(LicenseKeyPref));
        Assert.Null(harness.Preferences.Peek(ExpiresAtPref));
        Assert.False(harness.Service.IsActivated);
        Assert.True(harness.Service.IsFreeTier);
    }

    [Fact]
    public async Task ValidateSuccessKeepsStateAndPersistsExpiry()
    {
        using var harness = new Harness(activated: true);
        harness.Http.ResponseFactory = (_, _) => Task.FromResult(Json(
            HttpStatusCode.OK, """{"ok":true,"type":"lifetime","expires_at":null}"""));

        var (success, _) = await harness.Service.ValidateLicenseAsync();

        Assert.True(success);
        Assert.True(harness.Service.IsPremium);
        Assert.Equal("lifetime", harness.Service.LicenseType);
        Assert.Equal("lifetime", harness.Preferences.Peek(LicenseTypePref));
    }

    [Fact]
    public void ExplicitRejectionClassificationMatchesTheServerContract()
    {
        Assert.True(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.BadRequest));
        Assert.True(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.Forbidden));
        Assert.True(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.NotFound));
        Assert.False(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.Unauthorized));
        Assert.False(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.RequestTimeout));
        Assert.False(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.TooManyRequests));
        Assert.False(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.InternalServerError));
        Assert.False(LicenseService.IsExplicitLicenseRejection(HttpStatusCode.BadGateway));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHwidService : IHwidService
    {
        public string GetHardwareId() => "5734B40BB3DF5517866D578B18438B61";
    }

    private sealed class Harness : IDisposable
    {
        public Harness(bool activated)
        {
            Preferences = new FakePreferencesStore();
            if (activated)
            {
                // Mirrors a previous launch that validated a lifetime key.
                Preferences.Seed(LicenseKeyPref, LicenseKey);
                Preferences.Seed(ExpiresAtPref, string.Empty);
                Preferences.Seed(LicenseTypePref, "lifetime");
            }

            Http = new RecordingHttpMessageHandler();
            Client = new HttpClient(Http, disposeHandler: false);
            Service = new LicenseService(Client, new StubHwidService(), Preferences);
        }

        public FakePreferencesStore Preferences { get; }
        public RecordingHttpMessageHandler Http { get; }
        public HttpClient Client { get; }
        public LicenseService Service { get; }

        public void Dispose()
        {
            Client.Dispose();
            Http.Dispose();
        }
    }
}
