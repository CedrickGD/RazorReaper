using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Shared "is ARK the foreground window?" gate for automation scripts. Every vision-driven script
/// must pause while another window covers the game (the captured HUD pixels would be meaningless and
/// injected input would land in the wrong app). Extracted so scripts share one implementation instead
/// of each re-doing the GetForegroundWindow + process-id dance. Caches the ARK pids for a few seconds
/// so a tight scan loop doesn't enumerate processes on every tick.
/// </summary>
public interface IForegroundGate
{
    /// <summary>True when the current foreground window belongs to the configured ARK game process.</summary>
    bool IsGameForeground();
}

/// <summary>Default <see cref="IForegroundGate"/> implementation.</summary>
public sealed class ForegroundGate : IForegroundGate
{
    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly object _gate = new();
    private HashSet<uint> _gamePids = new();
    private long _refreshedAt = long.MinValue;

    public ForegroundGate(IProcessService process, IOptions<AppConfiguration> config)
    {
        _process = process;
        _config = config;
    }

    public bool IsGameForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);

            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (now - _refreshedAt > 5000)
                {
                    _refreshedAt = now;
                    var processes = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
                    try { _gamePids = processes.Select(p => (uint)p.Id).ToHashSet(); }
                    finally { foreach (var p in processes) p?.Dispose(); }
                }
                return _gamePids.Contains(pid);
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
