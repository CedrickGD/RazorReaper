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
        private readonly IDiscordPresenceService discordPresence;
        private int telemetryShutdownStarted;
        private Task? telemetryShutdownTask;

        public App(
            IFontInstaller fontInstaller,
            IScopeModeStartupService scopeModeStartupService,
            ITelemetryService telemetryService,
            IAutoUpdateManager autoUpdateManager,
            IDiscordPresenceService discordPresence)
        {
            this.telemetryService = telemetryService;
            this.autoUpdateManager = autoUpdateManager;
            this.discordPresence = discordPresence;

            InitializeComponent();
            RunStartupTask("font-install", () => fontInstaller.EnsurePresetFontsInstalledAsync());
            RunStartupTask("scope-mode", () => scopeModeStartupService.ApplySavedScopeModeAsync());
            RunStartupTask("update-check", () => autoUpdateManager.RunStartupCheckAsync());
            RunStartupTask("telemetry-start", () => this.telemetryService.StartAsync());
            RunStartupTask("discord-rpc", () => { discordPresence.Initialize(); return Task.CompletedTask; });

            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        }

        private static void RunStartupTask(string name, Func<Task> work)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppDiagnostics.RecordError(
                        AppErrorCodes.StartupTaskFailure,
                        $"Startup task '{name}' failed.",
                        ex);
                }
            });
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
            SafeInvoke(() => autoUpdateManager.LaunchPendingInstaller());
            SafeInvoke(() => discordPresence.Shutdown());
            // Fire-and-forget so the window disappears instantly when the user clicks X.
            // ProcessExit waits on this task as a backstop so the session_end POST
            // gets a chance to land before the process tears down.
            telemetryShutdownTask = Task.Run(FlushTelemetryShutdown);
        }

        private void HandleProcessExit(object? sender, EventArgs e)
        {
            SafeInvoke(() => autoUpdateManager.LaunchPendingInstaller());

            // If Destroying already queued the flush, wait (bounded) for it to land.
            // The Interlocked guard in FlushTelemetryShutdown would otherwise short-circuit
            // this call to a no-op and the background POST would be killed on exit.
            var pendingFlush = telemetryShutdownTask;
            if (pendingFlush is not null)
            {
                try
                {
                    pendingFlush.Wait(TelemetryShutdownTimeout);
                }
                catch
                {
                    // FlushTelemetryShutdown already logs its own failures.
                }
                return;
            }

            FlushTelemetryShutdown();
        }

        private static void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.StartupTaskFailure,
                    "Shutdown hook failed.",
                    ex);
            }
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
            // Observe first: nothing below may run before the exception is defused.
            e.SetObserved();

            AppDiagnostics.RecordError(
                AppErrorCodes.UnobservedTaskException,
                "Background task exception was not observed.",
                e.Exception);

            // Known-benign noise, logged locally above but not worth an app_error event:
            // the DiscordRichPresence library's named-pipe client abandons its pending
            // BeginRead when the IPC pipe drops (Discord closed/restarted, RPC toggled off,
            // shutdown). The orphaned ReadAsync task faults with IOException
            // ERROR_OPERATION_ABORTED ("The I/O operation has been aborted because of either
            // a thread exit or an application request.") and app code can never observe it.
            if (IsAbortedBackgroundIo(e.Exception))
            {
                return;
            }

            _ = telemetryService.TrackEventAsync(
                "app_error",
                TelemetryEventStatus.Down,
                e.Exception.Message,
                new Dictionary<string, object?>
                {
                    ["error_code"] = AppErrorCodes.UnobservedTaskException,
                    ["error_kind"] = "background",
                    ["exception_type"] = e.Exception.GetType().FullName ?? "unknown",
                    // AggregateException alone says nothing — surface the actual fault type.
                    ["base_exception_type"] = e.Exception.GetBaseException().GetType().FullName
                });
        }

        /// <summary>
        /// True when every leaf exception is canceled/aborted async I/O — overlapped reads or
        /// writes killed by a handle close or thread exit (Win32 ERROR_OPERATION_ABORTED, 995).
        /// These come from third-party background plumbing (e.g. the Discord RPC pipe) and are
        /// expected during disconnects and shutdown; they are not app errors.
        /// </summary>
        private static bool IsAbortedBackgroundIo(AggregateException exception)
        {
            const uint OperationAbortedHResult = 0x800703E3; // HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED)

            var leaves = exception.Flatten().InnerExceptions;
            return leaves.Count > 0 && leaves.All(static ex =>
                ex is OperationCanceledException
                || (ex is IOException && (uint)ex.HResult == OperationAbortedHResult));
        }

        private void FlushTelemetryShutdown()
        {
            // Idempotent: both Destroying and ProcessExit may fire on the same shutdown.
            if (Interlocked.Exchange(ref telemetryShutdownStarted, 1) != 0)
            {
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TelemetryShutdownTimeout);
                // Run on a thread-pool thread to avoid deadlocks if invoked from the UI sync context.
                Task.Run(async () => await telemetryService.StopAsync(cts.Token).ConfigureAwait(false))
                    .Wait(TelemetryShutdownTimeout);
            }
            catch (OperationCanceledException)
            {
                // App is closing and the bounded telemetry flush timed out.
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                // Same — the wrapped cancellation is expected when the bounded flush times out.
            }
            catch (Exception ex)
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.StartupTaskFailure,
                    "Telemetry shutdown flush failed.",
                    ex);
            }
        }
    }
}
