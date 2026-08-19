using System.Reflection;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;

namespace RazorReaper.UnitTests.Identity;

public sealed class LocalPreviewIdentityCompositionTests
{
    [Fact]
    public void LocalPreviewCompositionPlansInertIdentityTelemetryAndUsage()
    {
        var plan = LocalPreviewComposition.CreatePlan(new StubRunMode(true));

        Assert.Equal(typeof(LocalPreviewClientIdentityService), plan.ServiceTypes[typeof(IClientIdentityService)]);
        Assert.Equal(typeof(LocalPreviewTelemetryService), plan.ServiceTypes[typeof(ITelemetryService)]);
        Assert.Equal(typeof(LocalPreviewUsageGateService), plan.ServiceTypes[typeof(IUsageGateService)]);
    }

    [Fact]
    public void LocalPreviewNormalCompositionPlansProductionIdentityTelemetryAndUsage()
    {
        var plan = LocalPreviewComposition.CreatePlan(new StubRunMode(false));

        Assert.Equal(typeof(ClientIdentityService), plan.ServiceTypes[typeof(IClientIdentityService)]);
        Assert.Equal(typeof(TelemetryService), plan.ServiceTypes[typeof(ITelemetryService)]);
        Assert.Equal(typeof(UsageGateService), plan.ServiceTypes[typeof(IUsageGateService)]);
    }

    [Fact]
    public void LocalPreviewMauiCompositionKeepsInertServicesAsFinalRegistrations()
    {
        var provider = BuildMauiServiceProvider(new StubRunMode(true), out var lease);
        using (lease)
        {
            Assert.IsType<LocalPreviewClientIdentityService>(provider.GetService(typeof(IClientIdentityService)));
            Assert.IsType<LocalPreviewTelemetryService>(provider.GetService(typeof(ITelemetryService)));
            Assert.IsType<LocalPreviewUsageGateService>(provider.GetService(typeof(IUsageGateService)));
        }
    }

    [Fact]
    public async Task LocalPreviewInertServicesExposeNoProductionIdentityTelemetryOrUsageData()
    {
        var provider = BuildMauiServiceProvider(new StubRunMode(true), out var lease);
        using (lease)
        {
            var identity = Assert.IsType<LocalPreviewClientIdentityService>(
                provider.GetService(typeof(IClientIdentityService)));
            var telemetry = Assert.IsType<LocalPreviewTelemetryService>(
                provider.GetService(typeof(ITelemetryService)));
            var usage = Assert.IsType<LocalPreviewUsageGateService>(
                provider.GetService(typeof(IUsageGateService)));

            var snapshot = identity.GetIdentity();
            await telemetry.StartAsync();
            await telemetry.TrackEventAsync(
                "process_start",
                TelemetryEventStatus.Down,
                "must stay local",
                new Dictionary<string, object?> { ["private"] = "value" });
            await telemetry.StopAsync();
            var consume = await usage.TryConsumeAsync(UsageFeatures.SkyChanger);
            var status = await usage.GetStatusAsync();

            Assert.Equal("00000000-0000-0000-0000-000000000000", snapshot.InstallId);
            Assert.Equal("00000000000000000000000000000000", snapshot.HardwareId);
            Assert.False(consume.Allowed);
            Assert.False(consume.Unlimited);
            Assert.Equal(0, consume.Remaining);
            Assert.Null(consume.Limit);
            Assert.Null(status);
        }
    }

    private static IServiceProvider BuildMauiServiceProvider(
        IAppRunMode runMode,
        out IDisposable lease)
    {
        var serviceCollectionType = Type.GetType(
            "Microsoft.Extensions.DependencyInjection.ServiceCollection, Microsoft.Extensions.DependencyInjection.Abstractions",
            throwOnError: true)!;
        var services = Activator.CreateInstance(serviceCollectionType)!;
        var configureServices = typeof(MauiProgram).GetMethod(
            "ConfigureServices",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(configureServices);

        configureServices.Invoke(null, [services, runMode]);

        var builderExtensions = Type.GetType(
            "Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions, Microsoft.Extensions.DependencyInjection",
            throwOnError: true)!;
        var buildServiceProvider = Assert.Single(builderExtensions.GetMethods(
            BindingFlags.Static | BindingFlags.Public),
            method => method.Name == "BuildServiceProvider" && method.GetParameters().Length == 1);
        var provider = buildServiceProvider.Invoke(null, [services]);
        lease = Assert.IsAssignableFrom<IDisposable>(provider);
        return Assert.IsAssignableFrom<IServiceProvider>(provider);
    }

    private sealed class StubRunMode(bool isLocalPreview) : IAppRunMode
    {
        public bool IsLocalPreview { get; } = isLocalPreview;
    }
}
