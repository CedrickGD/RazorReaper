using RazorReaper.Navigation;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.Services.Overlay;

namespace RazorReaper.UnitTests;

public sealed class LocalPreviewStartupTests
{
    private static readonly string[] ExpectedStartupOrder =
    [
        "font-install",
        "scope-mode",
        "update-check",
        "telemetry-start",
        "access-gate",
        "discord-rpc",
        "ark-link",
    ];

    [Fact]
    public void PreviewStartupDoesNotCreateOrQueueProductionIntegrations()
    {
        var factoryCalls = 0;
        var queuedNames = new List<string>();

        AppStartupPolicy.Queue(
            new StubRunMode(true),
            () =>
            {
                factoryCalls++;
                return CreateStartupActions([]);
            },
            (name, _) => queuedNames.Add(name));

        Assert.Equal(0, factoryCalls);
        Assert.Empty(queuedNames);
    }

    [Fact]
    public void NormalStartupCreatesEveryIntegrationOnceInExistingOrder()
    {
        var factoryCalls = 0;
        var invokedActions = new List<string>();
        var queuedNames = new List<string>();

        AppStartupPolicy.Queue(
            new StubRunMode(false),
            () =>
            {
                factoryCalls++;
                return CreateStartupActions(invokedActions);
            },
            (name, action) =>
            {
                queuedNames.Add(name);
                action().GetAwaiter().GetResult();
            });

        Assert.Equal(1, factoryCalls);
        Assert.Equal(ExpectedStartupOrder, queuedNames);
        Assert.Equal(ExpectedStartupOrder, invokedActions);
    }

