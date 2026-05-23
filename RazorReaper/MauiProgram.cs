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
            builder.Services.AddHttpClient();
            builder.Services.AddHttpClient("RazorReaperTelemetry");

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
#endif

            return builder.Build();
        }

        private static void ConfigureWebView2UserDataFolder()
        {
#if WINDOWS
            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RazorReaper",
                    "WebView2");

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

            var levelSwitch = new LoggingLevelSwitch();
            LoggingControl.Initialize(levelSwitch);
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
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("RazorReaper.appsettings.json");

            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
                .Build();

            builder.Configuration.AddConfiguration(config);

            // Bind configuration to AppConfiguration class
            builder.Services.Configure<AppConfiguration>(config);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Register application services
            services.AddSingleton<IArkPathProvider, ArkPathProvider>();
            services.AddSingleton<IFileSystemService, FileSystemService>();
            services.AddSingleton<IProcessService, ProcessService>();
            services.AddSingleton<IActivityService, ActivityService>();
            services.AddSingleton<IIniPresetService, IniPresetService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IAutoUpdateManager, AutoUpdateManager>();
            services.AddSingleton<IFontInstaller, FontInstaller>();
            services.AddSingleton<IScopeModeStartupService, ScopeModeStartupService>();
            services.AddSingleton<IDeviceLocationService, DeviceLocationService>();
            services.AddSingleton<ITelemetryService, TelemetryService>();
            services.AddSingleton<ISteamWorkshopService, SteamWorkshopService>();
            services.AddSingleton<ICustomServerDataService, CustomServerDataService>();
            services.AddSingleton<ITextureBackupService, TextureBackupService>();
            services.AddSingleton<ICrosshairService, CrosshairService>();
            services.AddSingleton<ICustomLabSettingsService, CustomLabSettingsService>();
            services.AddSingleton<IMemoryPatcherService, MemoryPatcherService>();
            services.AddSingleton<ISkyInjectorService, RazorReaper.Services.Implementations.CustomLab.SkyInjectorService>();
            services.AddSingleton<ISkyInjectorSessionState, RazorReaper.Services.Implementations.CustomLab.SkyInjectorSessionState>();
        }
    }
}
