using Microsoft.Extensions.DependencyInjection;
using RazorReaper.Diagnostics;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;

namespace RazorReaper
{
    public partial class App : Application
    {
        private static readonly TimeSpan TelemetryShutdownTimeout = TimeSpan.FromSeconds(5);
        private readonly IServiceProvider services;
        private ITelemetryService? telemetryService;
        private IAutoUpdateManager? autoUpdateManager;
        private IDiscordPresenceService? discordPresence;
        private IAccessGateService? accessGate;
        private int telemetryShutdownStarted;
        private Task? telemetryShutdownTask;

        public App(IServiceProvider services)
        {
            this.services = services;

            InitializeComponent();

            QueueStartupTasks();

            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += HandleProcessExit;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
        }

        private void QueueStartupTasks()
        {
            // Resolved in the same order as the former constructor injection.
            var fontInstaller = services.GetRequiredService<IFontInstaller>();
            var scopeModeStartupService = services.GetRequiredService<IScopeModeStartupService>();
            var installIdentity = services.GetRequiredService<IInstallIdentityService>();
            var telemetry = services.GetRequiredService<ITelemetryService>();
            var updateManager = services.GetRequiredService<IAutoUpdateManager>();
            var discord = services.GetRequiredService<IDiscordPresenceService>();
            var access = services.GetRequiredService<IAccessGateService>();
            var arkLink = services.GetRequiredService<IArkLinkService>();

            // Scan the player's ARK key bindings before anything reads a script default. Scripts
            // resolve their defaults in their constructors, so a lazy scan would arrive too late
            // and they would silently fall back to ARK's factory layout.
            services.GetRequiredService<RazorReaper.Services.Automation.IArkKeyBindingService>();

            // Constructing the binder claims the Auto Clicker's key for the lifetime of the app.
            // Resolved here rather than by the page so the hotkey survives navigating away.
            services.GetRequiredService<RazorReaper.Services.Automation.IAutoClickerHotkeyBinder>();

            telemetryService = telemetry;
            autoUpdateManager = updateManager;
            discordPresence = discord;
            accessGate = access;

            // Updates are forced: when the manager has an installer staged it asks us to
            // get out of the way. The orchestrator it spawns waits for this PID to exit,
            // installs silently, then relaunches — so all we do is hand off and quit.
            updateManager.InstallRequested += HandleInstallRequested;

            RunStartupTask("font-install", () => fontInstaller.EnsurePresetFontsInstalledAsync());
            RunStartupTask("scope-mode", () => scopeModeStartupService.ApplySavedScopeModeAsync());
            RunStartupTask("update-check", () => updateManager.RunStartupCheckAsync());
            // Registers the install's signing key before the first telemetry event goes out.
            // Fire-and-forget like the rest: signing only needs the key, which exists at once;
            // the backend acknowledgement may lag without holding anything up.
            RunStartupTask("install-identity", () => installIdentity.EnsureRegisteredAsync());
            RunStartupTask("telemetry-start", () => telemetry.StartAsync());
            RunStartupTask("access-gate", () => access.StartAsync());
            RunStartupTask("discord-rpc", () =>
            {
                discord.Initialize();
                return Task.CompletedTask;
            });
            RunStartupTask("ark-link", () =>
            {
                arkLink.Start();
                return Task.CompletedTask;
            });
        }

        private void HandleInstallRequested()
        {
            var manager = autoUpdateManager;
            if (manager is null)
            {
                return;
            }

            try
            {
                if (!manager.LaunchPendingInstaller())
                {
                    // Nothing staged, or the orchestrator wouldn't start. Staying open is
                    // the right failure mode, but the manager has to be told: it stops
                    // checking while an installer is staged, so leaving that state behind
                    // would end updates for the rest of the session.
                    manager.ResetPendingInstaller();
                    AppDiagnostics.RecordError(
                        AppErrorCodes.StartupTaskFailure,
                        "Auto-update handoff failed: installer did not launch.");
                    return;
                }

                // Hard exit so file locks are released before the installer's replace step.
                // ProcessExit still fires, so the telemetry flush stays bounded.
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.StartupTaskFailure,
                    "Auto-update handoff threw.",
                    ex);
            }
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
            // Version lives at the foot of the sidebar now, so the title bar doesn't
            // repeat it — otherwise the name and version each showed up twice on screen.
            var window = new Window(new MainPage())
            {
                Title = "Razor Reaper — Ark QOL Tool"
            };
            window.Destroying += HandleWindowDestroying;

            return window;
        }

        private void HandleWindowDestroying(object? sender, EventArgs e)
        {
            SafeInvoke(() => autoUpdateManager!.LaunchPendingInstaller());
            SafeInvoke(() => discordPresence!.Shutdown());

            // Fire-and-forget so the window disappears instantly when the user clicks X.
            // ProcessExit waits on this task as a backstop so the session_end POST gets a
            // chance to land before the process tears down.
            telemetryShutdownTask = Task.Run(FlushTelemetryShutdown);
        }

        private void HandleProcessExit(object? sender, EventArgs e)
        {
            SafeInvoke(() => autoUpdateManager!.LaunchPendingInstaller());
            FlushTelemetryAtProcessExit();
        }

        private void FlushTelemetryAtProcessExit()
        {
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

            {
                        _ = telemetryService!.TrackEventAsync(
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

            {
                        _ = telemetryService!.TrackEventAsync(
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
            var telemetry = telemetryService;
            if (telemetry is null)
            {
                return;
            }

            // Idempotent: both Destroying and ProcessExit may fire on the same shutdown.
            if (Interlocked.Exchange(ref telemetryShutdownStarted, 1) != 0)
            {
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TelemetryShutdownTimeout);
                // Run on a thread-pool thread to avoid deadlocks if invoked from the UI sync context.
                Task.Run(async () => await telemetry.StopAsync(cts.Token).ConfigureAwait(false))
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
