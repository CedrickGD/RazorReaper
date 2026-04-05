using RazorReaper.Models;

namespace RazorReaper.Services;

public interface ICustomServerDataService
{
    Task<CustomServerStore> LoadAsync();
    Task SaveAsync(CustomServerStore store);
}
