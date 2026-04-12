using Serilog.Core;
using Serilog.Events;

namespace RazorReaper.Diagnostics;

public static class LoggingControl
{
    private static LoggingLevelSwitch? _levelSwitch;

    public static void Initialize(LoggingLevelSwitch levelSwitch)
    {
        _levelSwitch = levelSwitch;
    }

    public static void ApplySettings(bool enabled, bool verbose)
    {
        if (_levelSwitch == null)
        {
            return;
        }

        if (!enabled)
        {
            _levelSwitch.MinimumLevel = LogEventLevel.Fatal;
            return;
        }

        _levelSwitch.MinimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;
    }
}
