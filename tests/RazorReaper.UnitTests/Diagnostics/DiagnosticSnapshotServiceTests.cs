using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;
using RazorReaper.Navigation;
using RazorReaper.Services;
using RazorReaper.Services.Diagnostics;
using RazorReaper.Services.Media;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Diagnostics;

public sealed class DiagnosticSnapshotServiceTests
{
    [Fact]
    public async Task CaptureEmitsExactRequiredProviderSetAndIsolatesFailuresAndTimeouts()
    {
        var providers = DiagnosticSnapshotService.RequiredProviderIds
            .Select(id => (IDiagnosticProvider)new FakeProvider(id))
            .ToList();
        Replace(providers, new FakeProvider("windows_host", failure: new InvalidOperationException("boom")));
        Replace(providers, new HangingProvider("ark_environment"));

        var service = new DiagnosticSnapshotService(
            providers,
            TimeProvider.System,
            NullLogger<DiagnosticSnapshotService>.Instance,
            TimeSpan.FromMilliseconds(40));

        var snapshot = await service.CaptureAsync("/desync?from=test");

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.True(snapshot.Consent);
        Assert.Equal(DiagnosticSnapshotService.RequiredProviderIds, snapshot.Providers.Select(p => p.Provider));
        Assert.Equal("error", snapshot.Providers.Single(p => p.Provider == "windows_host").Status);
        Assert.Equal("unavailable", snapshot.Providers.Single(p => p.Provider == "ark_environment").Status);
        Assert.All(snapshot.Providers, provider => Assert.InRange(provider.DurationMs ?? -1, 0, 120_000));
        Assert.InRange(DiagnosticSnapshotService.SerializedSize(snapshot), 1, DiagnosticSnapshotService.MaxSerializedBytes);
    }

    [Fact]
    public async Task MissingCollectorsStillProduceBackendCompleteUnavailableRows()
    {
        var service = new DiagnosticSnapshotService(
            [],
            TimeProvider.System,
            NullLogger<DiagnosticSnapshotService>.Instance,
            TimeSpan.FromMilliseconds(20));

        var snapshot = await service.CaptureAsync("feedback");

        Assert.Equal(12, snapshot.Providers.Count);
        Assert.All(snapshot.Providers, provider =>
        {
            Assert.Equal("unavailable", provider.Status);
            Assert.Empty(provider.Checks);
        });
    }

    [Fact]
    public async Task CategoryProvidersCoverEveryNavigationRouteAndEveryBuiltInScript()
    {
        var categoryProviders = new IDiagnosticProvider[]
        {
            new FeatureCatalogDiagnosticProvider("core_features", "Core"),
            new FeatureCatalogDiagnosticProvider("ark_tweaks", "ARK Tweaks"),
            new FeatureCatalogDiagnosticProvider("custom_ark", "Custom ARK"),
            new FeatureCatalogDiagnosticProvider("automation", "Automation", includeScripts: true),
            new FeatureCatalogDiagnosticProvider("mods_intel", "Mods & Intel"),
            new FeatureCatalogDiagnosticProvider("utilities", "Utilities"),
            new FeatureCatalogDiagnosticProvider("help_support", "Help & About"),
        };

        var reports = await Task.WhenAll(categoryProviders.Select(async provider =>
            await provider.CaptureAsync(new DiagnosticCaptureContext("feedback"))));
        var keys = reports.SelectMany(report => report.Checks).Select(check => check.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var page in NavCatalog.Pages)
        {
            var key = $"route_{NavCatalog.Normalize(page.Route).Replace('-', '_')}";
            Assert.Contains(key, keys);
        }

        Assert.Equal(17, keys.Count(key => key.StartsWith("script_", StringComparison.Ordinal)));
        Assert.DoesNotContain(reports.SelectMany(report => report.Checks), check => Equals(check.Value, "included"));
    }

    [Fact]
    public void ActivityClassifierRemovesNamesPathsCommandsAndServerAddresses()
    {
        Assert.Equal("App operation", SettingsOperationsDiagnosticProvider.ClassifyActivity(
            "Server saved: Secret Tribe (203.0.113.42:27015)"));
        Assert.Equal("App operation", SettingsOperationsDiagnosticProvider.ClassifyActivity(
            @"Opened folder: C:\Users\Someone\Private"));
        Assert.Equal("Desync failed: administrator required", SettingsOperationsDiagnosticProvider.ClassifyActivity(
            "Desync failed: administrator required"));
    }

    [Fact]
    public async Task FullFeatureManifestFitsBudgetWithoutDroppingUsefulRouteValues()
    {
        var providers = new List<IDiagnosticProvider>
        {
            new SizedProvider("app_runtime", 8),
            new SizedProvider("windows_host", 7),
            new SizedProvider("identity_license_access", 7),
            new SizedProvider("ark_environment", 6),
            new FeatureCatalogDiagnosticProvider("core_features", "Core"),
            new FeatureCatalogDiagnosticProvider("ark_tweaks", "ARK Tweaks"),
            new FeatureCatalogDiagnosticProvider("custom_ark", "Custom ARK"),
            new FeatureCatalogDiagnosticProvider("automation", "Automation", includeScripts: true),
            new FeatureCatalogDiagnosticProvider("mods_intel", "Mods & Intel"),
            new FeatureCatalogDiagnosticProvider("utilities", "Utilities"),
            new FeatureCatalogDiagnosticProvider("help_support", "Help & About"),
            new SizedProvider("settings_operations", 16),
        };
        var service = new DiagnosticSnapshotService(
            providers,
            TimeProvider.System,
            NullLogger<DiagnosticSnapshotService>.Instance,
            TimeSpan.FromSeconds(1));

        var snapshot = await service.CaptureAsync("troubleshoot");

        Assert.InRange(DiagnosticSnapshotService.SerializedSize(snapshot), 1, DiagnosticSnapshotService.MaxSerializedBytes);
        var featureChecks = snapshot.Providers
            .Where(provider => provider.Provider is "core_features" or "ark_tweaks" or "custom_ark" or
                "automation" or "mods_intel" or "utilities" or "help_support")
            .SelectMany(provider => provider.Checks);
        Assert.All(featureChecks, check => Assert.NotNull(check.Value));
    }

