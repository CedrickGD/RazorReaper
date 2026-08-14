using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services.Overlay;

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

    /// <summary>Total seconds of the current activation, or 0 when inactive (drives the progress bar).</summary>
    int TotalSeconds { get; }

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
    private readonly IArkPathProvider _arkPaths;
    private readonly IHudOverlayService _hud;
    private readonly IOptions<AppConfiguration> _config;
    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;
    private readonly IUsageGateService _usageGate;
    private readonly ILogger<DesyncService> _logger;

    private readonly object _gate = new();
    private readonly Task _startupCleanup;
    private CancellationTokenSource? _cts;
    private DateTime _revertAtUtc;
    private int _totalSeconds;
    private volatile bool _active;
    private bool _activating;
    private bool _disposed;

    public DesyncService(
        IProcessService process,
        IArkPathProvider arkPaths,
        IHudOverlayService hud,
        IOptions<AppConfiguration> config,
        INotificationService notifications,
        IActivityService activity,
        IUsageGateService usageGate,
        ILogger<DesyncService> logger)
    {
        _process = process;
        _arkPaths = arkPaths;
        _hud = hud;
        _config = config;
        _notifications = notifications;
        _activity = activity;
        _usageGate = usageGate;
        _logger = logger;

        // A rule could survive a crash from a previous run — clear it at startup. Kept as a task so an
        // activation that lands first can await it: otherwise this delete would race in behind the add
        // and silently remove the rule the user just asked for.
        _startupCleanup = Task.Run(() => RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\""));
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
        => _active ? Math.Max(0, (int)Math.Ceiling((_revertAtUtc - DateTime.UtcNow).TotalSeconds)) : 0;

    public int TotalSeconds => _active ? _totalSeconds : 0;

    public event Action? Changed;

    public async Task<bool> ActivateAsync(int seconds)
    {
        if (_disposed) return false;
        if (_active) return true;

        // Claimed before the first await: two overlapping calls (double-click) would both
        // pass the _active check, add the rule twice and consume the quota twice.
        lock (_gate)
        {
            if (_activating) return false;
            _activating = true;
        }

        try
        {
            return await ActivateCoreAsync(seconds);
        }
        finally
        {
            lock (_gate) { _activating = false; }
        }
    }

    private async Task<bool> ActivateCoreAsync(int seconds)
    {
        if (_active) return true;

        if (!IsAdministrator)
        {
            _notifications.ShowWarning("Desync needs RazorReaper to run as Administrator — restart it elevated.");
            return false;
        }

        if (!_process.IsProcessRunning(_config.Value.Ark.GameProcessName))
        {
            _notifications.ShowWarning("ARK isn't running — start the game first.");
            return false;
        }

        var exePath = ResolveArkExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _notifications.ShowError("Could not locate ShooterGame.exe — check that ARK is installed where Steam reports it.");
            return false;
        }

        // Let the one-shot startup cleanup finish first, so it can't delete the rule we're about to add.
        try { await _startupCleanup; } catch { /* best-effort */ }

        seconds = Math.Clamp(seconds, 5, MaxSeconds);
        var add = await RunNetshAsync(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block program=\"{exePath}\" profile=any enable=yes");
        if (!add.Success)
        {
            _logger.LogError("Desync could not add the firewall rule for {ExePath}: {Output}", exePath, add.Output);
            _notifications.ShowError(add.Output.Length > 0
                ? $"Could not create the firewall rule: {add.Output}"
                : "Could not create the firewall rule (needs Administrator).");
            return false;
        }

        // Counted only after the rule actually exists — a failed pre-check or netsh error must
        // not burn a use. If the month is used up, take the rule right back out; deactivate and
        // the auto-revert themselves never count.
        var quota = await _usageGate.TryConsumeAsync(UsageFeatures.Desync);
        if (!quota.Allowed)
        {
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"");
            _notifications.ShowWarning($"Free monthly limit reached ({quota.Limit} desync activations). Resets next month — Premium is unlimited.");
            return false;
        }

        DateTime revertAt;
        lock (_gate)
        {
            _active = true;
            _totalSeconds = seconds;
            revertAt = _revertAtUtc = DateTime.UtcNow.AddSeconds(seconds);
            _cts = new CancellationTokenSource();
        }

        _notifications.ShowSuccess($"Desync active — auto-reverts in {seconds}s.");
        TryActivity($"Desync activated ({seconds}s)", "warning");
        TryHud(revertAt);
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
        cts?.Dispose();
        TryHud(null);

        var del = await RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (!del.Success)
            _logger.LogWarning("Desync revert: netsh delete rule reported failure: {Output}", del.Output);
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
            TryHud(null);
        }
    }

    /// <summary>
    /// Full path of ARK's executable, preferring the running process so the rule always matches the
    /// image actually on screen, and falling back to the detected install when the process can't be
    /// read at all.
    /// </summary>
    private string? ResolveArkExecutablePath()
    {
        var processes = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
        try
        {
            foreach (var p in processes)
            {
                var path = _process.GetExecutablePath(p);
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
        }
        finally
        {
            foreach (var p in processes) p?.Dispose();
        }

        return ResolveInstalledExecutablePath();
    }

    private string? ResolveInstalledExecutablePath()
    {
        try
        {
            var arkPath = _arkPaths.FindArkPath();
            if (string.IsNullOrWhiteSpace(arkPath)) return null;

            var exePath = Path.Combine(arkPath, _config.Value.Ark.ExecutableRelativePath);
            if (!File.Exists(exePath)) return null;

            _logger.LogInformation("Desync fell back to the installed ARK executable at {ExePath}", exePath);
            return exePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Desync could not resolve the installed ARK executable");
            return null;
        }
    }

    /// <summary>Outcome of a netsh call; <paramref name="Output"/> carries the reason a call failed.</summary>
    private readonly record struct NetshResult(bool Success, string Output);

    private async Task<NetshResult> RunNetshAsync(string arguments)
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
            if (proc is null) return new NetshResult(false, "netsh could not be started.");

            // Read both streams before waiting, or a full pipe buffer would deadlock the wait.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var output = string.Concat(await stdout, await stderr).Trim();
            return new NetshResult(proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "netsh call failed: {Args}", arguments);
            return new NetshResult(false, ex.Message);
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

    private void TryHud(DateTime? revertAtUtc)
    {
        try { _hud.SetDesync(revertAtUtc); }
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
            TryHud(null);
            RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"").GetAwaiter().GetResult();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Desync cleanup on dispose failed"); }
    }
}
