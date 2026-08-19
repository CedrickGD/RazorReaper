using Microsoft.Extensions.DependencyInjection;
using RazorReaper.Models;
using RazorReaper.Navigation;
using RazorReaper.Services.Overlay;

namespace RazorReaper.Services.Implementations;

internal sealed record LocalPreviewServicePlan(
    IAppRunMode RunMode,
    IReadOnlyDictionary<Type, Type> ServiceTypes);

internal static class LocalPreviewComposition
{
    public static LocalPreviewServicePlan CreatePlan(IAppRunMode runMode)
    {
        ArgumentNullException.ThrowIfNull(runMode);

        IReadOnlyDictionary<Type, Type> serviceTypes = runMode.IsLocalPreview
            ? new Dictionary<Type, Type>
            {
                [typeof(IClientIdentityService)] = typeof(LocalPreviewClientIdentityService),
                [typeof(ITelemetryService)] = typeof(LocalPreviewTelemetryService),
                [typeof(IUsageGateService)] = typeof(LocalPreviewUsageGateService),
                [typeof(IUpdateService)] = typeof(LocalPreviewUpdateService),
                [typeof(IAutoUpdateManager)] = typeof(LocalPreviewAutoUpdateManager),
                [typeof(IDiscordPresenceService)] = typeof(LocalPreviewDiscordPresenceService),
                [typeof(ILicenseService)] = typeof(LocalPreviewLicenseService),
                [typeof(IAccessGateService)] = typeof(LocalPreviewAccessGateService),
                [typeof(IArkLinkService)] = typeof(LocalPreviewArkLinkService),
                [typeof(IPaletteCommandProvider)] = typeof(LocalPreviewPaletteCommandProvider),
                [typeof(IHudOverlayService)] = typeof(LocalPreviewHudOverlayService),
                [typeof(ISessionHudService)] = typeof(LocalPreviewSessionHudService),
                [typeof(IGameIniService)] = typeof(LocalPreviewGameIniService),
                [typeof(ILineListService)] = typeof(LocalPreviewLineListService),
            }
            : new Dictionary<Type, Type>
            {
                [typeof(IClientIdentityService)] = typeof(ClientIdentityService),
                [typeof(ITelemetryService)] = typeof(TelemetryService),
                [typeof(IUsageGateService)] = typeof(UsageGateService),
                [typeof(IUpdateService)] = typeof(UpdateService),
                [typeof(IAutoUpdateManager)] = typeof(AutoUpdateManager),
                [typeof(IDiscordPresenceService)] = typeof(DiscordPresenceService),
                [typeof(ILicenseService)] = typeof(LicenseService),
                [typeof(IAccessGateService)] = typeof(AccessGateService),
                [typeof(IArkLinkService)] = typeof(ArkLinkService),
                [typeof(IPaletteCommandProvider)] = typeof(PaletteCommandProvider),
                [typeof(IHudOverlayService)] = typeof(HudOverlayService),
                [typeof(ISessionHudService)] = typeof(SessionHudService),
                [typeof(IGameIniService)] = typeof(GameIniService),
                [typeof(ILineListService)] = typeof(LineListService),
            };

        return new LocalPreviewServicePlan(runMode, serviceTypes);
    }

    public static void Register(IServiceCollection services, IAppRunMode runMode)
    {
        ArgumentNullException.ThrowIfNull(services);

        var plan = CreatePlan(runMode);
        services.AddSingleton<IAppRunMode>(plan.RunMode);

        foreach (var registration in plan.ServiceTypes)
        {
            services.AddSingleton(registration.Key, registration.Value);
        }
    }
}

internal sealed record AppStartupActions(
    Func<Task> FontInstall,
    Func<Task> ScopeMode,
    Func<Task> UpdateCheck,
    Func<Task> TelemetryStart,
    Func<Task> AccessGate,
    Func<Task> DiscordRpc,
    Func<Task> ArkLink);

internal static class AppStartupPolicy
{
    public static void Queue(
        IAppRunMode runMode,
        Func<AppStartupActions> actionsFactory,
        Action<string, Func<Task>> queue)
    {
        if (runMode.IsLocalPreview)
        {
            return;
        }

        var actions = actionsFactory();
        queue("font-install", actions.FontInstall);
        queue("scope-mode", actions.ScopeMode);
        queue("update-check", actions.UpdateCheck);
        queue("telemetry-start", actions.TelemetryStart);
        queue("access-gate", actions.AccessGate);
        queue("discord-rpc", actions.DiscordRpc);
        queue("ark-link", actions.ArkLink);
    }
}

