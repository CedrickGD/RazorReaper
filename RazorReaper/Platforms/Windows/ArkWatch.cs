using System.Diagnostics;
using RazorReaper.Services;

namespace RazorReaper.WinUI;

/// <summary>
/// Headless login watcher ("--arkwatch"): started by the Run-key entry at Windows login,
/// keeps no window and never initializes XAML/MAUI/WebView2. It just polls the process list
/// and launches a normal RazorReaper instance the moment ShooterGame appears.
/// </summary>
internal static class ArkWatch
{
    private const string WatchMutexName = "RazorReaper_ArkWatch_Mutex";

    /// <summary>
    /// Must match the single-instance mutex name in App.xaml.cs — while a UI instance is
    /// alive it owns that mutex, so its existence tells the watcher not to launch another.
    /// </summary>
    private const string UiMutexName = "RazorReaper_SingleInstance_Mutex";

    /// <summary>
    /// Matches AppConfiguration.Ark.GameProcessName's default. The watcher deliberately
    /// avoids the whole config stack to stay tiny.
    /// </summary>
    private const string GameProcessName = "ShooterGame";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Consecutive misses before ARK counts as exited — a transient enumeration blip must
    /// not reset the transition state (that could relaunch RR the user just closed mid-game).
    /// </summary>
    private const int ExitConfirmPolls = 2;

    public static bool ShouldRunWatchMode(string[] args) => args.Any(a =>
        string.Equals(a, IArkLinkService.ArkWatchArg, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a, IArkLinkService.LegacyWaitForArkArg, StringComparison.OrdinalIgnoreCase));

    public static void Run()
    {
        using var mutex = new Mutex(true, WatchMutexName, out var isOnlyWatcher);
        if (!isOnlyWatcher)
            return;

        // Transition-based: RR is launched only when ARK goes (confirmed) not-running →
        // running, so closing RazorReaper mid-game doesn't get it relaunched. The initial
        // state counts as not-running, so an ARK that is already up when the watcher starts
        // brings RR up once on the first poll.
        var arkRunning = false;
        var missedPolls = 0;

        while (true)
        {
            try
            {
                if (IsProcessRunning(GameProcessName))
                {
                    missedPolls = 0;
                    if (!arkRunning)
                    {
                        arkRunning = true;
                        if (!IsUiInstanceRunning())
                            LaunchRazorReaper();
                    }
                }
                else if (arkRunning && ++missedPolls >= ExitConfirmPolls)
                {
                    arkRunning = false;
                    missedPolls = 0;
                }
            }
            catch
            {
                // The login watcher must never die on a transient failure; next poll retries.
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { /* dispose must never throw */ }
            }
        }
    }

    private static bool IsUiInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(UiMutexName, out var ui))
            {
                ui.Dispose();
                return true;
            }
        }
        catch
        {
            // Access denied etc. — treat as not running; the UI's own single-instance
            // handling still prevents duplicates.
        }

        return false;
    }

    private static void LaunchRazorReaper()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                UseShellExecute = true
            })?.Dispose();
        }
        catch
        {
            // Nothing sensible to do headless; the next ARK start cycle retries.
        }
    }
}
