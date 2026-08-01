using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Implementation of IProcessService for process management.
/// </summary>
public class ProcessService : IProcessService
{
    private readonly ILogger<ProcessService> _logger;
    private readonly ITelemetryService _telemetryService;

    public ProcessService(ILogger<ProcessService> logger, ITelemetryService telemetryService)
    {
        _logger = logger;
        _telemetryService = telemetryService;
    }

    /// <inheritdoc/>
    public Process[] GetProcessesByName(string processName)
    {
        try
        {
            _logger.LogDebug("Getting processes by name: {ProcessName}", processName);
            var processes = Process.GetProcessesByName(processName);
            _logger.LogDebug("Found {Count} process(es) with name: {ProcessName}", processes.Length, processName);
            return processes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processes by name: {ProcessName}", processName);
            return Array.Empty<Process>();
        }
    }

    /// <inheritdoc/>
    public bool IsProcessRunning(string processName)
    {
        Process[]? processes = null;
        try
        {
            processes = GetProcessesByName(processName);
            bool isRunning = processes.Length > 0;
            _logger.LogDebug("Process {ProcessName} running: {IsRunning}", processName, isRunning);
            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if process is running: {ProcessName}", processName);
            return false;
        }
        finally
        {
            if (processes is not null)
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { /* dispose must never throw out of finally */ }
                }
            }
        }
    }

    /// <inheritdoc/>
    public string? GetExecutablePath(Process process)
    {
        // QueryFullProcessImageName first: it is the only one of the three that survives an
        // anti-cheat-protected game (BattlEye strips module-read rights, so MainModule goes null).
        try
        {
            var path = QueryImagePath(process.Id);
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QueryFullProcessImageName failed for PID {ProcessId}", process.Id);
        }

        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MainModule lookup failed for PID {ProcessId}", process.Id);
        }

        _logger.LogWarning("Could not resolve the image path of PID {ProcessId}", process.Id);
        return null;
    }

    private static string? QueryImagePath(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <inheritdoc/>
    public Process? Start(string filePath)
    {
        try
        {
            _logger.LogInformation("Starting process: {FilePath}", filePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true // Required for steam:// and other URI protocols
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                // Shell-executed URIs (steam://, https://, etc.) succeed without a parent process handle.
                _logger.LogDebug("Process.Start returned null for shell-launched target: {FilePath}", filePath);
                _ = _telemetryService.TrackEventAsync(
                    "process_start",
                    TelemetryEventStatus.Ok,
                    "Process launch requested (shell-executed URI).",
                    BuildProcessMetrics(filePath, null));
                return null;
            }

            _ = _telemetryService.TrackEventAsync(
                "process_start",
                TelemetryEventStatus.Ok,
                "Process launch requested.",
                BuildProcessMetrics(filePath, process));

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process: {FilePath}", filePath);
            _ = _telemetryService.TrackEventAsync(
                "process_start",
                TelemetryEventStatus.Down,
                ex.Message,
                BuildProcessMetrics(filePath, null));
            throw;
        }
    }

    /// <inheritdoc/>
    public void Kill(Process process)
    {
        try
        {
            _logger.LogInformation("Killing process: {ProcessName} (ID: {ProcessId})",
                process.ProcessName, process.Id);
            process.Kill();
            _ = _telemetryService.TrackEventAsync(
                "process_kill",
                TelemetryEventStatus.Ok,
                "Process kill requested.",
                new Dictionary<string, object?>
                {
                    ["process_name"] = process.ProcessName,
                    ["process_id"] = process.Id
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error killing process: {ProcessName} (ID: {ProcessId})",
                process.ProcessName, process.Id);
            _ = _telemetryService.TrackEventAsync(
                "process_kill",
                TelemetryEventStatus.Down,
                ex.Message,
                new Dictionary<string, object?>
                {
                    ["process_name"] = process.ProcessName,
                    ["process_id"] = process.Id
                });
            throw;
        }
    }

    private static Dictionary<string, object?> BuildProcessMetrics(string filePath, Process? process)
    {
        var metrics = new Dictionary<string, object?>
        {
            ["target"] = filePath,
            ["target_name"] = Path.GetFileName(filePath)
        };

        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri))
        {
            metrics["target_scheme"] = uri.Scheme;
            metrics["target_is_uri"] = !uri.IsFile;
        }
        else
        {
            metrics["target_scheme"] = "file";
            metrics["target_is_uri"] = false;
        }

        if (process is not null)
        {
            metrics["process_id"] = process.Id;
            metrics["process_name"] = process.ProcessName;
        }

        return metrics;
    }
}