internal sealed record LocalPreviewLayoutActions(
    Action ResolveHudAndSession,
    Action DetectVersionUpgrade,
    Func<Task> ValidateLicenseAsync,
    Action SubscribeAccess,
    Action ReflectDiscordAndHud,
    Action SubscribeNavigation);

internal static class LocalPreviewLayoutPolicy
{
    public static async Task RunBeforeLocalNavigationAsync(
        IAppRunMode runMode,
        LocalPreviewLayoutActions actions)
    {
        if (runMode.IsLocalPreview)
        {
            return;
        }

        actions.ResolveHudAndSession();
        actions.DetectVersionUpgrade();
        await actions.ValidateLicenseAsync();
    }

    public static void RunAfterLocalNavigation(
        IAppRunMode runMode,
        LocalPreviewLayoutActions actions)
    {
        if (runMode.IsLocalPreview)
        {
            return;
        }

        actions.SubscribeAccess();
        actions.ReflectDiscordAndHud();
        actions.SubscribeNavigation();
    }

    public static bool ShouldRenderAccessBlock(IAppRunMode runMode, Func<bool> isSuspended)
        => !runMode.IsLocalPreview && isSuspended();
}

internal sealed record AppShutdownActions(
    Action? UpdaterHandoff,
    Action? DiscordShutdown,
    Action? TelemetryStop,
    Action? TelemetryTrack);

internal static class AppShutdownPolicy
{
    public static void Run(IAppRunMode runMode, Func<AppShutdownActions> actionsFactory)
    {
        if (runMode.IsLocalPreview)
        {
            return;
        }

        var actions = actionsFactory();
        actions.UpdaterHandoff?.Invoke();
        actions.DiscordShutdown?.Invoke();
        actions.TelemetryStop?.Invoke();
        actions.TelemetryTrack?.Invoke();
    }
}

internal static class WindowsBootstrapPolicy
{
    public static bool ShouldWireIntegrations(
        bool authoritativeLocalPreview,
        IAppRunMode? registeredRunMode)
        => !authoritativeLocalPreview && registeredRunMode?.IsLocalPreview != true;

    public static WindowsSynchronizationNames GetSynchronizationNames(bool isLocalPreview)
        => isLocalPreview
            ? new WindowsSynchronizationNames(
                "RazorReaper_LocalPreview_SingleInstance_Mutex",
                "RazorReaper_LocalPreview_ShowWindow_Event")
            : new WindowsSynchronizationNames(
                "RazorReaper_SingleInstance_Mutex",
                "RazorReaper_ShowWindow_Event");
}

internal sealed record WindowsSynchronizationNames(string MutexName, string ShowEventName);

internal sealed record LocalPreviewLoggingPlan(
    bool UseProductionDiagnostics,
    bool UseFileSink);

internal static class LocalPreviewLoggingPolicy
{
    public static LocalPreviewLoggingPlan CreatePlan(IAppRunMode runMode)
        => runMode.IsLocalPreview
            ? new LocalPreviewLoggingPlan(
                UseProductionDiagnostics: false,
                UseFileSink: false)
            : new LocalPreviewLoggingPlan(
                UseProductionDiagnostics: true,
                UseFileSink: true);
}

internal static class LocalPreviewWebViewProfilePolicy
{
    public static void Prepare(IAppRunMode runMode, Action prepareProfile)
    {
        try
        {
            prepareProfile();
        }
        catch when (!runMode.IsLocalPreview)
        {
            // Normal startup keeps its historical fallback to WebView2's default profile.
        }
    }
}

internal static class LocalPreviewLaunchPolicy
{
    public static string GetStartPath(IAppRunMode runMode)
        => runMode.IsLocalPreview ? "/credits" : "/";

    public static bool ShouldMapProductionMediaCaches(IAppRunMode runMode)
        => !runMode.IsLocalPreview;

    public static bool ShouldConsumeElevationReturnRoute(IAppRunMode runMode)
        => !runMode.IsLocalPreview;
}

internal sealed class LocalPreviewClientIdentityService : IClientIdentityService
{
    private static readonly ClientIdentity PreviewIdentity = new(
        "00000000-0000-0000-0000-000000000000",
        "00000000000000000000000000000000");

    public ClientIdentity GetIdentity() => PreviewIdentity;
}

