using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Elevation;

/// <summary>
/// Restarts RazorReaper with administrator rights via a UAC prompt. Used by features that need
/// elevation (Desync's firewall rule, the File Modifier's game-folder writes/deletes). The
/// relaunched instance is passed the old PID so it can wait for this one to exit before touching
/// the shared WebView2 data folder (see MauiProgram.WaitForPriorInstanceIfRelaunched).
/// </summary>
public interface IElevationService
{
    /// <summary>True when the current process is already running elevated.</summary>
    bool IsElevated { get; }

    /// <summary>Command-line marker the relaunched elevated instance is started with.</summary>
    static string RestartMarker => "--elevated-restart";

    /// <summary>Command-line flag carrying the route to return to after an elevated relaunch.</summary>
    static string PageMarker => "--elevated-page";

    /// <summary>
    /// The page route the app should jump to after being relaunched elevated (e.g. "desync"),
    /// or null. Reading it consumes it, so the jump only happens once.
    /// </summary>
    string? ConsumePendingReturnRoute();

    /// <summary>
    /// Relaunch elevated, returning afterwards to <paramref name="returnRoute"/> (a page-relative
    /// route like "desync", or null for the default). On success the current process is terminated
    /// and the method does not return. Returns false only when elevation could not be started —
    /// user cancelled the UAC prompt, or an error — with a reason in <paramref name="error"/>.
    /// </summary>
    bool RelaunchAsAdministrator(string? returnRoute, out string? error);
}

public sealed class ElevationService : IElevationService
{
    // ERROR_CANCELLED — user clicked "No" on the UAC prompt.
    private const int ErrorCancelled = 1223;

    private readonly ILogger<ElevationService> _logger;
    private string? _pendingReturnRoute;
    private bool _pendingConsumed;

    public ElevationService(ILogger<ElevationService> logger)
    {
        _logger = logger;

        // If we were relaunched elevated with a return route, capture it for the first navigation.
        try
        {
            var args = Environment.GetCommandLineArgs();
            var idx = Array.IndexOf(args, IElevationService.PageMarker);
            if (idx >= 0 && idx + 1 < args.Length)
            {
                var route = args[idx + 1].Trim();
                // Only accept a simple app-relative route (no scheme/host/backtracking).
                if (route.Length > 0 && !route.Contains("://") && !route.Contains(".."))
                {
                    _pendingReturnRoute = route.TrimStart('/');
                }
            }
        }
        catch { /* never let arg parsing break startup */ }
    }

    public string? ConsumePendingReturnRoute()
    {
        if (_pendingConsumed) return null;
        _pendingConsumed = true;
        return _pendingReturnRoute;
    }

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public bool RelaunchAsAdministrator(string? returnRoute, out string? error)
    {
        error = null;

        if (IsElevated)
        {
            // Already elevated — nothing to do; callers treat "true" as "you have admin now".
            return true;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            error = "Could not locate the RazorReaper executable to restart.";
            _logger.LogWarning("Elevation aborted — Environment.ProcessPath was '{Path}'", exePath);
            return false;
        }

        var arguments = $"{IElevationService.RestartMarker} {Environment.ProcessId}";
        var route = returnRoute?.Trim().TrimStart('/');
        if (!string.IsNullOrWhiteSpace(route) && !route.Contains(' ') && !route.Contains("..") && !route.Contains("://"))
        {
            arguments += $" {IElevationService.PageMarker} {route}";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true, // required for the "runas" verb (UAC)
                Verb = "runas",
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            error = "Administrator access was declined.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relaunch elevated");
            error = "Could not restart with administrator rights. See the log for details.";
            return false;
        }

        // The elevated instance is starting; shut this one down so it can take over cleanly.
        _logger.LogInformation("Relaunched elevated; exiting current instance");
        QuitCurrentInstance();
        return true;
    }

    private void QuitCurrentInstance()
    {
        try
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app is not null)
            {
                app.Dispatcher.Dispatch(() =>
                {
                    try { app.Quit(); }
                    catch { Environment.Exit(0); }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful quit failed; forcing exit");
        }

        // Backstop: if the graceful quit didn't end the process shortly, force it so the elevated
        // instance never has to contend with this one for the WebView2 data folder.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Environment.Exit(0);
        });
    }
}
