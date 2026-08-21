using RazorReaper.Services;

namespace RazorReaper.UnitTests.Infrastructure;

public sealed class FakeSecureValueStore : ISecureValueStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public int GetCallCount { get; private set; }
    public int SetCallCount { get; private set; }

    /// <summary>When set, every read/write throws it — simulates a broken DPAPI store.</summary>
    public Exception? Failure { get; set; }

    public Task<string?> GetAsync(string key)
    {
        lock (_gate)
        {
            GetCallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }
    }

    public Task SetAsync(string key, string value)
    {
        lock (_gate)
        {
            SetCallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            return _values.Remove(key);
        }
    }

    public string? Peek(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Seed(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = value;
        }
    }
}
