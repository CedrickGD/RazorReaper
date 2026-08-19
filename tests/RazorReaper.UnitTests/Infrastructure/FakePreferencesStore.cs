using RazorReaper.Services;

namespace RazorReaper.UnitTests.Infrastructure;

public sealed class FakePreferencesStore
    : IPreferencesStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<string> _getKeys = [];

    public int GetCallCount { get; private set; }
    public int SetCallCount { get; private set; }
    public int ContainsKeyCallCount { get; private set; }
    public int RemoveCallCount { get; private set; }

    public IReadOnlyList<string> GetKeys
    {
        get
        {
            lock (_gate)
            {
                return _getKeys.ToArray();
            }
        }
    }

    public object? Get(string key, object? defaultValue = null)
    {
        lock (_gate)
        {
            GetCallCount++;
            _getKeys.Add(key);
            return _values.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }

    public void Set(string key, object? value)
    {
        lock (_gate)
        {
            SetCallCount++;
            _values[key] = value;
        }
    }

    public bool ContainsKey(string key)
    {
        lock (_gate)
        {
            ContainsKeyCallCount++;
            return _values.ContainsKey(key);
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            RemoveCallCount++;
            return _values.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
        }
    }

    public void Seed(string key, object? value)
    {
        lock (_gate)
        {
            _values[key] = value;
        }
    }

    public object? Peek(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void ResetCallCounts()
    {
        lock (_gate)
        {
            GetCallCount = 0;
            SetCallCount = 0;
            ContainsKeyCallCount = 0;
            RemoveCallCount = 0;
            _getKeys.Clear();
        }
    }

    T IPreferencesStore.Get<T>(string key, T defaultValue)
    {
        lock (_gate)
        {
            GetCallCount++;
            _getKeys.Add(key);
            return _values.TryGetValue(key, out var value) && value is T typedValue
                ? typedValue
                : defaultValue;
        }
    }

    void IPreferencesStore.Set<T>(string key, T value)
    {
        lock (_gate)
        {
            SetCallCount++;
            _values[key] = value;
        }
    }
}
