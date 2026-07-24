using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Desync;

/// <summary>
/// Blocks ARK's outbound traffic with a temporary Windows Firewall rule, so your character freezes
/// server-side while the game keeps receiving. Requires the app to run as Administrator (creating a
/// firewall rule does). Safety is deliberate and layered: an activation always carries an
/// auto-revert deadline, <see cref="DeactivateAsync"/> removes the rule, and <see cref="Dispose"/>
/// removes it again on app exit — the rule must never outlive the session.
/// </summary>
public interface IDesyncService : IDisposable
{
    /// <summary>True when the app is running elevated (required to add/remove the firewall rule).</summary>
    bool IsAdministrator { get; }

    /// <summary>True while the block rule is in place.</summary>
    bool IsActive { get; }

    /// <summary>Seconds left before the automatic revert, or 0 when inactive.</summary>
    int RemainingSeconds { get; }

    /// <summary>Raised when active state / countdown changes.</summary>
    event Action? Changed;

    /// <summary>Adds the outbound block rule for the running ARK executable, auto-reverting after <paramref name="seconds"/>.</summary>
    Task<bool> ActivateAsync(int seconds);

    /// <summary>Removes the block rule immediately.</summary>
    Task DeactivateAsync();
}

/// <summary>Default <see cref="IDesyncService"/> implementation (netsh advfirewall).</summary>
public sealed class DesyncService : IDesyncService
{
    private const string RuleName = "RazorReaper Desync Block";
    private const int MaxSeconds = 600;

    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;
    private readonly ILogger<DesyncService> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private DateTime _revertAtUtc;
    private volatile bool _active;
    private bool _disposed;

    public DesyncService(
        IProcessService process,
        IOptions<AppConfiguration> config,
        INotificationService notifications,
        IActivityService activity,
        ILogger<DesyncService> logger)
    {
        _process = process;
        _config = config;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;

        // A rule could survive a crash from a previous run — clear it at startup.
        _ = Task.Run(() => RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\""));
    }

    public bool IsAdministrator
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

    public bool IsActive => _active;

    public int RemainingSeconds
        => _active ? Math.Max(0, (int)(_revertAtUtc - DateTime.UtcNow).TotalSeconds) : 0;

    public event Action? Changed;

    public async Task<bool> ActivateAsync(int seconds)
    {
        if (_disposed) return false;
        if (_active) return true;

        if (!IsAdministrator)
        {
            _notifications.ShowWarning("Desync needs RazorReaper to run as Administrator — restart it elevated.");
            return false;
        }

        var exePath = ResolveArkExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _notifications.ShowWarning("ARK isn't running — start the game first.");
            return false;
        }

        seconds = Math.Clamp(seconds, 5, MaxSeconds);
        var ok = await RunNetshAsync(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block program=\"{exePath}\" enable=yes");
        if (!ok)
        {
            _notifications.ShowError("Could not create the firewall rule (needs Administrator).");
            return false;
        }

        lock (_gate)
        {
            _active = true;
            _revertAtUtc = DateTime.UtcNow.AddSeconds(seconds);
            _cts = new CancellationTokenSource();
        }

        _notifications.ShowSuccess($"Desync active — auto-reverts in {seconds}s.");
        TryActivity($"Desync activated ({seconds}s)", "warning");
        RaiseChanged();

        _ = Task.Run(() => AutoRevertAsync(_cts!.Token, seconds));
        return true;
    }

    public async Task DeactivateAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }

        await RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"");
        _notifications.ShowInfo("Desync reverted — traffic restored.");
        TryActivity("Desync reverted", "info");
        RaiseChanged();
    }

    private async Task AutoRevertAsync(CancellationToken ct, int seconds)
    {
        try
        {
            // Tick so the UI can show the countdown; revert when it runs out.
            for (var i = 0; i < seconds && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, ct);
                RaiseChanged();
            }
            if (!ct.IsCancellationRequested)
                await DeactivateAsync();
        }
        catch (OperationCanceledException) { /* manual deactivate */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Desync auto-revert failed — forcing rule removal");
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"");
        }
    }

    private string? ResolveArkExecutablePath()
    {
        var processes = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                catch { /* access denied on some processes — try the next */ }
            }
            return null;
        }
        finally
        {
            foreach (var p in processes) p?.Dispose();
        }
    }

    private async Task<bool> RunNetshAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "netsh call failed: {Args}", arguments);
            return false;
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Desync Changed subscriber threw"); }
    }

    private void TryActivity(string title, string type)
    {
        try { _activity.AddActivity(title, type); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Never leave the block rule behind.
        try
        {
            lock (_gate) { _cts?.Cancel(); _active = false; }
            RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"").GetAwaiter().GetResult();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Desync cleanup on dispose failed"); }
    }
}
