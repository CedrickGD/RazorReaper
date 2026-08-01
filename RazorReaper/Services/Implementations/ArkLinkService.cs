using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

public sealed class ArkLinkService : IArkLinkService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Consecutive "not running" polls required before a state change is trusted. Process
    /// enumeration can transiently miss a live process, and a single 3s blip must neither
    /// tear down the app nor re-trigger the show-window path.
    /// </summary>
    private const int ExitConfirmPolls = 2;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RazorReaper";

    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly IDiscordPresenceService _discordPresence;
    private readonly ILogger<ArkLinkService> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _watchCts;
    private bool _started;

    public ArkLinkService(
        IProcessService process,
        IOptions<AppConfiguration> config,
        IDiscordPresenceService discordPresence,
        ILogger<ArkLinkService> logger)
    {
        _process = process;
        _config = config;
        _discordPresence = discordPresence;
        _logger = logger;

        MigrateLegacyPreference();
    }

    private Action? _showAppRequested;
    private int _showPending;

    public event Action? ShowAppRequested
    {
        add
        {
            _showAppRequested += value;

            // Replay a show that fired before anyone was wired up: the login autostart
            // instance polls ARK ~3s after Start(), which can beat the platform window
            // wiring on a busy login — without the latch that one-shot show would be
            // lost and the window would stay hidden until ARK restarts.
            if (Interlocked.Exchange(ref _showPending, 0) == 1)
            {
                try { value?.Invoke(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Replayed show-app callback failed."); }
            }
        }
        remove => _showAppRequested -= value;
    }

    private void RaiseShowApp()
    {
        var handler = _showAppRequested;
        if (handler is null)
        {
            Interlocked.Exchange(ref _showPending, 1);
            return;
        }

        try { handler.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Show-app callback failed."); }
    }

    public bool StartWithArk
    {
        get => Preferences.Get(IArkLinkService.StartWithArkPreferenceKey, false);
        set
        {
            if (value == StartWithArk)
                return;

            Preferences.Set(IArkLinkService.StartWithArkPreferenceKey, value);
            _logger.LogInformation("ARK link start-with-ARK {State}.", value ? "enabled" : "disabled");

            if (value)
            {
                RegisterAutostart();
            }
            else
            {
                UnregisterAutostart();
            }

            RefreshWatcher();
        }
    }

    public bool CloseWithArk
    {
        get => Preferences.Get(IArkLinkService.CloseWithArkPreferenceKey, false);
        set
        {
            if (value == CloseWithArk)
                return;

            Preferences.Set(IArkLinkService.CloseWithArkPreferenceKey, value);
            _logger.LogInformation("ARK link close-with-ARK {State}.", value ? "enabled" : "disabled");

            RefreshWatcher();
        }
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        if (StartWithArk)
        {
            // Re-write the entry every enabled start so it always points at the current
            // exe (auto-updates or a moved install would otherwise leave it stale).
            RegisterAutostart();
        }
        else
        {
            // Drop a stale login entry if one survived (e.g. preferences were reset while
            // the entry stayed behind) so a disabled toggle never keeps autostarting us.
            UnregisterAutostart();
        }

        RefreshWatcher();
    }

    public void Dispose() => StopWatcher();

    /// <summary>
    /// The pre-split builds stored one combined toggle; carry it into both new options so
    /// nobody loses their setting.
    /// </summary>
    private void MigrateLegacyPreference()
    {
        try
        {
            if (!Preferences.ContainsKey(IArkLinkService.LegacyEnabledPreferenceKey))
                return;

            if (Preferences.Get(IArkLinkService.LegacyEnabledPreferenceKey, false))
            {
                if (!Preferences.ContainsKey(IArkLinkService.StartWithArkPreferenceKey))
                    Preferences.Set(IArkLinkService.StartWithArkPreferenceKey, true);
                if (!Preferences.ContainsKey(IArkLinkService.CloseWithArkPreferenceKey))
                    Preferences.Set(IArkLinkService.CloseWithArkPreferenceKey, true);
                _logger.LogInformation("Migrated legacy ARK link toggle to split start/close options.");
            }

            Preferences.Remove(IArkLinkService.LegacyEnabledPreferenceKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ARK link legacy preference migration failed.");
        }
    }

    private void RefreshWatcher()
    {
        if (StartWithArk || CloseWithArk)
        {
            StartWatcher();
        }
        else
        {
            StopWatcher();
        }
    }

    private void StartWatcher()
    {
        lock (_gate)
        {
            if (_watchCts is not null)
                return;

            var cts = new CancellationTokenSource();
            _watchCts = cts;
            _ = Task.Run(() => WatchLoopAsync(cts.Token));
        }
    }

    private void StopWatcher()
    {
        lock (_gate)
        {
            if (_watchCts is null)
                return;

            _watchCts.Cancel();
            _watchCts.Dispose();
            _watchCts = null;
        }
    }

    private async Task WatchLoopAsync(CancellationToken token)
    {
        var processName = _config.Value.Ark.GameProcessName;

        // Debounced ARK state: null until the first poll settles. Show fires on a
        // confirmed off → on transition (brings a tray-hidden instance back into view —
        // fresh launches are the login watcher's job, see Platforms/Windows/ArkWatch.cs);
        // quit fires on a confirmed on → off transition. The toggles are re-read at each
        // event so flipping them mid-session takes effect without a watcher restart.
        bool? arkRunning = null;
        var missedPolls = 0;

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_process.IsProcessRunning(processName))
                {
                    missedPolls = 0;
                    if (arkRunning != true)
                    {
                        var cameUp = arkRunning == false;
                        arkRunning = true;

                        if (cameUp && StartWithArk && !token.IsCancellationRequested)
                        {
                            _logger.LogInformation("ARK detected — bringing RazorReaper into view.");
                            RaiseShowApp();
                        }
                    }
                    continue;
                }

                if (arkRunning is null)
                {
                    arkRunning = false;
                    continue;
                }

                if (arkRunning == false || ++missedPolls < ExitConfirmPolls)
                    continue;

                // Confirmed on → off transition.
                arkRunning = false;
                missedPolls = 0;

                if (CloseWithArk)
                {
                    QuitApp(token);
                    return;
                }

                // Close-with-ARK is off: keep watching so a later ARK start can still
                // bring the window up.
            }
        }
        catch (OperationCanceledException)
        {
            // Toggles turned off or app shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ARK link watcher failed.");
        }
    }

    private void RegisterAutostart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                _logger.LogWarning("ARK link autostart not registered — executable path unavailable.");
                return;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunValueName, $"\"{exe}\" {IArkLinkService.ArkWatchArg}");
            _logger.LogInformation("ARK link autostart registered ({Exe}).", exe);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register the ARK link autostart entry.");
        }
    }

    private void UnregisterAutostart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                _logger.LogInformation("ARK link autostart entry removed.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove the ARK link autostart entry.");
        }
    }

    private void QuitApp(CancellationToken token)
    {
        // Last chance to bail if the user flipped the toggle off while this tick was in
        // flight; past this point the close is committed.
        if (token.IsCancellationRequested)
            return;

        _logger.LogInformation("ARK exited — closing RazorReaper (close with ARK is enabled).");

        // Clear the Discord presence while the process (and IPC pipe) is still alive,
        // matching the tray Quit path in Platforms/Windows/App.xaml.cs.
        try { _discordPresence.Shutdown(); } catch { /* best effort */ }

        // With the window hidden to tray (the X button hides instead of closing), WinUI's
        // Application.Exit can no-op — it needs an open activated window — and Quit() has
        // known failure modes on Windows. Mirror ElevationService.QuitCurrentInstance:
        // graceful Quit on the dispatcher, Environment.Exit as the guaranteed fallback.
        // Environment.Exit still raises ProcessExit, so the pending-installer launch and
        // telemetry flush in App.HandleProcessExit run either way.
        try
        {
            var app = Application.Current;
            if (app is not null)
            {
                app.Dispatcher.Dispatch(() =>
                {
                    try { app.Quit(); }
                    catch { Environment.Exit(0); }
                });
            }
            else
            {
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful quit failed; forcing exit.");
            Environment.Exit(0);
        }

        // Backstop: if the graceful quit didn't end the process shortly, force it so the
        // app never lingers invisibly in the tray after ARK closed.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }
}