internal sealed class LocalPreviewTelemetryService : ITelemetryService
{
    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task TrackEventAsync(
        string eventName,
        TelemetryEventStatus status = TelemetryEventStatus.Ok,
        string? message = null,
        IReadOnlyDictionary<string, object?>? metrics = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class LocalPreviewUsageGateService : IUsageGateService
{
    private static readonly UsageGateResult DeniedResult = new(
        Allowed: false,
        Unlimited: false,
        Remaining: 0,
        Limit: null);

    public event Action? OnUsageChanged
    {
        add { }
        remove { }
    }

    public Task<UsageGateResult> TryConsumeAsync(string feature)
        => Task.FromResult(DeniedResult);

    public Task<IReadOnlyDictionary<string, FeatureUsage>?> GetStatusAsync()
        => Task.FromResult<IReadOnlyDictionary<string, FeatureUsage>?>(null);
}

internal sealed class LocalPreviewUpdateService : IUpdateService
{
    private static readonly Version AssemblyVersion =
        typeof(LocalPreviewUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public Version CurrentVersion => AssemblyVersion;

    public string CurrentVersionLabel => CurrentVersion.Build >= 0
        ? $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}"
        : $"{CurrentVersion.Major}.{CurrentVersion.Minor}";

    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new UpdateCheckResult
        {
            CurrentVersion = CurrentVersion,
            HasUpdate = false,
            ErrorMessage = "Update checks are disabled in local preview.",
            CheckedAt = DateTimeOffset.UnixEpoch,
        });
    }
}

internal sealed class LocalPreviewAutoUpdateManager : IAutoUpdateManager
{
    public bool IsChecking => false;
    public bool IsInstallerReady => false;
    public bool IsDownloading => false;
    public int? DownloadProgressPercent => null;
    public Version? PendingVersion => null;
    public string StatusMessage => "Update checks are disabled in local preview.";
    public UpdateCheckResult? LastCheckResult => null;

    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public event Action? InstallRequested
    {
        add { }
        remove { }
    }

    public Task RunStartupCheckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool LaunchPendingInstaller() => false;
    public void ResetPendingInstaller() { }
    public Version? DetectVersionUpgrade() => null;
}

internal sealed class LocalPreviewDiscordPresenceService : IDiscordPresenceService
{
    public bool IsEnabled
    {
        get => false;
        set { }
    }

    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public void Initialize() { }
    public void SetActivityForPath(string relativePath) { }
    public string ResolveToolLabel(string relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? "Home" : relativePath;
    public void SetActivityLabel(string label) { }
    public void SetMinimizedToTray(bool minimized) { }
    public void Shutdown() { }
}

internal sealed class LocalPreviewLicenseService : ILicenseService
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
        => Task.FromResult((false, "License activation is disabled in local preview."));

    public Task<(bool Success, string Message)> ValidateLicenseAsync()
        => Task.FromResult((false, "License validation is disabled in local preview."));
}

internal sealed class LocalPreviewAccessGateService : IAccessGateService
{
    public bool IsSuspended => false;
    public string? Mode => null;
    public string? Reason => null;
    public DateTimeOffset? BannedUntil => null;

    public event Action? OnAccessStateChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync() => Task.CompletedTask;
    public Task<bool> CheckNowAsync() => Task.FromResult(false);
}

internal sealed class LocalPreviewArkLinkService : IArkLinkService
{
    public bool StartWithArk
    {
        get => false;
        set { }
    }

    public bool CloseWithArk
    {
        get => false;
        set { }
    }

    public event Action? ShowAppRequested
    {
        add { }
        remove { }
    }

    public void Start() { }
}

internal sealed class LocalPreviewPaletteCommandProvider : IPaletteCommandProvider
{
    public IReadOnlyList<PaletteItem> GetCommands()
        => NavCatalog.Groups
            .SelectMany(group => group.Pages)
            .Select(page => new PaletteItem
            {
                Kind = PaletteKind.Page,
                Id = $"page:{page.Route}",
                Title = page.Label,
                Subtitle = page.Description,
                Category = page.Category,
                IconSvg = page.IconSvg,
                Keywords = page.Keywords,
                Route = page.Route,
            })
            .ToArray();
}

internal sealed class LocalPreviewHudOverlayService : IHudOverlayService
{
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public bool IsRunning => false;
    public bool IsMoveMode => false;
    public HudSettings Settings => new();

    public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
    public void Start() { }
    public void Stop() { }
    public void Toggle() { }
    public void SetMoveMode(bool enabled) { }
    public void UpdateSettings(HudSettings settings) { }
    public void SetAccent(byte r, byte g, byte b) { }
    public void SetActiveTool(string? label) { }
    public void SetServerInfo(string? name, int? players, int? maxPlayers, int? pingMs) { }
    public void SetDesync(DateTime? revertAtUtc) { }
    public void PushAlert(HudAlert alert) { }
    public void PushAlert(string text, HudAlertSeverity severity = HudAlertSeverity.Info) { }
    public IReadOnlyList<HudAlert> GetRecentAlerts() => Array.Empty<HudAlert>();
    public void TestAlert() { }
    public void ResetSessionTimer() { }
    public void SetSessionStart(DateTime startUtc) { }
    public void Dispose() { }
}

