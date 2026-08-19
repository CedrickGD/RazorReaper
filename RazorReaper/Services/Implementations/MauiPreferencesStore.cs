using Microsoft.Maui.Storage;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

public sealed class MauiPreferencesStore : IPreferencesStore
{
    public T Get<T>(string key, T defaultValue) => Preferences.Default.Get(key, defaultValue);

    public void Set<T>(string key, T value) => Preferences.Default.Set(key, value);

    public bool ContainsKey(string key) => Preferences.Default.ContainsKey(key);

    public bool Remove(string key)
    {
        var existed = Preferences.Default.ContainsKey(key);
        Preferences.Default.Remove(key);
        return existed;
    }
}
