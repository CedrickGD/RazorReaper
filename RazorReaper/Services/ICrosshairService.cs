using RazorReaper.Models;

namespace RazorReaper.Services;

public interface ICrosshairService
{
    /// <summary>Fired any time the active profile or visibility changes (from any source — UI, hotkey, etc).</summary>
    event Action? Changed;

    /// <summary>Fired when the tray icon asks to bring the main app window back into view.</summary>
    event Action? ShowAppRequested;

    /// <summary>Fired when the tray icon asks to terminate the process entirely.</summary>
    event Action? QuitRequested;

    bool IsOverlayActive { get; }
    CrosshairProfile ActiveProfile { get; }

    /// <summary>True when the currently-loaded image has more than one frame (animated GIF, etc).</summary>
    bool HasAnimatedActiveImage { get; }

    IReadOnlyList<CrosshairProfile> GetBuiltInPresets();
    IReadOnlyList<CrosshairProfile> GetSavedProfiles();
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Replace the active profile's data and push to the overlay if active.</summary>
    void UpdateActive(CrosshairProfile profile);

    /// <summary>Apply a preset/saved profile as the new active profile (creates a fresh editable copy).</summary>
    void LoadProfile(CrosshairProfile profile);

    void StartOverlay();
    void StopOverlay();
    void ToggleOverlay();

    /// <summary>Persist the current active profile as a new saved profile under the given name.</summary>
    Task<bool> SaveAsAsync(string name);
    Task<bool> DeleteSavedAsync(string id);

    /// <summary>
    /// Render the active profile to a PNG byte array for the editor preview. <paramref name="phase"/>
    /// drives the animation/rainbow cycle (0..1). When the page passes a wall-clock-derived phase,
    /// the preview mirrors what's on screen.
    /// </summary>
    byte[] RenderPreviewPng(double phase = 0.25);

    /// <summary>
    /// Copies the source image into the local crosshair-image store and returns the new persistent path
    /// (so the cached path survives even if the user moves/deletes the original file).
    /// </summary>
    Task<string?> ImportImageAsync(Stream source, string fileName);

    /// <summary>All images currently in the local crosshair-image store, newest first.</summary>
    IReadOnlyList<string> GetImportedImagePaths();

    /// <summary>Render a square thumbnail (letterboxed) of an imported image for the library grid.</summary>
    byte[]? RenderThumbnailPng(string imagePath, int size = 72);

    /// <summary>Open the imported-images folder in Explorer.</summary>
    void OpenImportsFolder();

    /// <summary>Copy the imported-images folder path to the system clipboard.</summary>
    Task<bool> CopyImportsFolderPathAsync();

    /// <summary>Absolute path to the imported-images folder (so the UI can display it for the
    /// user to copy/paste when the Explorer launch is being uncooperative).</summary>
    string ImportsFolderPath { get; }

    /// <summary>Delete an imported image from disk. If it was the active image, clears the profile.</summary>
    bool DeleteImportedImage(string path);

    /// <summary>Set the active crosshair to use a specific imported image path (must already exist).</summary>
    void UseImportedImage(string path);

    /// <summary>
    /// Best-effort Crosshair X workshop import. Accepts a file or folder path. Returns a profile if at
    /// least an image was found; null if nothing usable.
    /// </summary>
    Task<CrosshairProfile?> ImportWorkshopAsync(string path);

    /// <summary>
    /// Best-effort parser for community crosshair codes — currently Valorant strings and CSGO/CS2
    /// share codes. Returns null if the code isn't recognised.
    /// </summary>
    CrosshairProfile? ImportFromCode(string code);

    void SetHotkey(string displayLabel, int virtualKey, bool ctrl, bool alt, bool shift);
    (string Label, int VirtualKey, bool Ctrl, bool Alt, bool Shift) GetHotkey();
}
