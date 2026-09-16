using Microsoft.Extensions.DependencyInjection;
using RazorReaper.Components;
using RazorReaper.Diagnostics;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using System.Net.Sockets;

namespace RazorReaper
{
    public partial class App : Application
    {
        private static readonly TimeSpan TelemetryShutdownTimeout = TimeSpan.FromSeconds(5);
        // Affected sessions average 13 hours and some end with their last faults 0.4 s before
        // session_end, so the rolled-up counts cannot wait for shutdown to be told.
        private static readonly TimeSpan BackgroundFaultFlushInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BackgroundFaultShutdownTimeout = TimeSpan.FromSeconds(2);
        private readonly BackgroundFaultTracker backgroundFaults = new();
        private readonly IServiceProvider services;
        private ITelemetryService? telemetryService;
        private IAutoUpdateManager? autoUpdateManager;
        private IDiscordPresenceService? discordPresence;
        private IAccessGateService? accessGate;
        private readonly System.Threading.Timer backgroundFaultFlushTimer;
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

            // The render gate catches the same faults one step earlier than the finalizer does.
            // Without this line it would observe them and tell only the local log — which is how
            // v1.4.10 turned a live socket family into eight weeks of silence that read as a fix.
            // Set here, before any window exists, so no component can render before it is wired.
            RenderDispatchReporting.UseSink(HandleRenderDispatchFault);

            backgroundFaultFlushTimer = new System.Threading.Timer(
                _ => FlushBackgroundFaults(),
                null,
                BackgroundFaultFlushInterval,
                BackgroundFaultFlushInterval);
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
            // Telemetry starts only after the install's signing key is registered: requests are
            // signed solely once the backend has acknowledged the key, and the first events
            // (session_start) are not retried, so they must not race the registration. Still
            // fire-and-forget — EnsureRegisteredAsync never throws and never blocks on retries.
            RunStartupTask("telemetry-start", async () =>
            {
                await installIdentity.EnsureRegisteredAsync().ConfigureAwait(false);
                await telemetry.StartAsync().ConfigureAwait(false);
            });
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

            try
            {
                if (IsAbortedBackgroundIo(e.Exception))
                {
                    // Dropped, but no longer invisible. v1.4.10 silenced this shape without
                    // telling anyone, the socket family went to exactly zero on 1.4.10/1.5.0/
                    // 1.5.2, and the panel read eight weeks of silence as a fix. The count now
                    // rides out on the next reported event; the first one still reaches the log.
                    if (backgroundFaults.RecordSuppressed(e.Exception))
                    {
                        AppDiagnostics.RecordError(
                            AppErrorCodes.UnobservedTaskException,
                            "Aborted background I/O suppressed.",
                            e.Exception);
                    }

                    return;
                }

                var sighting = backgroundFaults.Record(e.Exception);
                if (sighting is null)
                {
                    // A repeat of a fault already reported in this session: same base type,
                    // same site, same message. It is now a number in the next rollup instead
                    // of a row — one install once emitted 36,456 of these in 2.5 hours, and
                    // a local RecordError per fault would have written Preferences for each.
                    return;
                }

                // The finalizer thread has done all it should: a cheap site capture and a
                // count. The local record, the frame description and the POST go to the pool.
                ReportBackgroundFault(sighting, "Background task exception was not observed.");
            }
            catch (Exception ex)
            {
                // This runs on the finalizer thread: anything escaping here kills the process.
                AppDiagnostics.RecordError(
                    AppErrorCodes.UnobservedTaskException,
                    "Background fault reporting failed.",
                    ex);
            }
        }