internal sealed class LocalPreviewSessionHudService : ISessionHudService
{
    public void Dispose() { }
}

internal sealed class LocalPreviewGameIniService : IGameIniService
{
    private static readonly IReadOnlyList<GameIniPreset> Presets =
    [
        new GameIniPreset
        {
            Name = "Balanced performance",
            Description = "A reversible baseline for clearer visuals and stable frame pacing.",
            Entries =
            [
                new GameIniEntry("ScalabilityGroups", "sg.ViewDistanceQuality", "2"),
                new GameIniEntry("ScalabilityGroups", "sg.ShadowQuality", "1"),
                new GameIniEntry("ScalabilityGroups", "sg.TextureQuality", "2"),
            ],
        },
        new GameIniPreset
        {
            Name = "Competitive clarity",
            Description = "Reduces visual noise while keeping essential world detail readable.",
            Entries =
            [
                new GameIniEntry("ScalabilityGroups", "sg.PostProcessQuality", "0"),
                new GameIniEntry("ScalabilityGroups", "sg.EffectsQuality", "1"),
            ],
        },
    ];

    public IReadOnlyList<GameIniPreset> GetBuiltInPresets() => Presets;
    public string? GetIniPath(GameIniTarget target) => null;
    public bool IniFileExists(GameIniTarget target) => false;
    public bool IsArkRunning() => false;
    public Task<GameIniApplyResult> ApplyPresetAsync(GameIniPreset preset)
        => Task.FromResult(GameIniApplyResult.Fail("Changes are disabled in local preview."));
    public Task<GameIniApplyResult> ApplyEntriesAsync(GameIniTarget target, IReadOnlyList<GameIniEntry> entries)
        => Task.FromResult(GameIniApplyResult.Fail("Changes are disabled in local preview."));
    public List<GameIniBackup> ListBackups() => [];
    public Task<GameIniApplyResult> RestoreBackupAsync(GameIniBackup backup)
        => Task.FromResult(GameIniApplyResult.Fail("Changes are disabled in local preview."));
    public bool DeleteBackup(GameIniBackup backup) => false;
    public Task<GameIniDraft?> LoadDraftAsync() => Task.FromResult<GameIniDraft?>(null);
    public Task<bool> SaveDraftAsync(GameIniDraft draft) => Task.FromResult(false);
}

internal sealed class LocalPreviewLineListService : ILineListService
{
    private static readonly IReadOnlyList<string> Suggestions =
        ["Rex", "Shadowmane", "Giganotosaurus", "Stegosaurus"];

    public IReadOnlyList<string> SpeciesSuggestions => Suggestions;

    public Task<LineListStore> LoadAsync()
        => Task.FromResult(new LineListStore
        {
            WtbText = "WTB clean starter lines — message with stats.",
            Lines =
            [
                new BreedingLine
                {
                    Id = "preview-rex",
                    Species = "Rex",
                    Name = "Boss line",
                    Health = 52,
                    Stamina = 38,
                    Weight = 41,
                    Melee = 48,
                    Generation = 12,
                    ForSale = true,
                    Price = "Offer",
                    Notes = "Example data",
                },
                new BreedingLine
                {
                    Id = "preview-shadowmane",
                    Species = "Shadowmane",
                    Name = "Travel line",
                    Health = 44,
                    Stamina = 45,
                    Weight = 39,
                    Melee = 46,
                    Generation = 8,
                    Notes = "Example data",
                },
                new BreedingLine
                {
                    Id = "preview-giga",
                    Species = "Giganotosaurus",
                    Name = "Melee line",
                    Health = 36,
                    Stamina = 22,
                    Weight = 32,
                    Melee = 54,
                    Generation = 18,
                    ForSale = true,
                    Price = "Offer",
                    Notes = "Example data",
                },
            ],
        });

    public Task<bool> AddLineAsync(BreedingLine line) => Task.FromResult(false);
    public Task<bool> UpdateLineAsync(BreedingLine line) => Task.FromResult(false);
    public Task<bool> DeleteLineAsync(string id) => Task.FromResult(false);
    public Task<bool> SaveWtbTextAsync(string text) => Task.FromResult(false);
}
