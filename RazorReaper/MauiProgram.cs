using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using Serilog;
using System.Reflection;

namespace RazorReaper
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
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

        private static void ConfigureLogging(MauiAppBuilder builder)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RazorReaper",
                "Logs",
                "app.log");

            // Ensure log directory exists
            var logDir = Path.GetDirectoryName(logPath);
            if (logDir != null && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
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
        }
    }
}
