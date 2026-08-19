namespace RazorReaper.Services;

public interface IPreferencesStore
{
    T Get<T>(string key, T defaultValue);

    void Set<T>(string key, T value);

    bool ContainsKey(string key);

    bool Remove(string key);
}
