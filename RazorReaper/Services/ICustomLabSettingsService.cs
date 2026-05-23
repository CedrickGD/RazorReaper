using RazorReaper.Models;

namespace RazorReaper.Services;

public interface ICustomLabSettingsService
{
    CustomLabSettings Current { get; }
    event Action? Changed;

    Task LoadAsync();
    Task SaveAsync();
    Task SetAcceptedAsync(bool accepted);
    Task SetMasterEnabledAsync(bool enabled);
    Task SetMemoryInjectEnabledAsync(bool enabled);
    Task SetGuardArkProcessAsync(bool guard);
    Task ResetAcknowledgementAsync();

    /// <summary>Stamp <see cref="CustomLabSettings.LastSkyInjectAt"/> with the current time and persist.</summary>
    Task MarkSkyInjectedAsync();

    /// <summary>Stamp <see cref="CustomLabSettings.LastSkyRestoreAt"/> with the current time and persist.</summary>
    Task MarkSkyRestoredAsync();

    /// <summary>Clear both sky timestamps (used when backups are wiped, returning the state to "original").</summary>
    Task ClearSkyTimestampsAsync();
}
