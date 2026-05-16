using Microsoft.JSInterop;

namespace RazorReaper.Diagnostics;

/// <summary>
/// Static JS bridge used exclusively by the runtime test runner
/// (<c>wwwroot/_tests/test-runner.js</c>). Appends a line to
/// <c>%LOCALAPPDATA%\RazorReaper\Logs\test-results.log</c>.
///
/// Static so the runner can call it via <c>DotNet.invokeMethodAsync</c>
/// without needing a per-component DotNetObjectReference to be registered
/// first — that registration only happens once SharedNavbar's OnAfterRender
/// fires, which is too late for diagnostics about the runner itself.
/// </summary>
public static class TestLogBridge
{
    [JSInvokable("WriteTestLog")]
    public static Task WriteTestLog(string message)
    {
        string? attemptedPath = null;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RazorReaper",
                "Logs");
            Directory.CreateDirectory(dir);
            attemptedPath = Path.Combine(dir, "test-results.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(attemptedPath, line);
            Serilog.Log.Information("TestLogBridge OK: {Message} -> {Path}", message, attemptedPath);
        }
        catch (Exception ex)
        {
            try { Serilog.Log.Warning(ex, "TestLogBridge FAIL: {Message} path={Path}", message, attemptedPath); }
            catch { /* */ }
        }
        return Task.CompletedTask;
    }
}
