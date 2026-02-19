using RazorReaper.Services;
using RazorReaper.Telemetry;

namespace RazorReaper
{
    public partial class App : Application
    {
        public App(
            IFontInstaller fontInstaller,
            IScopeModeStartupService scopeModeStartupService,
            ITelemetryStartupService telemetryStartupService)
        {
            InitializeComponent();
            _ = Task.Run(() => fontInstaller.EnsurePresetFontsInstalledAsync());
            _ = Task.Run(() => scopeModeStartupService.ApplySavedScopeModeAsync());
            _ = Task.Run(() => telemetryStartupService.RunAsync());

            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var version = UpdateService.GetCurrentVersion();
            var window = new Window(new MainPage())
            {
                Title = $"Razor Reaper : Version {version}"
            };

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
    }
}