        /// <summary>
        /// The render gate's faults, folded into the same tracker as the finalizer's.
        ///
        /// Runs wherever the dispatch completed — the renderer's own thread for a fault raised
        /// during a render, a timer or pool thread for one raised after — so it may not throw,
        /// may not block, and must not walk a stack: on the renderer's thread that competes
        /// with the UI, once per fault, and the first walk in a process loads the PDB from disk.
        /// The tracker takes only a cheap site capture and a count here; the description and
        /// the POST happen on the pool, and a component faulting at 4/s still costs one row plus
        /// a count rather than 4 POSTs a second.
        /// </summary>
        private void HandleRenderDispatchFault(RenderDispatchFault fault)
        {
            try
            {
                var sighting = backgroundFaults.RecordRenderDispatch(
                    fault.Exception,
                    fault.Owner,
                    fault.Origin,
                    fault.Stopped);
                if (sighting is null)
                {
                    return;
                }

                // Same local record the finalizer path writes, so a support bundle pulled from
                // a session the gate was carrying shows what telemetry shows instead of
                // reporting a healthy app. The gate's own log line stays exactly as it was.
                var localMessage = fault.Stopped
                    ? $"{fault.Owner}.{fault.Origin} stopped dispatching renders after {fault.ConsecutiveFaults} consecutive faults."
                    : $"Render dispatch from {fault.Owner}.{fault.Origin} faulted.";
                ReportBackgroundFault(sighting, localMessage);
            }
            catch (Exception ex)
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.UnobservedTaskException,
                    "Render dispatch fault reporting failed.",
                    ex);
            }
        }

        /// <summary>
        /// Hands a first sighting to the pool. The thread that observed it — the finalizer for
        /// an unobserved task, the renderer's dispatcher for a render fault — has already done
        /// the only work the fold needs, and the rest costs more than that thread should pay:
        /// the PDB-backed frame description, three Preferences writes plus a Serilog line for
        /// the local record, and telemetry's synchronous prefix. A pool work item rather than a
        /// Task, so there is no Task here that could go unobserved; the body is guarded whole
        /// because a throw escaping a pool work item terminates the process.
        /// </summary>
        private void ReportBackgroundFault(PendingBackgroundFaultReport sighting, string localMessage)
        {
            ThreadPool.QueueUserWorkItem(
                static state => state.app.ReportBackgroundFaultOnPool(state.sighting, state.localMessage),
                (app: this, sighting, localMessage),
                preferLocal: false);
        }

        private void ReportBackgroundFaultOnPool(PendingBackgroundFaultReport sighting, string localMessage)
        {
            try
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.UnobservedTaskException,
                    localMessage,
                    sighting.Exception);

                // Describe() is the stack walk, on this thread and nowhere earlier.
                _ = PublishBackgroundFaultAsync(sighting.Describe());
            }
            catch (Exception ex)
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.UnobservedTaskException,
                    "Background fault reporting failed.",
                    ex);
            }
        }

        private Task PublishBackgroundFaultAsync(BackgroundFaultReport report, CancellationToken cancellationToken = default)
        {
            // Faults can arrive before the startup tasks have resolved the service.
            var telemetry = telemetryService;
            if (telemetry is null)
            {
                return Task.CompletedTask;
            }

            return telemetry.TrackEventAsync(
                "app_error",
                TelemetryEventStatus.Down,
                report.Message,
                BuildBackgroundFaultMetrics(report),
                cancellationToken);
        }

        /// <summary>
        /// The app_error row for one background fault, from either observer.
        ///
        /// Every key below error_kind is additive and optional — the ingest contract caps metrics
        /// at 64 keys / 8 KB (RR-Admin-Panel/shared/telemetry-contract.ts) and this event carries
        /// well under half of each even once TelemetryService merges its base metrics in.
        /// </summary>
        internal static Dictionary<string, object?> BuildBackgroundFaultMetrics(BackgroundFaultReport report)
        {
            var metrics = new Dictionary<string, object?>
            {
                ["error_code"] = AppErrorCodes.UnobservedTaskException,
                // Deliberately still "background" for both sources. Every panel KPI filters on
                // != 'background' and the background-fault aggregate matches it exactly; a new
                // value here would move the render population into the crash counts. The two
                // populations are told apart by fault_source below.
                ["error_kind"] = "background",
                ["exception_type"] = report.ExceptionType ?? "unknown",
                // AggregateException alone says nothing — surface the actual fault type.
                ["base_exception_type"] = report.BaseExceptionType,
                // The field whose absence made ~83k rows unattributable. Own namespace
                // only, file names without paths — see BackgroundFaultFrames.
                ["top_frame"] = report.TopFrame,
                ["top_frames"] = report.TopFrames,
                ["leaf_exception_count"] = report.LeafExceptionCount,
                // Faults this row stands for: sum it, do not count rows.
                ["occurrences"] = report.Occurrences,
                ["report_kind"] = ToReportKindText(report.Kind),
                ["suppressed_aborted_io"] = report.SuppressedAbortedIo,
                // Which observer caught it. Absent on every pre-1.5.3 row, so the panel reads a
                // missing value as "unobserved_task" and the series stays continuous.
                ["fault_source"] = ToFaultSourceText(report.Source)
            };

            // Render-only keys, omitted rather than sent null, so an unobserved row is byte for
            // byte what it was. These two are what the gate knows and a dead renderer's stack
            // does not: which component, and which member dispatched.
            if (report.Source == BackgroundFaultSource.RenderDispatch)
            {
                metrics["render_owner"] = report.Owner;
                metrics["render_origin"] = report.Origin;
                metrics["render_stopped"] = report.RenderStopped;
            }

            return metrics;
        }

        private static string ToFaultSourceText(BackgroundFaultSource source)
        {
            return source switch
            {
                BackgroundFaultSource.RenderDispatch => "render_dispatch",
                _ => "unobserved_task"
            };
        }

        private static string ToReportKindText(BackgroundFaultReportKind kind)
        {
            return kind switch
            {
                BackgroundFaultReportKind.First => "first",
                BackgroundFaultReportKind.Rollup => "rollup",
                _ => "suppressed"
            };
        }

        private void FlushBackgroundFaults()
        {
            _ = FlushBackgroundFaultsAsync();
        }

        /// <summary>
        /// Ships the counts accumulated since the last flush. Never faults: this task is
        /// discarded, and a faulted discarded task is the very thing being reported.
        /// </summary>
        private async Task FlushBackgroundFaultsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                foreach (var report in backgroundFaults.Flush())
                {
                    await PublishBackgroundFaultAsync(report, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.RecordError(
                    AppErrorCodes.UnobservedTaskException,
                    "Background fault rollup failed.",
                    ex);
            }
        }

        /// <summary>
        /// True for the Discord RPC pipe's abandoned read: an IOException carrying Win32
        /// ERROR_OPERATION_ABORTED (995), directly or wrapped one I/O level deep. The library's
        /// named-pipe client drops its pending BeginRead whenever the IPC pipe goes away
        /// (Discord closed/restarted, RPC toggled off, shutdown) and app code can never observe
        /// that task, so it is noise rather than an app error.
        ///
        /// Deliberately NOT matched: a bare aborted SocketException. That shape is our own UDP
        /// server query orphaning a receive and then closing the socket — an app bug that this
        /// predicate hid for the whole of 1.4.10, 1.5.0 and 1.5.2. Discord talks over
        /// NamedPipeClientStream, whose aborted read is the IOException shape above, and the
        /// production split (175 sessions with RPC on / 17 with it off, against an ~89 % on
        /// baseline) shows the socket family never tracked Discord at all.
        /// </summary>
        internal static bool IsAbortedBackgroundIo(AggregateException exception)
        {
            const uint OperationAbortedHResult = 0x800703E3; // HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED)
            const int OperationAbortedNativeError = 995;

            // Discord nests at most one wrapper; the bound stops a pathological chain from
            // recursing the finalizer thread into a stack overflow.
            const int MaxWrapperDepth = 4;

            var leaves = exception.Flatten().InnerExceptions;
            return leaves.Count > 0 && leaves.All(leaf => IsAbortedPipeLeaf(leaf, 0));

            static bool IsAbortedPipeLeaf(Exception exception, int depth)
            {
                if (depth > MaxWrapperDepth || exception is not IOException ioException)
                {
                    return false;
                }

                if ((uint)ioException.HResult == OperationAbortedHResult)
                {
                    return true;
                }

                // DiscordRichPresence may wrap the native error in the IOException. Follow only
                // I/O wrappers so an unrelated exception that happens to contain an aborted
                // handle is not hidden as benign background plumbing.
                return ioException.InnerException switch
                {
                    OperationCanceledException => true,
                    // A wrapped aborted socket is still the pipe shape: SocketException keeps
                    // WinSock 995 in NativeErrorCode while exposing the generic 0x80004005
                    // HResult, so the HResult check above cannot recognize it.
                    SocketException socketException =>
                        socketException.NativeErrorCode == OperationAbortedNativeError
                        || socketException.SocketErrorCode == SocketError.OperationAborted,
                    IOException inner => IsAbortedPipeLeaf(inner, depth + 1),
                    _ => false
                };
            }
        }

        private void FlushTelemetryShutdown()
        {
            backgroundFaultFlushTimer.Dispose();

            // Every component is about to be torn down. Their dispatch faults are teardown by
            // definition and the gate does not report those, but the sink is removed anyway so
            // nothing can queue a POST behind the flush that is about to close the session.
            // Unconditional, ahead of every early return below: a shutdown on which telemetry
            // was never resolved still must not leave the sink installed.
            RenderDispatchReporting.UseSink(null);

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
                Task.Run(async () =>
                    {
                        // The session summary goes out before StopAsync, on a slice of the
                        // shutdown budget, so the folded counts cannot be lost with the process
                        // and cannot starve session_end either.
                        using var rollupCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        rollupCts.CancelAfter(BackgroundFaultShutdownTimeout);
                        await FlushBackgroundFaultsAsync(rollupCts.Token).ConfigureAwait(false);

                        await telemetry.StopAsync(cts.Token).ConfigureAwait(false);
                    })
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