    [Fact]
    public async Task PreviewLayoutSkipsEveryProductionIntegrationAndAccessBlock()
    {
        var calls = new Dictionary<string, int>();
        var actions = CreateLayoutActions(calls);

        await LocalPreviewLayoutPolicy.RunBeforeLocalNavigationAsync(new StubRunMode(true), actions);
        LocalPreviewLayoutPolicy.RunAfterLocalNavigation(new StubRunMode(true), actions);
        var shouldRenderBlock = LocalPreviewLayoutPolicy.ShouldRenderAccessBlock(
            new StubRunMode(true),
            () =>
            {
                Increment(calls, "access-block");
                return true;
            });

        Assert.False(shouldRenderBlock);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task NormalLayoutInvokesEveryExistingIntegrationOnce()
    {
        var calls = new Dictionary<string, int>();
        var actions = CreateLayoutActions(calls);

        await LocalPreviewLayoutPolicy.RunBeforeLocalNavigationAsync(new StubRunMode(false), actions);
        LocalPreviewLayoutPolicy.RunAfterLocalNavigation(new StubRunMode(false), actions);
        var shouldRenderBlock = LocalPreviewLayoutPolicy.ShouldRenderAccessBlock(
            new StubRunMode(false),
            () =>
            {
                Increment(calls, "access-block");
                return true;
            });

        Assert.True(shouldRenderBlock);
        Assert.Equal(1, calls["hud-session-resolution"]);
        Assert.Equal(1, calls["version-detection"]);
        Assert.Equal(1, calls["license-validation"]);
        Assert.Equal(1, calls["access-subscription"]);
        Assert.Equal(1, calls["discord-reflection"]);
        Assert.Equal(1, calls["navigation-subscription"]);
        Assert.Equal(1, calls["access-block"]);
    }

    [Fact]
    public void PreviewShutdownDoesNotCreateOrInvokeProductionActions()
    {
        var factoryCalls = 0;
        var calls = new Dictionary<string, int>();

        AppShutdownPolicy.Run(
            new StubRunMode(true),
            () =>
            {
                factoryCalls++;
                return CreateShutdownActions(calls);
            });

        Assert.Equal(0, factoryCalls);
        Assert.Empty(calls);
    }

    [Fact]
    public void NormalShutdownInvokesEverySuppliedProductionActionOnce()
    {
        var calls = new Dictionary<string, int>();

        AppShutdownPolicy.Run(new StubRunMode(false), () => CreateShutdownActions(calls));

        Assert.Equal(1, calls["updater-handoff"]);
        Assert.Equal(1, calls["discord-shutdown"]);
        Assert.Equal(1, calls["telemetry-stop"]);
        Assert.Equal(1, calls["telemetry-track"]);
    }

    [Fact]
    public void PreviewCompositionUsesSameRunModeAndOnlyInertIntegrationTypes()
    {
        var runMode = new StubRunMode(true);

        var plan = LocalPreviewComposition.CreatePlan(runMode);

        Assert.Same(runMode, plan.RunMode);
        Assert.Equal(14, plan.ServiceTypes.Count);
        AssertRegistration<IClientIdentityService, LocalPreviewClientIdentityService>(plan);
        AssertRegistration<ITelemetryService, LocalPreviewTelemetryService>(plan);
        AssertRegistration<IUsageGateService, LocalPreviewUsageGateService>(plan);
        AssertRegistration<IUpdateService, LocalPreviewUpdateService>(plan);
        AssertRegistration<IAutoUpdateManager, LocalPreviewAutoUpdateManager>(plan);
        AssertRegistration<IDiscordPresenceService, LocalPreviewDiscordPresenceService>(plan);
        AssertRegistration<ILicenseService, LocalPreviewLicenseService>(plan);
        AssertRegistration<IAccessGateService, LocalPreviewAccessGateService>(plan);
        AssertRegistration<IArkLinkService, LocalPreviewArkLinkService>(plan);
        AssertRegistration<IPaletteCommandProvider, LocalPreviewPaletteCommandProvider>(plan);
        AssertRegistration<IHudOverlayService, LocalPreviewHudOverlayService>(plan);
        AssertRegistration<ISessionHudService, LocalPreviewSessionHudService>(plan);
        AssertRegistration<IGameIniService, LocalPreviewGameIniService>(plan);
        AssertRegistration<ILineListService, LocalPreviewLineListService>(plan);
    }

    [Fact]
    public void NormalCompositionRetainsExactProductionIntegrationTypes()
    {
        var runMode = new StubRunMode(false);

        var plan = LocalPreviewComposition.CreatePlan(runMode);

        Assert.Same(runMode, plan.RunMode);
        Assert.Equal(14, plan.ServiceTypes.Count);
        AssertRegistration<IClientIdentityService, ClientIdentityService>(plan);
        AssertRegistration<ITelemetryService, TelemetryService>(plan);
        AssertRegistration<IUsageGateService, UsageGateService>(plan);
        AssertRegistration<IUpdateService, UpdateService>(plan);
        AssertRegistration<IAutoUpdateManager, AutoUpdateManager>(plan);
        AssertRegistration<IDiscordPresenceService, DiscordPresenceService>(plan);
        AssertRegistration<ILicenseService, LicenseService>(plan);
        AssertRegistration<IAccessGateService, AccessGateService>(plan);
        AssertRegistration<IArkLinkService, ArkLinkService>(plan);
        AssertRegistration<IPaletteCommandProvider, PaletteCommandProvider>(plan);
        AssertRegistration<IHudOverlayService, HudOverlayService>(plan);
        AssertRegistration<ISessionHudService, SessionHudService>(plan);
        AssertRegistration<IGameIniService, GameIniService>(plan);
        AssertRegistration<ILineListService, LineListService>(plan);
    }

    [Fact]
    public async Task PreviewReplacementsExposeOnlyDeterministicInertState()
    {
        IUpdateService versionOnlyUpdate = new LocalPreviewUpdateService();
        var versionOnlyResult = await versionOnlyUpdate.CheckForUpdatesAsync();
        Assert.Equal(versionOnlyUpdate.CurrentVersion, versionOnlyResult.CurrentVersion);
        Assert.Equal(DateTimeOffset.UnixEpoch, versionOnlyResult.CheckedAt);
        Assert.False(versionOnlyResult.HasUpdate);
        Assert.Equal("Update checks are disabled in local preview.", versionOnlyResult.ErrorMessage);

        IAutoUpdateManager updater = new LocalPreviewAutoUpdateManager();
        await updater.RunStartupCheckAsync();
        Assert.False(updater.IsChecking);
        Assert.False(updater.IsDownloading);
        Assert.False(updater.IsInstallerReady);
        Assert.Null(updater.DownloadProgressPercent);
        Assert.Null(updater.PendingVersion);
        Assert.Null(updater.LastCheckResult);
        Assert.Equal("Update checks are disabled in local preview.", updater.StatusMessage);
        Assert.False(updater.LaunchPendingInstaller());
        Assert.Null(updater.DetectVersionUpgrade());

        ILicenseService license = new LocalPreviewLicenseService();
        var validation = await license.ValidateLicenseAsync();
        var activation = await license.ActivateLicenseAsync("not-used");
        Assert.False(validation.Success);
        Assert.False(activation.Success);
        Assert.False(license.IsActivated);
        Assert.False(license.IsPremium);
        Assert.True(license.IsFreeTier);
        Assert.Equal(string.Empty, license.CurrentLicenseKey);
        Assert.Null(license.ExpiresAt);
        Assert.Null(license.LicenseType);

        IAccessGateService access = new LocalPreviewAccessGateService();
        await access.StartAsync();
        Assert.False(await access.CheckNowAsync());
        Assert.False(access.IsSuspended);
        Assert.Null(access.Mode);
        Assert.Null(access.Reason);
        Assert.Null(access.BannedUntil);

        IDiscordPresenceService discord = new LocalPreviewDiscordPresenceService();
        discord.Initialize();
        discord.SetActivityForPath("home");
        discord.SetActivityLabel("Home");
        discord.SetMinimizedToTray(true);
        discord.Shutdown();
        Assert.False(discord.IsEnabled);

        IArkLinkService arkLink = new LocalPreviewArkLinkService();
        arkLink.Start();
        arkLink.StartWithArk = true;
        arkLink.CloseWithArk = true;
        Assert.False(arkLink.StartWithArk);
        Assert.False(arkLink.CloseWithArk);

        IPaletteCommandProvider palette = new LocalPreviewPaletteCommandProvider();
        var previewPages = palette.GetCommands();
        Assert.NotEmpty(previewPages);
        Assert.All(previewPages, item =>
        {
            Assert.Equal(PaletteKind.Page, item.Kind);
            Assert.NotNull(item.Route);
            Assert.Null(item.Invoke);
            Assert.Null(item.Status);
        });
        Assert.Contains(previewPages, item => item.Route == "/home" && item.Title == "Home");
        Assert.Contains(previewPages, item => item.Route == "/ini-builder" && item.Title == "INI Builder");

        IGameIniService gameIni = new LocalPreviewGameIniService();
        Assert.NotEmpty(gameIni.GetBuiltInPresets());
        Assert.Null(gameIni.GetIniPath(GameIniTarget.GameUserSettings));
        Assert.False(gameIni.IniFileExists(GameIniTarget.GameUserSettings));
        Assert.False(gameIni.IsArkRunning());
        Assert.Empty(gameIni.ListBackups());
        Assert.Null(await gameIni.LoadDraftAsync());

        ILineListService lineList = new LocalPreviewLineListService();
        var previewLines = await lineList.LoadAsync();
        Assert.NotEmpty(previewLines.Lines);
        Assert.All(previewLines.Lines, line => Assert.StartsWith("preview-", line.Id, StringComparison.Ordinal));
        Assert.False(await lineList.AddLineAsync(new BreedingLine()));

        IHudOverlayService hud = new LocalPreviewHudOverlayService();
        var suppliedSettings = new HudSettings { Enabled = true };
        hud.Start();
        hud.Toggle();
        hud.SetMoveMode(true);
        hud.UpdateSettings(suppliedSettings);
        hud.SetAccent(1, 2, 3);
        hud.SetActiveTool("Preview");
        hud.SetServerInfo("Private", 1, 2, 3);
        hud.SetDesync(DateTime.UtcNow);
        hud.PushAlert("Preview");
        hud.TestAlert();
        hud.ResetSessionTimer();
        hud.SetSessionStart(DateTime.UtcNow);
        hud.Stop();
        Assert.False(hud.IsRunning);
        Assert.False(hud.IsMoveMode);
        Assert.False(hud.Settings.Enabled);
        Assert.Empty(hud.GetMonitors());
        Assert.Empty(hud.GetRecentAlerts());
        hud.Dispose();

        ISessionHudService sessionHud = new LocalPreviewSessionHudService();
        sessionHud.Dispose();
    }

    [Fact]
    public void PreviewSearchAndHudRegistrationsCannotConstructAutomationOrWatcherGraphs()
    {
        var plan = LocalPreviewComposition.CreatePlan(new StubRunMode(true));

        AssertParameterless(plan.ServiceTypes[typeof(IClientIdentityService)]);
        AssertParameterless(plan.ServiceTypes[typeof(ITelemetryService)]);
        AssertParameterless(plan.ServiceTypes[typeof(IUsageGateService)]);
        AssertParameterless(plan.ServiceTypes[typeof(IUpdateService)]);
        AssertParameterless(plan.ServiceTypes[typeof(IPaletteCommandProvider)]);
        AssertParameterless(plan.ServiceTypes[typeof(IHudOverlayService)]);
        AssertParameterless(plan.ServiceTypes[typeof(ISessionHudService)]);
        AssertParameterless(plan.ServiceTypes[typeof(IGameIniService)]);
        AssertParameterless(plan.ServiceTypes[typeof(ILineListService)]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WindowsBootstrapWiresProductionIntegrationsOnlyOutsidePreview(bool isPreview, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsBootstrapPolicy.ShouldWireIntegrations(
                authoritativeLocalPreview: isPreview,
                registeredRunMode: new StubRunMode(isPreview)));
    }

    [Fact]
    public void WindowsBootstrapFailsClosedForAuthoritativeOrRegisteredPreview()
    {
        Assert.False(WindowsBootstrapPolicy.ShouldWireIntegrations(
            authoritativeLocalPreview: true,
            registeredRunMode: null));
        Assert.False(WindowsBootstrapPolicy.ShouldWireIntegrations(
            authoritativeLocalPreview: true,
            registeredRunMode: new StubRunMode(false)));
        Assert.False(WindowsBootstrapPolicy.ShouldWireIntegrations(
            authoritativeLocalPreview: false,
            registeredRunMode: new StubRunMode(true)));
        Assert.True(WindowsBootstrapPolicy.ShouldWireIntegrations(
            authoritativeLocalPreview: false,
            registeredRunMode: null));
        Assert.True(WindowsBootstrapPolicy.ShouldWireIntegrations(
            authoritativeLocalPreview: false,
            registeredRunMode: new StubRunMode(false)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void LoggingPolicyKeepsPreviewAwayFromProductionPreferencesAndFiles(
        bool isPreview,
        bool expectedProductionDiagnostics,
        bool expectedFileSink)
    {
        var plan = LocalPreviewLoggingPolicy.CreatePlan(new StubRunMode(isPreview));

        Assert.Equal(expectedProductionDiagnostics, plan.UseProductionDiagnostics);
        Assert.Equal(expectedFileSink, plan.UseFileSink);
    }

    [Fact]
    public void WebViewProfilePreparationFailureAbortsPreviewStartup()
    {
        var expected = new IOException("isolated profile unavailable");
        var calls = 0;

        var actual = Assert.Throws<IOException>(() =>
            LocalPreviewWebViewProfilePolicy.Prepare(
                new StubRunMode(true),
                () =>
                {
                    calls++;
                    throw expected;
                }));

        Assert.Same(expected, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WebViewProfilePreparationFailureKeepsNormalFallback()
    {
        var calls = 0;

        LocalPreviewWebViewProfilePolicy.Prepare(
            new StubRunMode(false),
            () =>
            {
                calls++;
                throw new IOException("custom profile unavailable");
            });

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true, "/credits", false)]
    [InlineData(false, "/", true)]
    public void LaunchPolicySelectsSafeStartAndCacheBehavior(
        bool isPreview,
        string expectedStartPath,
        bool expectedProductionCacheMapping)
    {
        var runMode = new StubRunMode(isPreview);

        Assert.Equal(expectedStartPath, LocalPreviewLaunchPolicy.GetStartPath(runMode));
        Assert.Equal(
            expectedProductionCacheMapping,
            LocalPreviewLaunchPolicy.ShouldMapProductionMediaCaches(runMode));
        Assert.Equal(
            !isPreview,
            LocalPreviewLaunchPolicy.ShouldConsumeElevationReturnRoute(runMode));
        if (isPreview)
        {
            Assert.NotEqual("/", expectedStartPath);
            Assert.NotEqual("/home", expectedStartPath);
        }
    }

    [Fact]
    public void PreviewAndNormalWindowsSynchronizationNamesAreDisjoint()
    {
        var normal = WindowsBootstrapPolicy.GetSynchronizationNames(isLocalPreview: false);
        var preview = WindowsBootstrapPolicy.GetSynchronizationNames(isLocalPreview: true);

        Assert.Equal("RazorReaper_SingleInstance_Mutex", normal.MutexName);
        Assert.Equal("RazorReaper_ShowWindow_Event", normal.ShowEventName);
        Assert.NotEqual(normal.MutexName, preview.MutexName);
        Assert.NotEqual(normal.ShowEventName, preview.ShowEventName);
        Assert.Contains("LocalPreview", preview.MutexName, StringComparison.Ordinal);
        Assert.Contains("LocalPreview", preview.ShowEventName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, "WebView2-local-preview")]
    [InlineData(true, true, "WebView2-local-preview")]
    [InlineData(false, false, "WebView2")]
    [InlineData(false, true, "WebView2-admin")]
    public void WebViewProfileKeepsPreviewSeparateFromProduction(bool isPreview, bool isElevated, string expected)
    {
        Assert.Equal(expected, MauiProgram.GetWebView2FolderName(new StubRunMode(isPreview), isElevated));
    }

    private static AppStartupActions CreateStartupActions(ICollection<string> calls) => new(
        FontInstall: () => RecordAsync(calls, "font-install"),
        ScopeMode: () => RecordAsync(calls, "scope-mode"),
        UpdateCheck: () => RecordAsync(calls, "update-check"),
        TelemetryStart: () => RecordAsync(calls, "telemetry-start"),
        AccessGate: () => RecordAsync(calls, "access-gate"),
        DiscordRpc: () => RecordAsync(calls, "discord-rpc"),
        ArkLink: () => RecordAsync(calls, "ark-link"));

    private static LocalPreviewLayoutActions CreateLayoutActions(IDictionary<string, int> calls) => new(
        ResolveHudAndSession: () => Increment(calls, "hud-session-resolution"),
        DetectVersionUpgrade: () => Increment(calls, "version-detection"),
        ValidateLicenseAsync: () =>
        {
            Increment(calls, "license-validation");
            return Task.CompletedTask;
        },
        SubscribeAccess: () => Increment(calls, "access-subscription"),
        ReflectDiscordAndHud: () => Increment(calls, "discord-reflection"),
        SubscribeNavigation: () => Increment(calls, "navigation-subscription"));

    private static AppShutdownActions CreateShutdownActions(IDictionary<string, int> calls) => new(
        UpdaterHandoff: () => Increment(calls, "updater-handoff"),
        DiscordShutdown: () => Increment(calls, "discord-shutdown"),
        TelemetryStop: () => Increment(calls, "telemetry-stop"),
        TelemetryTrack: () => Increment(calls, "telemetry-track"));

    private static Task RecordAsync(ICollection<string> calls, string name)
    {
        calls.Add(name);
        return Task.CompletedTask;
    }

    private static void Increment(IDictionary<string, int> calls, string name)
    {
        calls.TryGetValue(name, out var current);
        calls[name] = current + 1;
    }

    private static void AssertRegistration<TService, TImplementation>(LocalPreviewServicePlan plan)
    {
        Assert.Equal(typeof(TImplementation), plan.ServiceTypes[typeof(TService)]);
    }

    private static void AssertParameterless(Type implementationType)
    {
        var constructor = Assert.Single(implementationType.GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));
        Assert.Empty(constructor.GetParameters());
    }

    private sealed class StubRunMode(bool isLocalPreview) : IAppRunMode
    {
        public bool IsLocalPreview { get; } = isLocalPreview;
    }
}
