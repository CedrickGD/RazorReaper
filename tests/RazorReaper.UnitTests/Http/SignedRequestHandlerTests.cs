using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Http;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Http;

public sealed class SignedRequestHandlerTests
{
    private const string InstallId = "d85b1407-351d-4694-9392-03acc5870eb1";
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] AllowedHosts = ["backend.rr-admin-panel.workers.dev", "rr-admin-panel.pages.dev"];

    [Fact]
    public async Task AddsThreeHeadersOnAllowListedHostAndVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new KeyedIdentity(key);
        using var harness = new Harness(identity, Now);
        var body = """{"event":"process_start"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://backend.rr-admin-panel.workers.dev/api/ingest?v=2")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await harness.Client.SendAsync(request);

        var sent = Assert.Single(harness.Inner.Requests);
        Assert.Equal(InstallId, Assert.Single(sent.Headers[SignedRequestHeaders.InstallHeaderName]));
        var timestamp = Assert.Single(sent.Headers[SignedRequestHeaders.TimestampHeaderName]);
        Assert.Equal(Now.ToUnixTimeSeconds().ToString(), timestamp);
        var signature = InstallRequestSigning.Base64UrlDecode(Assert.Single(sent.Headers[SignedRequestHeaders.SignatureHeaderName]));
        Assert.Equal(64, signature.Length);
        var signingString = InstallRequestSigning.BuildSigningString(
            HttpMethod.Post, request.RequestUri!, timestamp, Encoding.UTF8.GetBytes(body));
        Assert.True(InstallRequestSigning.Verify(key, signingString, signature));
    }

    [Fact]
    public async Task TimestampComesFromInjectedTimeProvider()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var harness = new Harness(new KeyedIdentity(key), Now);
        harness.Time.Advance(TimeSpan.FromMinutes(7));

        using var response = await harness.Client.GetAsync("https://rr-admin-panel.pages.dev/api/usage/status?hwid=X");

        var sent = Assert.Single(harness.Inner.Requests);
        Assert.Equal(Now.AddMinutes(7).ToUnixTimeSeconds().ToString(), Assert.Single(sent.Headers[SignedRequestHeaders.TimestampHeaderName]));
    }

    [Fact]
    public async Task LeavesBodyAndContentHeadersIntact()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var harness = new Harness(new KeyedIdentity(key), Now);
        var body = """{"message":"hällo ✓"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://rr-admin-panel.pages.dev/api/feedback")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await harness.Client.SendAsync(request);

        var sent = Assert.Single(harness.Inner.Requests);
        Assert.Equal(body, sent.Body);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("utf-8", sent.ContentCharset);
    }

    [Fact]
    public async Task DoesNotSignRequestsToOtherHosts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new KeyedIdentity(key);
        using var harness = new Harness(identity, Now);

        using var response = await harness.Client.GetAsync("https://api.steampowered.com/ISteamApps/GetAppList/v2/");

        var sent = Assert.Single(harness.Inner.Requests);
        Assert.DoesNotContain(SignedRequestHeaders.InstallHeaderName, sent.Headers.Keys);
        Assert.DoesNotContain(SignedRequestHeaders.SignatureHeaderName, sent.Headers.Keys);
        Assert.Equal(0, identity.SignCallCount);
    }

    [Fact]
    public async Task SkipsTheRegistrationEndpoint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new KeyedIdentity(key);
        using var harness = new Harness(identity, Now);

        using var response = await harness.Client.PostAsync(
            "https://backend.rr-admin-panel.workers.dev/api/install/register",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        var sent = Assert.Single(harness.Inner.Requests);
        Assert.DoesNotContain(SignedRequestHeaders.InstallHeaderName, sent.Headers.Keys);
        Assert.Equal(0, identity.SignCallCount);
    }

    [Fact]
    public async Task SendsUnsignedWhenIdentityHasNoKey()
    {
        using var harness = new Harness(new KeyedIdentity(null), Now);

        using var response = await harness.Client.GetAsync("https://rr-admin-panel.pages.dev/api/announcements/active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(harness.Inner.Requests);
        Assert.DoesNotContain(SignedRequestHeaders.InstallHeaderName, sent.Headers.Keys);
    }

    [Fact]
    public async Task SendsUnsignedWhenSigningThrows()
    {
        using var harness = new Harness(new ThrowingIdentity(), Now);

        using var response = await harness.Client.GetAsync("https://rr-admin-panel.pages.dev/api/announcements/active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(harness.Inner.Requests);
    }

    [Fact]
    public void AllowedHostsComeFromTelemetryEndpointAndAdminPanel()
    {
        var hosts = SignedRequestHandler.AllowedHostsFrom(new AppConfiguration
        {
            Telemetry = new TelemetrySettings { Endpoint = "https://backend.rr-admin-panel.workers.dev/api/ingest" },
            AdminPanel = new AdminPanelSettings { BaseUrl = "https://rr-admin-panel.pages.dev" }
        });

        Assert.Equal(AllowedHosts, hosts);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(IInstallIdentityService identity, DateTimeOffset now)
        {
            Time = new ManualTimeProvider(now);
            Inner = new HeaderRecordingHandler();
            var handler = new SignedRequestHandler(() => identity, Time, AllowedHosts, NullLogger<SignedRequestHandler>.Instance)
            {
                InnerHandler = Inner
            };
            Client = new HttpClient(handler);
        }

        public ManualTimeProvider Time { get; }
        public HeaderRecordingHandler Inner { get; }
        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }

    public sealed record SentRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Body,
        string? ContentType,
        string? ContentCharset,
        Dictionary<string, string[]> Headers);

    public sealed class HeaderRecordingHandler : HttpMessageHandler
    {
        private readonly List<SentRequest> _requests = [];

        public IReadOnlyList<SentRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Add(new SentRequest(
                request.Method,
                request.RequestUri,
                body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content?.Headers.ContentType?.CharSet,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class KeyedIdentity(ECDsa? key) : IInstallIdentityService
    {
        private int _signCalls;

        public int SignCallCount => _signCalls;
        public bool IsRegistered => key is not null;

        public Task<InstallPublicKeyJwk?> GetPublicKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(key is null ? null : InstallRequestSigning.ToJwk(key));

        public Task EnsureRegisteredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SignedRequestHeaders?> SignAsync(HttpMethod method, Uri uri, byte[] body, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _signCalls);
            if (key is null)
            {
                return Task.FromResult<SignedRequestHeaders?>(null);
            }

            var timestamp = now.ToUnixTimeSeconds().ToString();
            var signingString = InstallRequestSigning.BuildSigningString(method, uri, timestamp, body);
            var signature = InstallRequestSigning.Sign(key, signingString);
            return Task.FromResult<SignedRequestHeaders?>(
                new SignedRequestHeaders(InstallId, timestamp, InstallRequestSigning.Base64UrlEncode(signature)));
        }
    }

    private sealed class ThrowingIdentity : IInstallIdentityService
    {
        public bool IsRegistered => false;

        public Task<InstallPublicKeyJwk?> GetPublicKeyAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public Task EnsureRegisteredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SignedRequestHeaders?> SignAsync(HttpMethod method, Uri uri, byte[] body, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
