using RazorReaper.Services;
using RazorReaper.Telemetry;
using System.Threading;

namespace RazorReaper
{
    public partial class App : Application
    {
        private readonly ITelemetryService _telemetryService;
        private int _sessionEndTracked;

        public App(
            IFontInstaller fontInstaller,
            IScopeModeStartupService scopeModeStartupService,
            ITelemetryStartupService telemetryStartupService,
            ITelemetryService telemetryService)
        {
            _telemetryService = telemetryService;
            InitializeComponent();
            _ = Task.Run(() => fontInstaller.EnsurePresetFontsInstalledAsync());
            _ = Task.Run(() => scopeModeStartupService.ApplySavedScopeModeAsync());
            _ = Task.Run(() => telemetryStartupService.RunAsync());

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

        private static void HandleUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            AppDiagnostics.RecordError(
                AppErrorCodes.UnhandledException,
                "Unhandled exception during app execution.",
                exception);
        }

        private static void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppDiagnostics.RecordError(
                AppErrorCodes.UnobservedTaskException,
                "Background task exception was not observed.",
                e.Exception);
            e.SetObserved();
        }

        private void HandleWindowDestroying(object? sender, EventArgs e)
        {
            TrackSessionEndBestEffort();
        }

        private void HandleProcessExit(object? sender, EventArgs e)
        {
            TrackSessionEndBestEffort();
        }

        private void TrackSessionEndBestEffort()
        {
            if (Interlocked.Exchange(ref _sessionEndTracked, 1) != 0)
            {
                return;
            }

            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _telemetryService.TrackAppSessionEndAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort telemetry should never block shutdown paths.
            }
        }
    }
}
