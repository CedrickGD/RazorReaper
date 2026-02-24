using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RazorReaper.Configuration;
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
            }
            catch
            {
                // Fall back to WebView2 default behavior if the custom folder cannot be prepared.
            }
#endif
        }

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
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
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
            services.AddSingleton<IFontInstaller, FontInstaller>();
            services.AddSingleton<IScopeModeStartupService, ScopeModeStartupService>();
        }
    }
}
