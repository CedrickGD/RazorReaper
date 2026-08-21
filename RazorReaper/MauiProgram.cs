using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RazorReaper.Configuration;
using RazorReaper.Diagnostics;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using Serilog;
using Serilog.Core;
using System.Reflection;

namespace RazorReaper
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            // If we were relaunched elevated, wait for the prior instance to exit before we touch
            // the WebView2 data folder (only one process may hold a given folder at a time).
            WaitForPriorInstanceIfRelaunched();

            ConfigureWebView2UserDataFolder();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            // Configure Serilog
            ConfigureLogging(builder);

            // Load configuration from appsettings.json
            ConfigureAppConfiguration(builder);

            // Register services
            ConfigureServices(builder.Services);

            builder.Services.AddMauiBlazorWebView();
            // Both clients carry the per-install request signature (rr.install.v1).
            builder.Services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName, client => 
            {
                client.DefaultRequestHeaders.Add("User-Agent", "RazorReaper/1.4.8 (Windows NT 10.0; Win64; x64)");
            }).AddHttpMessageHandler<RazorReaper.Services.Http.SignedRequestHandler>();
            builder.Services.AddHttpClient("RazorReaperTelemetry", client => 
            {
                client.DefaultRequestHeaders.Add("User-Agent", "RazorReaper/1.4.8 (Windows NT 10.0; Win64; x64)");
            }).AddHttpMessageHandler<RazorReaper.Services.Http.SignedRequestHandler>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
#endif

            return builder.Build();
        }

        private static bool IsProcessElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void WaitForPriorInstanceIfRelaunched()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var idx = Array.IndexOf(args, RazorReaper.Services.Elevation.IElevationService.RestartMarker);
                if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var pid))
                {
                    try
                    {
                        using var prior = System.Diagnostics.Process.GetProcessById(pid);
                        prior.WaitForExit(5000);
                    }
                    catch
                    {
                        // Prior process already gone (the common case) — nothing to wait for.
                    }
                }
            }
            catch
            {
                // Never let startup argument parsing block the app from launching.
            }
        }

        private static void ConfigureWebView2UserDataFolder()
        {
#if WINDOWS
            try
            {
                // Elevated and normal instances get separate WebView2 data folders: a folder can
                // only be held by one process at a time, and during an elevate-relaunch the old
                // instance's WebView2 processes may still hold the normal folder for a moment,
                // which would otherwise crash the new elevated instance before it can render.
                var webViewFolderName = GetWebView2FolderName(IsProcessElevated());
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RazorReaper",
                    webViewFolderName);

                Directory.CreateDirectory(userDataFolder);
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

                // Clear any persisted zoom factor so the UI always starts at 100%.
                ResetWebView2ZoomPreference(userDataFolder);
            }
            catch
            {
                // Fall back to WebView2 default behavior if the custom folder cannot be prepared.
            }
#endif
        }

        internal static string GetWebView2FolderName(bool isElevated)
            => isElevated ? "WebView2-admin" : "WebView2";

