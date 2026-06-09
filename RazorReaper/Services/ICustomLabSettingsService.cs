using RazorReaper.Models;

namespace RazorReaper.Services;

public interface ICustomLabSettingsService
{
    CustomLabSettings Current { get; }
    event Action? Changed;

    Task LoadAsync();

    /// <summary>Stamp <see cref="CustomLabSettings.LastSkyInjectAt"/> with the current time and persist.</summary>
    Task MarkSkyInjectedAsync();

    /// <summary>Stamp <see cref="CustomLabSettings.LastSkyRestoreAt"/> with the current time and persist.</summary>
    Task MarkSkyRestoredAsync();
}
