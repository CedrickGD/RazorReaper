using Microsoft.Extensions.Logging;

namespace RazorReaper.UnitTests.Infrastructure;

/// <summary>Collects every formatted log entry so tests can assert what was (not) logged.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Number of entries whose message contains <paramref name="fragment"/> (case-insensitive).</summary>
    public int Count(string fragment)
        => Entries.Count(entry => entry.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_gate)
        {
            _entries.Add((logLevel, message));
        }
    }
}
