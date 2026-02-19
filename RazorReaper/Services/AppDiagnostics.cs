using System.Globalization;
using System.IO;
using Microsoft.Maui.Storage;
using Serilog;

namespace RazorReaper.Services;

public sealed record DiagnosticErrorInfo(string Code, string Message, DateTimeOffset Timestamp);

public static class AppDiagnostics
{
    private const string LoggingEnabledKey = "rr.logging.enabled";
    private const string VerboseLoggingKey = "rr.logging.verbose";
    private const string LogFolderKey = "rr.logging.folder";
    private const string TelemetryEnabledKey = "rr.telemetry.enabled";
    private const string LastErrorCodeKey = "rr.error.code";
    private const string LastErrorMessageKey = "rr.error.message";
    private const string LastErrorTimeKey = "rr.error.time";

    public const string DefaultLogFileName = "app.log";

    public static string DefaultLogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RazorReaper",
        "Logs");

    public static bool GetLoggingEnabled()
    {
        return Preferences.Get(LoggingEnabledKey, true);
    }

    public static void SetLoggingEnabled(bool enabled)
    {
        Preferences.Set(LoggingEnabledKey, enabled);
    }

    public static bool GetVerboseLoggingEnabled()
    {
        return Preferences.Get(VerboseLoggingKey, false);
    }

    public static void SetVerboseLoggingEnabled(bool enabled)
    {
        Preferences.Set(VerboseLoggingKey, enabled);
    }

    public static string GetLogFolder()
    {
        var folder = Preferences.Get(LogFolderKey, string.Empty);
        return string.IsNullOrWhiteSpace(folder) ? DefaultLogFolder : folder;
    }

    public static void SetLogFolder(string folder)
    {
        Preferences.Set(LogFolderKey, folder);
    }

    public static string GetLogFilePath()
    {
        return Path.Combine(GetLogFolder(), DefaultLogFileName);
    }

    public static bool GetTelemetryEnabled(bool defaultValue)
    {
        return Preferences.Get(TelemetryEnabledKey, defaultValue);
    }

    public static void SetTelemetryEnabled(bool enabled)
    {
        Preferences.Set(TelemetryEnabledKey, enabled);
    }

    public static void ClearTelemetryEnabledOverride()
    {
        Preferences.Remove(TelemetryEnabledKey);
    }

    public static void RecordError(string code, string message, Exception? exception = null)
    {
        try
        {
            Preferences.Set(LastErrorCodeKey, code);
            Preferences.Set(LastErrorMessageKey, message ?? string.Empty);
            Preferences.Set(LastErrorTimeKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
        }

        try
        {
            if (exception != null)
            {
                Log.Error(exception, "App error {ErrorCode}: {Message}", code, message);
            }
            else
            {
                Log.Error("App error {ErrorCode}: {Message}", code, message);
            }
        }
        catch
        {
        }
    }

    public static DiagnosticErrorInfo? GetLastError()
    {
        var code = Preferences.Get(LastErrorCodeKey, string.Empty);
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var message = Preferences.Get(LastErrorMessageKey, string.Empty);
        var rawTimestamp = Preferences.Get(LastErrorTimeKey, string.Empty);
        var timestamp = DateTimeOffset.MinValue;

        if (!string.IsNullOrWhiteSpace(rawTimestamp))
        {
            DateTimeOffset.TryParse(
                rawTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out timestamp);
        }

        return new DiagnosticErrorInfo(code, message, timestamp);
    }

    public static void ClearLastError()
    {
        Preferences.Remove(LastErrorCodeKey);
        Preferences.Remove(LastErrorMessageKey);
        Preferences.Remove(LastErrorTimeKey);
    }
}
