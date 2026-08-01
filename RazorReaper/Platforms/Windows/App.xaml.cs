using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RazorReaper.Services;
using WinRT.Interop;

namespace RazorReaper.WinUI
{
    public partial class App : MauiWinUIApplication
    {
        private static Mutex? _mutex;
        private AppWindow? _mainAppWindow;
        private bool _wiredCrosshairTray;
        private EventWaitHandle? _showSignal;
        private Action? _requestShowMainWindow;

        // Named event a duplicate launch signals so the running instance shows its window.
        // Needed because Process.MainWindowHandle is IntPtr.Zero for a HIDDEN window, so the
        // ShowWindow/SetForegroundWindow fallback below can't reach a tray-hidden or
        // --waitforark instance.
        private const string ShowSignalName = "RazorReaper_ShowWindow_Event";

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public App()
        {
            // An elevated relaunch (Restart as Administrator) starts a second process while the
            // old, non-elevated one is still exiting and still holds the single-instance mutex.
            // That handoff must NOT be treated as a duplicate launch — otherwise the new elevated
            // instance bails here (before MAUI even starts) and nothing comes back up.
            var relaunchedElevated = Environment.GetCommandLineArgs()
                .Contains(RazorReaper.Services.Elevation.IElevationService.RestartMarker, StringComparer.OrdinalIgnoreCase);

            _mutex = new Mutex(true, "RazorReaper_SingleInstance_Mutex", out bool isNewInstance);

            if (!isNewInstance)
            {
                if (relaunchedElevated)
                {
                    // Wait for the outgoing instance to release the mutex (it does so when it exits),
                    // then continue as the sole instance instead of exiting as a "duplicate".
                    try { _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                    catch (AbandonedMutexException) { /* prior instance exited; ownership is ours now */ }
                }
                else
                {
                    SignalExistingInstanceToShow();
                    BringExistingInstanceToFront();
                    Environment.Exit(0);
                    return;
                }
            }

            this.InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            base.OnLaunched(args);
            // Defer to next tick — MAUI hasn't built the window yet when OnLaunched fires.
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() => TryWireMainWindow());
        }

        private void TryWireMainWindow()
        {
            if (_wiredCrosshairTray) return;

            // Find the MAUI WinUI window
            var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            var winUiWindow = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (winUiWindow == null)
            {
                // Try again next tick — Handler is sometimes built lazily.
                DispatcherQueue.GetForCurrentThread().TryEnqueue(() => TryWireMainWindow());
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(winUiWindow);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _mainAppWindow = AppWindow.GetFromWindowId(windowId);
            // Capture the UI thread's dispatcher up front — the tray callback fires on the overlay's
            // STA thread, which has no WinUI dispatcher of its own, so DispatcherQueue.GetForCurrentThread()
            // from inside the callback would return null and silently no-op.
            var uiDispatcher = winUiWindow.DispatcherQueue;

            // Services were constructed during MAUI startup; resolve from DI.
            var services = IPlatformApplication.Current?.Services;
            var discordPresence = services?.GetService<IDiscordPresenceService>();

            // Intercept the X button — hide the window and keep the process alive so the overlay
            // and tray icon survive. The user quits explicitly via the tray's Quit menu item.
            // Tell Discord we're idling in the tray (drops the per-page activity).
            _mainAppWindow.Closing += (sender, e) =>
            {
                e.Cancel = true;
                sender.Hide();
                discordPresence?.SetMinimizedToTray(true);
            };

            // Single shared show path: tray "Show", the ARK-link watcher, and the duplicate-
            // launch signal all funnel through this.
            _requestShowMainWindow = () =>
            {
                uiDispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        _mainAppWindow?.Show();
                        // SW_RESTORE handles both hidden and minimized states; AppWindow.Show alone
                        // sometimes leaves the window de-activated behind other apps.
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                        // Back in view — restore the per-page Discord activity.
                        discordPresence?.SetMinimizedToTray(false);
                    }
                    catch { /* window already gone */ }
                });
            };

            StartShowSignalListener();

            // ARK link: pop a tray-hidden window back up when the in-app watcher sees ARK
            // start. (Fresh launches at login are handled by the headless --arkwatch mode in
            // Program.cs/ArkWatch.cs — that path never reaches this class.)
            var arkLink = services?.GetService<IArkLinkService>();
            if (arkLink is not null)
            {
                arkLink.ShowAppRequested += () => _requestShowMainWindow?.Invoke();
            }

            // Wire the tray callbacks.
            var crosshair = services?.GetService<ICrosshairService>();
            if (crosshair == null) return;

            crosshair.ShowAppRequested += () => _requestShowMainWindow?.Invoke();

            crosshair.QuitRequested += () =>
            {
                // Hard exit — we want the overlay, tray icon, and everything else torn down.
                // Clear the Discord presence first, while the process (and IPC pipe) is still alive.
                discordPresence?.Shutdown();
                Environment.Exit(0);
            };

            _wiredCrosshairTray = true;
        }

        private void StartShowSignalListener()
        {
            try
            {
                _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
                var listener = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            _showSignal.WaitOne();
                            _requestShowMainWindow?.Invoke();
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                        catch
                        {
                            // Never let the listener die on a transient failure.
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "rr-show-signal"
                };
                listener.Start();
            }
            catch
            {
                // Without the listener a duplicate launch just falls back to
                // BringExistingInstanceToFront (visible windows only).
            }
        }

        private static void SignalExistingInstanceToShow()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
                {
                    using (signal)
                    {
                        signal.Set();
                    }
                }
            }
            catch
            {
                // Fall back to the window-handle path below.
            }
        }

        private static void BringExistingInstanceToFront()
        {
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName);
            try
            {
                foreach (var process in processes)
                {
                    if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        break;
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
