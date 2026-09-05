using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Diagnostics;
using RazorReaper.Services;
using RazorReaper.Services.Diagnostics;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Diagnostics;

public sealed class FeedbackServiceDiagnosticsTests
{
    [Fact]
    public async Task MessageFeedbackPreservesExistingFieldsAddsDiagnosticsAndReturnsReportId()
    {
        using var handler = Handler("""{"ok":true,"message":"received","report_id":"FB-000123"}""");
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateService(client, new SnapshotService());

        var result = await service.SubmitWithDiagnosticsAsync("  Desync does not work  ", "  tester  ", "desync");

        Assert.True(result.Success);
        Assert.Equal("FB-000123", result.ReportId);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var root = body.RootElement;
        Assert.Equal("Desync does not work", root.GetProperty("message").GetString());
        Assert.Equal("tester", root.GetProperty("contact").GetString());
        Assert.Equal("HWID-TEST", root.GetProperty("hwid").GetString());
        Assert.Equal("6b591417-93ab-4411-b18e-e46080ef0025", root.GetProperty("install_id").GetString());
        Assert.Equal("RRRR-TEST-KEY", root.GetProperty("license_key").GetString());
        Assert.True(root.TryGetProperty("machine_name", out _));
        Assert.Equal(AppVersionInfo.VersionString, root.GetProperty("app_version").GetString());
        Assert.True(root.TryGetProperty("platform", out _));
        Assert.Equal(1, root.GetProperty("diagnostics").GetProperty("schema_version").GetInt32());
        Assert.Equal(12, root.GetProperty("diagnostics").GetProperty("providers").GetArrayLength());
    }

    [Fact]
    public async Task DiagnosticsIncludesTheWrittenDescription()
    {
        using var handler = Handler("""{"ok":true,"message":"received","report_id":"FB-7"}""");
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateService(client, new SnapshotService());

        var result = await service.SubmitDiagnosticsAsync("The game does not start", null, "troubleshoot");

        Assert.True(result.Success);
        Assert.Equal("FB-7", result.ReportId);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("The game does not start", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("contact").ValueKind);
        Assert.True(body.RootElement.TryGetProperty("diagnostics", out _));
    }

    [Fact]
    public async Task DiagnosticsOnlySendsNothingWhenSnapshotCollectionFails()
    {
        using var handler = Handler("""{"ok":true,"message":"unexpected"}""");
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateService(client, new ThrowingSnapshotService());

        var result = await service.SubmitDiagnosticsAsync("The game does not start", null, "home");

        Assert.False(result.Success);
        Assert.Contains("Nothing was sent", result.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \t\r\n")]
    public async Task EmptyDescriptionsNeverCollectOrSendData(string message)
    {
        using var handler = Handler("""{"ok":true}""");
        using var client = new HttpClient(handler, disposeHandler: false);
        var diagnostics = new CountingSnapshotService();
        var service = CreateService(client, diagnostics);
        Assert.False((await service.SubmitAsync(message, null)).Success);
        Assert.False((await service.SubmitWithDiagnosticsAsync(message, null, "feedback")).Success);
        Assert.False((await service.SubmitDiagnosticsAsync(message, null, "home")).Success);
        Assert.Equal(0, diagnostics.CallCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyFeedbackStillSendsWithoutDiagnosticsOrInvokingCollector()
    {
        using var handler = Handler("""{"ok":true,"message":"received"}""");
        using var client = new HttpClient(handler, disposeHandler: false);
        var diagnostics = new CountingSnapshotService();
        var service = CreateService(client, diagnostics);

        var result = await service.SubmitAsync("Legacy feedback", null);

        Assert.True(result.Success);
        Assert.Equal(0, diagnostics.CallCount);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.False(body.RootElement.TryGetProperty("diagnostics", out _));
    }

    [Fact]
    public async Task ValidLongMultibyteFeedbackRetainsDiagnostics()
    {
        using var handler = Handler("""{"ok":true,"message":"received","report_id":"FB-LONG"}""");
        using var client = new HttpClient(handler, disposeHandler: false);
        var service = CreateService(client, new SnapshotService());
        var message = new string('\u754c', 4000);

        var result = await service.SubmitWithDiagnosticsAsync(message, null, "feedback");

        Assert.True(result.Success);
        var requestBody = Assert.Single(handler.Requests).Body!;
        Assert.True(Encoding.UTF8.GetByteCount(requestBody) > 16 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(requestBody) < 48 * 1024);
        using var body = JsonDocument.Parse(requestBody);
        var diagnostics = body.RootElement.GetProperty("diagnostics");
        var firstCheck = diagnostics.GetProperty("providers")[0].GetProperty("checks")[0];
        Assert.Equal("feedback", firstCheck.GetProperty("value").GetString());
    }

    private static RecordingHttpMessageHandler Handler(string responseJson)
        => new()
        {
            ResponseFactory = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            }),
        };

    private static FeedbackService CreateService(HttpClient client, IDiagnosticSnapshotService diagnostics)
        => new(
            client,
            new StubIdentityService(),
            new StubLicenseService(),
            Options.Create(new AppConfiguration
            {
                AdminPanel = new AdminPanelSettings
                {
                    BaseUrl = "https://example.invalid",
                    RequestTimeoutSeconds = 3,
                },
            }),
            diagnostics,
            NullLogger<FeedbackService>.Instance);

    private sealed class SnapshotService : IDiagnosticSnapshotService
    {
        public Task<FeedbackDiagnostics> CaptureAsync(string? sourceRoute, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSnapshot(sourceRoute));
    }

    private sealed class CountingSnapshotService : IDiagnosticSnapshotService
    {
        public int CallCount { get; private set; }

        public Task<FeedbackDiagnostics> CaptureAsync(string? sourceRoute, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CreateSnapshot(sourceRoute));
        }
    }

    private sealed class ThrowingSnapshotService : IDiagnosticSnapshotService
    {
        public Task<FeedbackDiagnostics> CaptureAsync(string? sourceRoute, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("collector failed");
    }

    private static FeedbackDiagnostics CreateSnapshot(string? sourceRoute)
        => new()
        {
            GeneratedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            Providers = DiagnosticSnapshotService.RequiredProviderIds.Select(id => new DiagnosticProviderReport
            {
                Provider = id,
                Version = "1",
                Status = "ok",
                Checks =
                [
                    new DiagnosticCheck
                    {
                        Key = "source",
                        Label = "Source",
                        Status = "pass",
                        Value = sourceRoute ?? "feedback",
                    },
                ],
            }).ToArray(),
        };

    private sealed class StubIdentityService : IClientIdentityService
    {
        private static readonly ClientIdentity Identity = new(
            "6b591417-93ab-4411-b18e-e46080ef0025",
            "HWID-TEST");

        public ClientIdentity GetIdentity() => Identity;
        public ClientIdentity RotateInstallId() => Identity;
    }

    private sealed class StubLicenseService : ILicenseService
    {
        public bool IsActivated => true;
        public bool IsPremium => true;
        public bool IsFreeTier => false;
        public string CurrentLicenseKey => "RRRR-TEST-KEY";
        public string? ExpiresAt => null;
        public string? LicenseType => "lifetime";
        public event Action OnLicenseStateChanged { add { } remove { } }
        public event Action OnLicenseActivated { add { } remove { } }
        public Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey) => throw new NotSupportedException();
        public Task<(bool Success, string Message)> ValidateLicenseAsync() => throw new NotSupportedException();
    }
}
