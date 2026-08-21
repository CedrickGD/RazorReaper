using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

public sealed class AppearanceService : IAppearanceService
{
    private const string PrefScale = "rr.appearance.uiscale";

    public const int DefaultScalePercent = 100;
    public const int MinScalePercent = 85;
    public const int MaxScalePercent = 130;

    private readonly ILogger<AppearanceService> logger;

    public AppearanceService(ILogger<AppearanceService> logger)
    {
        this.logger = logger;
    }

    public event Action? Changed;

    public int UiScalePercent => Math.Clamp(
        Preferences.Get(PrefScale, DefaultScalePercent), MinScalePercent, MaxScalePercent);

    public bool IsDefault => UiScalePercent == DefaultScalePercent;

    public void SetUiScalePercent(int percent)
    {
        Preferences.Set(PrefScale, Math.Clamp(percent, MinScalePercent, MaxScalePercent));
        Changed?.Invoke();
    }

    public void ResetToDefaults()
    {
        Preferences.Remove(PrefScale);
        Changed?.Invoke();
    }

}