    [Theory]
    [InlineData("warning", "warning", "warning")]
    [InlineData("error", "fail", "error")]
    public async Task SettingsOperationsProviderReflectsRecentOperationFailures(
        string activityType,
        string expectedCheckStatus,
        string expectedProviderStatus)
    {
        var provider = new SettingsOperationsDiagnosticProvider(
            new FakePreferencesStore(),
            new StubActivityService(
            [
                new ActivityItem
                {
                    Title = "Desync failed: firewall rule removal",
                    Type = activityType,
                    Timestamp = DateTime.Now,
                },
            ]),
            Options.Create(new AppConfiguration()));

        var report = await provider.CaptureAsync(new DiagnosticCaptureContext("troubleshoot"));

        var operation = Assert.Single(report.Checks, check => check.Key == "operation_1");
        Assert.Equal(expectedCheckStatus, operation.Status);
        Assert.Equal(expectedProviderStatus, report.Status);
    }

    [Fact]
    public async Task CustomArkProviderWarnsForMissingConverterAndCharacterFolder()
    {
        var arkRoot = Path.Combine(Path.GetTempPath(), "RazorReaper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(arkRoot, "ShooterGame", "Content", "Movies"));
        try
        {
            var provider = new FeatureCatalogDiagnosticProvider(
                "custom_ark",
                "Custom ARK",
                arkPaths: new StubArkPathProvider(arkRoot),
                ffmpeg: new StubFfmpegProvider(isInstalled: false));

            var report = await provider.CaptureAsync(new DiagnosticCaptureContext("feedback"));

            var loadingScreen = Assert.Single(report.Checks, check => check.Key == "route_loading_screen");
            Assert.Equal("warning", loadingScreen.Status);
            Assert.Equal("movies found; converter pending", loadingScreen.Value);

            var charManager = Assert.Single(report.Checks, check => check.Key == "route_char_manager");
            Assert.Equal("warning", charManager.Status);
            Assert.Equal("SavedArksLocal missing", charManager.Value);
            Assert.Equal("warning", report.Status);
        }
        finally
        {
            if (Directory.Exists(arkRoot)) Directory.Delete(arkRoot, recursive: true);
        }
    }

    private static void Replace(List<IDiagnosticProvider> providers, IDiagnosticProvider replacement)
    {
        var index = providers.FindIndex(provider => provider.ProviderId == replacement.ProviderId);
        providers[index] = replacement;
    }

    private sealed class FakeProvider(string id, Exception? failure = null) : IDiagnosticProvider
    {
        public string ProviderId => id;

        public Task<DiagnosticProviderData> CaptureAsync(
            DiagnosticCaptureContext context,
            CancellationToken cancellationToken = default)
        {
            if (failure is not null) throw failure;
            return Task.FromResult(new DiagnosticProviderData(
                "ok",
                [new DiagnosticCheck { Key = "state", Label = "State", Status = "pass", Value = context.SourceRoute }]));
        }
    }

    private sealed class HangingProvider(string id) : IDiagnosticProvider
    {
        public string ProviderId => id;

        public Task<DiagnosticProviderData> CaptureAsync(
            DiagnosticCaptureContext context,
            CancellationToken cancellationToken = default)
            => new TaskCompletionSource<DiagnosticProviderData>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    private sealed class SizedProvider(string id, int checkCount) : IDiagnosticProvider
    {
        public string ProviderId => id;

        public Task<DiagnosticProviderData> CaptureAsync(
            DiagnosticCaptureContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DiagnosticProviderData(
                "ok",
                Enumerable.Range(1, checkCount).Select(index => new DiagnosticCheck
                {
                    Key = $"check_{index}",
                    Label = $"Useful check {index}",
                    Status = "pass",
                    Value = index,
                }).ToArray(),
                "Snapshot ready"));
    }

    private sealed class StubActivityService(IReadOnlyList<ActivityItem> activities) : IActivityService
    {
        public event EventHandler<ActivityItem>? ActivityAdded { add { } remove { } }

        public void AddActivity(string title, string type = "info") => throw new NotSupportedException();
        public IReadOnlyList<ActivityItem> GetRecentActivities() => activities;
        public void ClearActivities() => throw new NotSupportedException();
    }

    private sealed class StubArkPathProvider(string arkRoot) : IArkPathProvider
    {
        public string? FindArkPath() => arkRoot;
        public string? GetBaseDeviceProfilesPath() => null;
        public bool IsValidArkPath(string path) => string.Equals(path, arkRoot, StringComparison.Ordinal);
    }

    private sealed class StubFfmpegProvider(bool isInstalled) : IFfmpegProvider
    {
        public bool IsInstalled => isInstalled;
        public string FfmpegPath => string.Empty;

        public Task<string?> EnsureAsync(
            IProgress<FfmpegSetupProgress>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
