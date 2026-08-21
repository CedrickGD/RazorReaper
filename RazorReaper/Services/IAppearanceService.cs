namespace RazorReaper.Services;

/// <summary>
/// Persisted look-and-feel: interface scale.
///
/// Accent colour and interface font are deliberately NOT here — both already have
/// richer cards on the Home page, and two owners for one value is how you get them
/// disagreeing. This holds the value; a Blazor component pushes it into the document
/// (see theme.js) because CSS variables need a live circuit to reach.
/// </summary>
public interface IAppearanceService
{
    /// <summary>UI scale as a percentage (100 = default).</summary>
    int UiScalePercent { get; }

    bool IsDefault { get; }

    event Action? Changed;

    void SetUiScalePercent(int percent);
    void ResetToDefaults();
}