#if WINDOWS
        private static void ResetWebView2ZoomPreference(string userDataFolder)
        {
            try
            {
                var prefsPath = Path.Combine(userDataFolder, "EBWebView", "Default", "Preferences");
                if (!File.Exists(prefsPath))
                    return;

                var json = File.ReadAllText(prefsPath);

                // Remove the partition block that stores per-host zoom levels.
                // The zoom data lives under "partition" → "per_host_zoom_levels".
                // Stripping it forces WebView2 to fall back to the default 1.0 factor.
                if (json.Contains("per_host_zoom_levels"))
                {
                    // Simple but effective: deserialize, strip the key, rewrite.
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    using var ms = new MemoryStream();
                    using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                    {
                        WriteJsonWithoutZoom(writer, doc.RootElement);
                    }
                    File.WriteAllBytes(prefsPath, ms.ToArray());
                }
            }
            catch
            {
                // Non-critical; worst case the previous zoom level stays for one more launch.
            }
        }

        private static void WriteJsonWithoutZoom(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement element, string? propertyName = null)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.Name == "per_host_zoom_levels")
                            continue;
                        writer.WritePropertyName(prop.Name);
                        WriteJsonWithoutZoom(writer, prop.Value, prop.Name);
                    }
                    writer.WriteEndObject();
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteJsonWithoutZoom(writer, item);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
#endif

        private static void ConfigureLogging(MauiAppBuilder builder)
        {
            var levelSwitch = new LoggingLevelSwitch();
            LoggingControl.Initialize(levelSwitch);

            var logFolder = AppDiagnostics.GetLogFolder();
            var logPath = AppDiagnostics.GetLogFilePath();

            try
            {
                if (!Directory.Exists(logFolder))
                {
                    Directory.CreateDirectory(logFolder);
                }
            }
            catch
            {
                logFolder = AppDiagnostics.DefaultLogFolder;
                logPath = Path.Combine(logFolder, AppDiagnostics.DefaultLogFileName);
                try
                {
                    Directory.CreateDirectory(logFolder);
                    AppDiagnostics.SetLogFolder(logFolder);
                }
                catch
                {
                }
            }

            LoggingControl.ApplySettings(
                AppDiagnostics.GetLoggingEnabled(),
                AppDiagnostics.GetVerboseLoggingEnabled());

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .WriteTo.Debug()
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger);
        }

        private static void ConfigureAppConfiguration(MauiAppBuilder builder)
        {
            // Anchor JSON config lookup to the directory containing the executable, NOT
            // Directory.GetCurrentDirectory(). The CWD depends on how the app was launched
            // (Start menu shortcut, double-click, dotnet run) and is unreliable — for users
            // launching from the wrong cwd the bound TelemetrySettings silently falls back to
            // defaults (Enabled=false, empty Endpoint), which is what stopped session telemetry.
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);

            // Fall back to the embedded copy if appsettings.json didn't ship alongside the
            // exe (e.g. a packaging slip). The CopyToOutputDirectory entry in the csproj
            // covers the common case; this is belt-and-suspenders.
            var assembly = Assembly.GetExecutingAssembly();
            using var embedded = assembly.GetManifestResourceStream("RazorReaper.appsettings.json");
            if (embedded is not null)
            {
                configBuilder.AddJsonStream(embedded);
            }

            var config = configBuilder.Build();

            builder.Configuration.AddConfiguration(config);
            builder.Services.Configure<AppConfiguration>(config);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Register application services
            services.AddSingleton<IPreferencesStore, MauiPreferencesStore>();
            services.AddSingleton<IRawHardwareIdentitySource, WindowsRawHardwareIdentitySource>();
            services.AddSingleton<IAppearanceService, AppearanceService>();
            services.AddSingleton<IArkPathProvider, ArkPathProvider>();
            services.AddSingleton<IFileSystemService, FileSystemService>();
            services.AddSingleton<IProcessService, ProcessService>();
            services.AddSingleton<IGameConsoleService, RazorReaper.Services.Implementations.Game.GameConsoleService>();
            services.AddSingleton<IArkLauncher, ArkLauncher>();
            services.AddSingleton<IActivityService, ActivityService>();
            services.AddSingleton<IIniPresetService, IniPresetService>();
            services.AddSingleton<IGameIniService, GameIniService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<IFontInstaller, FontInstaller>();
            services.AddSingleton<IScopeModeStartupService, ScopeModeStartupService>();
            services.AddSingleton<IDeviceLocationService, DeviceLocationService>();
            services.AddSingleton<IAnnouncementService, AnnouncementService>();
            services.AddSingleton<IFeedbackService, FeedbackService>();
            services.AddSingleton<ISteamWorkshopService, SteamWorkshopService>();
            services.AddSingleton<ICustomServerDataService, CustomServerDataService>();
            services.AddSingleton<IMediaCacheService, MediaCacheService>();
            services.AddSingleton<IHostedMediaService, HostedMediaService>();
            services.AddSingleton<RazorReaper.Services.Steam.ISteamFavoritesService, RazorReaper.Services.Steam.SteamFavoritesService>();
            services.AddSingleton<RazorReaper.Services.Steam.ISteamServerHistoryService, RazorReaper.Services.Steam.SteamServerHistoryService>();
            services.AddSingleton<RazorReaper.Services.ServerQuery.IServerQueryService, RazorReaper.Services.ServerQuery.ServerQueryService>();
            services.AddSingleton<ILineListService, LineListService>();
            services.AddSingleton<ITextureBackupService, TextureBackupService>();
            services.AddSingleton<ICompactArkService, CompactArkService>();
            services.AddSingleton<ICrosshairService, CrosshairService>();
            services.AddSingleton<ICustomLabSettingsService, CustomLabSettingsService>();
            services.AddSingleton<ISkyInjectorService, RazorReaper.Services.Implementations.CustomLab.SkyInjectorService>();
            services.AddSingleton<ISkyInjectorSessionState, RazorReaper.Services.Implementations.CustomLab.SkyInjectorSessionState>();
            services.AddSingleton<RazorReaper.Services.Media.IFfmpegProvider, RazorReaper.Services.Media.FfmpegProvider>();
            services.AddSingleton<RazorReaper.Services.Media.IVideoConverter, RazorReaper.Services.Media.VideoConverter>();
            // General-purpose conversion for the Convert page (ported from Convert-X).
            services.AddSingleton<RazorReaper.Services.Media.IMediaConverter, RazorReaper.Services.Media.MediaConverter>();
            services.AddSingleton<RazorReaper.Services.Media.IMediaProbe, RazorReaper.Services.Media.MediaProbe>();
            services.AddSingleton<ILoadingScreenService, LoadingScreenService>();
            services.AddSingleton<ICharPresetService, CharPresetService>();
            services.AddSingleton<IStretchedResService, StretchedResService>();
            services.AddSingleton<RazorReaper.Services.Gamma.IGammaService, RazorReaper.Services.Gamma.GammaService>();

            // Automation platform (input simulation, hotkeys, macros, calibration, screen sampling)
            services.AddSingleton<RazorReaper.Services.Automation.IArkKeyBindingService, RazorReaper.Services.Automation.ArkKeyBindingService>();
            services.AddSingleton<RazorReaper.Services.Automation.IInputSimulator, RazorReaper.Services.Automation.InputSimulator>();
            services.AddSingleton<RazorReaper.Services.Automation.IAutomationHotkeyService, RazorReaper.Services.Automation.AutomationHotkeyService>();
            services.AddSingleton<RazorReaper.Services.Automation.IAutoClickerRuntime, RazorReaper.Services.Automation.AutoClickerRuntime>();
            services.AddSingleton<RazorReaper.Services.Automation.IAutoClickerHotkeyBinder, RazorReaper.Services.Automation.AutoClickerHotkeyBinder>();
            services.AddSingleton<RazorReaper.Services.Automation.IMacroEngine, RazorReaper.Services.Automation.MacroEngine>();
            services.AddSingleton<RazorReaper.Services.Automation.ICalibrationService, RazorReaper.Services.Automation.CalibrationService>();
            services.AddSingleton<RazorReaper.Services.Automation.IInputRecorderService, RazorReaper.Services.Automation.InputRecorderService>();
            services.AddSingleton<RazorReaper.Services.Automation.IScreenSampler, RazorReaper.Services.Automation.ScreenSampler>();
            services.AddSingleton<RazorReaper.Services.Automation.IScreenOcr, RazorReaper.Services.Automation.ScreenOcr>();
            services.AddSingleton<RazorReaper.Services.Automation.DurabilityReader>();
            services.AddSingleton<RazorReaper.Services.Automation.IForegroundGate, RazorReaper.Services.Automation.ForegroundGate>();
            services.AddSingleton<RazorReaper.Services.Automation.IFastTransferMacro, RazorReaper.Services.Automation.FastTransferMacro>();
            services.AddSingleton<RazorReaper.Services.Automation.IFedSuitMacro, RazorReaper.Services.Automation.FedSuitMacro>();
            services.AddSingleton<RazorReaper.Services.Automation.IAutoAntidoteService, RazorReaper.Services.Automation.AutoAntidoteService>();
            // Registered after the scripts below are enumerable as AutomationScriptBase.
            services.AddSingleton<RazorReaper.Services.Automation.IHotkeyRegistry, RazorReaper.Services.Automation.HotkeyRegistry>();

            // ATS3-parity automation scripts (each derives from AutomationScriptBase)
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.YutyScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.AutoWalkScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.MammothScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.AstroScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.AutoDownloadScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.FastTpScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.TakeAllScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.TekSaddleScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.NoglinScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.InvSizeScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.AntiAfkScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.TurretManagerScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.FlakScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.DinoReadyScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.CraftingScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.AutoAntidoteScript>();
            services.AddSingleton<RazorReaper.Services.Automation.Scripts.FedSuitScript>();

            // Expose every script as AutomationScriptBase so the Scripts hub + Global Hotkeys page
            // enumerate them uniformly (registration order = display order). Add new scripts here too.
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.YutyScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.AutoWalkScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.MammothScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.FastTpScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.TakeAllScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.AutoDownloadScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.AstroScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.TekSaddleScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.NoglinScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.InvSizeScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.AntiAfkScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.TurretManagerScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.FlakScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.DinoReadyScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.CraftingScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.AutoAntidoteScript>());
            services.AddSingleton<RazorReaper.Services.Automation.AutomationScriptBase>(sp => sp.GetRequiredService<RazorReaper.Services.Automation.Scripts.FedSuitScript>());

            services.AddSingleton<RazorReaper.Services.Desync.IDesyncService, RazorReaper.Services.Desync.DesyncService>();
            services.AddSingleton<RazorReaper.Services.FileModifier.IFileModifierService, RazorReaper.Services.FileModifier.FileModifierService>();

            // Overlay platform (HUD overlay window + notifier client + session auto-detect)
            services.AddSingleton<RazorReaper.Services.Overlay.INotifierClientService, RazorReaper.Services.Overlay.NotifierClientService>();
            
            services.AddSingleton<RazorReaper.Services.Elevation.IElevationService, RazorReaper.Services.Elevation.ElevationService>();

            // Command palette: turns the services above into runnable rows in Ctrl+K.
            // Registered after them so every dependency is already known to the container.
            services.AddSingleton<IHwidService, HwidService>();

            services.AddSingleton<IClientIdentityService, ClientIdentityService>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ISecureValueStore, MauiSecureValueStore>();
            services.AddSingleton<IInstallIdentityService, InstallIdentityService>();
            // The handler resolves the identity service lazily: the identity service depends on
            // ILicenseService, which owns an HttpClient that carries this very handler.
            services.AddTransient(sp => new RazorReaper.Services.Http.SignedRequestHandler(
                () => sp.GetRequiredService<IInstallIdentityService>(),
                sp.GetRequiredService<TimeProvider>(),
                RazorReaper.Services.Http.SignedRequestHandler.AllowedHostsFrom(
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfiguration>>().Value),
                sp.GetRequiredService<ILogger<RazorReaper.Services.Http.SignedRequestHandler>>()));
            services.AddSingleton<ITelemetryService, TelemetryService>();
            services.AddSingleton<IUsageGateService, UsageGateService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IAutoUpdateManager, AutoUpdateManager>();
            services.AddSingleton<IDiscordPresenceService, DiscordPresenceService>();
            services.AddSingleton<ILicenseService, LicenseService>();
            services.AddSingleton<IAccessGateService, AccessGateService>();
            services.AddSingleton<IArkLinkService, ArkLinkService>();
            services.AddSingleton<RazorReaper.Navigation.IPaletteCommandProvider, RazorReaper.Navigation.PaletteCommandProvider>();
            services.AddSingleton<RazorReaper.Services.Overlay.IHudOverlayService, RazorReaper.Services.Overlay.HudOverlayService>();
            services.AddSingleton<RazorReaper.Services.Overlay.ISessionHudService, RazorReaper.Services.Overlay.SessionHudService>();
            services.AddSingleton<IGameIniService, GameIniService>();
            services.AddSingleton<ILineListService, LineListService>();
        }
    }
}
