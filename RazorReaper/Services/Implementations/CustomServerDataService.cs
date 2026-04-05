using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

public class CustomServerDataService : ICustomServerDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly ILogger<CustomServerDataService> _logger;

    public CustomServerDataService(ILogger<CustomServerDataService> logger)
    {
        _logger = logger;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper");
        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "custom-servers.json");
    }

    public async Task<CustomServerStore> LoadAsync()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath);
                var store = JsonSerializer.Deserialize<CustomServerStore>(json, JsonOptions);
                if (store != null)
                    return store;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load custom server data from {Path}", _filePath);
        }

        return new CustomServerStore();
    }

    public async Task SaveAsync(CustomServerStore store)
    {
        try
        {
            var json = JsonSerializer.Serialize(store, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save custom server data to {Path}", _filePath);
        }
    }
}
