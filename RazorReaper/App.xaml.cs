using RazorReaper.Diagnostics;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;

namespace RazorReaper
{
    public partial class App : Application
    {
        private static readonly TimeSpan TelemetryShutdownTimeout = TimeSpan.FromSeconds(5);
        private readonly ITelemetryService telemetryService;
        private readonly IAutoUpdateManager autoUpdateManager;

        public App(
            IFontInstaller fontInstaller,
            IScopeModeStartupService scopeModeStartupService,
            ITelemetryService telemetryService,
            IAutoUpdateManager autoUpdateManager)
        {
            this.telemetryService = telemetryService;
            this.autoUpdateManager = autoUpdateManager;

            InitializeComponent();
            _ = Task.Run(() => fontInstaller.EnsurePresetFontsInstalledAsync());
            _ = Task.Run(() => scopeModeStartupService.ApplySavedScopeModeAsync());
            _ = Task.Run(() => autoUpdateManager.RunStartupCheckAsync());
            _ = this.telemetryService.StartAsync();

            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var version = UpdateService.GetCurrentVersion();
            var window = new Window(new MainPage())
            {
                Title = $"Razor Reaper : Version {version}"
            };
            window.Destroying += HandleWindowDestroying;

            return window;
        }

        private void HandleWindowDestroying(object? sender, EventArgs e)
        {
            autoUpdateManager.LaunchPendingInstaller();
            FlushTelemetryShutdown();
        }

        private void HandleProcessExit(object? sender, EventArgs e)
        {
            autoUpdateManager.LaunchPendingInstaller();
            FlushTelemetryShutdown();
        }

        private void HandleUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            AppDiagnostics.RecordError(
                AppErrorCodes.UnhandledException,
                "Unhandled exception during app execution.",
                exception);

            _ = telemetryService.TrackEventAsync(
                "app_error",
                TelemetryEventStatus.Down,
                exception?.Message ?? "Unhandled app exception.",
                new Dictionary<string, object?>
                {
                    ["error_code"] = AppErrorCodes.UnhandledException,
                    ["error_kind"] = "unhandled",
                    ["is_terminating"] = e.IsTerminating,
                    ["exception_type"] = exception?.GetType().FullName ?? "unknown"
                });
        }

        private void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppDiagnostics.RecordError(
                AppErrorCodes.UnobservedTaskException,
                "Background task exception was not observed.",
                e.Exception);

            _ = telemetryService.TrackEventAsync(
                "app_error",
                TelemetryEventStatus.Down,
                e.Exception.Message,
                new Dictionary<string, object?>
                {
                    ["error_code"] = AppErrorCodes.UnobservedTaskException,
                    ["error_kind"] = "background",
                    ["exception_type"] = e.Exception.GetType().FullName ?? "unknown"
                });

            e.SetObserved();
        }

        private void FlushTelemetryShutdown()
        {
            try
            {
                using var cts = new CancellationTokenSource(TelemetryShutdownTimeout);
                Task.Run(() => telemetryService.StopAsync(cts.Token)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // App is closing and the bounded telemetry flush timed out.
            }
        }
    }
}
