namespace RazorReaper.Services;

/// <summary>
/// Small secret store for values that must not live in plain preferences (the install's private
/// signing key). Backed by MAUI SecureStorage (DPAPI on Windows) in the app; tests use an
/// in-memory fake.
/// </summary>
public interface ISecureValueStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    bool Remove(string key);
}
