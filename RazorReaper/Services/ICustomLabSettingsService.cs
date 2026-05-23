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
}
