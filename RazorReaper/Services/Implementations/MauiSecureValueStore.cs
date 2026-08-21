using Microsoft.Maui.Storage;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

public sealed class MauiSecureValueStore : ISecureValueStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public bool Remove(string key) => SecureStorage.Default.Remove(key);
}
