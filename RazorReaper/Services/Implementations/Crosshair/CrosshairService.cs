using System.Drawing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
// Avoid Microsoft.Maui.* implicit usings colliding with System.Drawing types.
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using Graphics = System.Drawing.Graphics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Singleton owner of the crosshair feature: built-in presets, user profiles (JSON in LocalAppData),
/// imported images, hotkey config, and the lifetime of the Win32 overlay window. The Crosshair page
/// is a thin view over this — it never owns state directly.
///
/// Implementation is split across partial files so each concern stays readable:
///  • <c>CrosshairService.cs</c> — fields, ctor, profile CRUD, persistence, Dispose (you are here).
///  • <c>CrosshairService.Preview.cs</c> — editor-side preview rendering and image-load lifecycle.
///  • <c>CrosshairService.Library.cs</c> — imports (image / video / workshop / code) and the library cache.
///  • <c>CrosshairService.Hotkey.cs</c> — global hotkey wiring and the hotkey-toggle event handler.
/// </summary>
public partial class CrosshairService : ICrosshairService, IDisposable
{
    private readonly ILogger<CrosshairService> _logger;
    private readonly INotificationService _notifications;
    private readonly VideoFrameExtractor _videoExtractor;

    private readonly string _rootDir;
    private readonly string _imagesDir;
    private readonly string _profilesPath;
    private readonly string _settingsPath;
    private readonly string _activeProfilePath;

    private readonly object _lock = new();
    private readonly List<CrosshairProfile> _saved = new();
    private CrosshairProfile _active;
    private bool _overlayActive;

    // Session-level library cache. Populated once at startup (one disk enumeration) and
    // mutated explicitly on Import/Delete — never re-scanned just because the active
    // profile changed. Slider tweaks must not touch disk.
    private readonly object _libraryLock = new();
    private List<string> _libraryCache = new();

    private readonly CrosshairOverlayWindow _overlay;

    // Preview-side image cache. Decoded once per ImagePath; reused across rapid preview re-renders
    // (animation timer ticks). Separate from the overlay's cache because both consumers may need to
    // own a frame at the same instant without cross-thread surprises.
    private readonly object _previewImageLock = new();
    private AnimatedImage? _previewImage;
    private string? _previewImagePath;
    private bool _previewLoadInFlight;
    // Paths that already failed to decode (e.g., a file with .png extension but WEBP bytes).
    // Without this set, EnsurePreviewImage would re-fire Changed → page re-renders → fires
    // EnsurePreviewImage again → re-fails in a tight loop that locks the UI thread.
    private readonly HashSet<string> _previewLoadFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _previewStart = DateTime.UtcNow;
    // Cap the preview canvas tight. The editor pane is ~260px on screen — rendering at 1024+ just
    // to scale down in the browser makes the per-tick PNG-encode + base64 work an order of
    // magnitude heavier than necessary.
    private const int PreviewMaxBound = 384;
    // Pooled render canvas — same rationale as in CrosshairOverlayWindow. Without this, the
    // page's ~25 Hz preview tick allocates a fresh LOH bitmap each frame and tanks performance
    // once the user starts cranking the Scale slider.
    private Bitmap? _previewRenderBuffer;
    private int _previewRenderBufferSize;

    // Hotkey state — persisted to settings.json alongside profiles.
    private string _hotkeyLabel = "F8";
    private int _hotkeyVk = 0x77; // F8
    private bool _hotkeyCtrl, _hotkeyAlt, _hotkeyShift;

    // Debounce gate for active-profile persistence. Slider drags fire UpdateActive at ~25 Hz;
    // writing JSON on every tick would hammer the disk. We coalesce bursts into a single
    // delayed write, then Dispose flushes synchronously so the last edits aren't lost on exit.
    private static readonly TimeSpan ActiveProfileSaveDebounce = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _activeProfileSaveCts;

    public event Action? Changed;
    public event Action? LibraryChanged;
    public event Action? ShowAppRequested;
    public event Action? QuitRequested;

    public bool IsOverlayActive => _overlayActive;
    public CrosshairProfile ActiveProfile { get { lock (_lock) return _active; } }

    public CrosshairService(ILogger<CrosshairService> logger, INotificationService notifications)
    {
        _logger = logger;
        _notifications = notifications;
        _videoExtractor = new VideoFrameExtractor(logger);

        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "Crosshairs");
        _imagesDir = Path.Combine(_rootDir, "Images");
        _profilesPath = Path.Combine(_rootDir, "profiles.json");
        _settingsPath = Path.Combine(_rootDir, "settings.json");
        _activeProfilePath = Path.Combine(_rootDir, "active.json");

        try
        {
            Directory.CreateDirectory(_imagesDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create crosshair storage dir at {Dir}", _rootDir);
        }

        // One eager disk scan at startup. From here on the library list lives in memory and
        // is only mutated by Import/Delete — no re-enumeration on every Changed event.
        RebuildLibraryCache();

        // Restore the last session's working profile. If active.json is missing or unreadable,
        // fall back to the first built-in preset so the editor still opens with something usable.
        _active = LoadActiveProfile();

        LoadSaved();
        LoadSettings();

        _overlay = new CrosshairOverlayWindow(
            logger,
            onHotkeyToggle: OnHotkeyToggle,
            onTrayShowApp: () => ShowAppRequested?.Invoke(),
            onTrayQuit: () => QuitRequested?.Invoke(),
            isOverlayActive: () => _overlayActive);
        _overlay.Start();
        _overlay.RegisterHotkey(_hotkeyVk, _hotkeyCtrl, _hotkeyAlt, _hotkeyShift);
    }

    public IReadOnlyList<CrosshairProfile> GetBuiltInPresets()
    {
        // Return cloned copies — Clone() mints a fresh GUID, so re-pin the original ID so the
        // caller can still compare against the built-in's stable id, and re-set IsBuiltIn so
        // the page can recognise them. The static catalog itself is never exposed; this
        // prevents any caller from accidentally mutating a base preset.
        return CrosshairBuiltInPresets.All.Select(p =>
        {
            var c = p.Clone();
            c.Id = p.Id;
            c.IsBuiltIn = true;
            return c;
        }).ToList();
    }

    public IReadOnlyList<CrosshairProfile> GetSavedProfiles()
    {
        lock (_lock)
        {
            // Clone() generates a NEW id — but the delete code matches by id, so we must
            // re-pin each clone's id to its source. Without this, "Delete" silently does
            // nothing because the id you click never matches anything in _saved.
            return _saved.Select(p =>
            {
                var c = p.Clone();
                c.Id = p.Id;
                return c;
            }).ToList();
        }
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => _overlay.GetMonitors();

    public void UpdateActive(CrosshairProfile profile)
    {
        // Snapshot — the caller (the page) holds a long-lived reference to its slider-bound
        // profile object. If we stored that reference directly, any field tweak from the page
        // would mutate _active right under any in-flight render path (and worse, if the caller
        // ever handed us a built-in preset reference, we'd be writing to the static list).
        var snapshot = profile.Clone();
        snapshot.Id = profile.Id;
        snapshot.IsBuiltIn = false;
        lock (_lock) { _active = snapshot; }
        if (_overlayActive) _overlay.Show(snapshot);
        ScheduleActiveProfileSave();
        Changed?.Invoke();
    }

    public void LoadProfile(CrosshairProfile profile)
    {
        // We always make a *copy* — built-in presets are immutable, and saved profiles shouldn't
        // mutate just because the user is tweaking sliders on the editor page.
        var copy = profile.Clone();
        copy.IsBuiltIn = false;
        lock (_lock) { _active = copy; }
        if (_overlayActive) _overlay.Show(copy);
        ScheduleActiveProfileSave();
        Changed?.Invoke();
    }

    public void StartOverlay()
    {
        CrosshairProfile snapshot;
        lock (_lock) { snapshot = _active; _overlayActive = true; }
        _overlay.Show(snapshot);
        Changed?.Invoke();
    }

    public void StopOverlay()
    {
        lock (_lock) { _overlayActive = false; }
        _overlay.Hide();
        Changed?.Invoke();
    }

    public void ToggleOverlay()
    {
        if (_overlayActive) StopOverlay();
        else StartOverlay();
    }

    public async Task<bool> SaveAsAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        CrosshairProfile copy;
        lock (_lock)
        {
            copy = _active.Clone();
            copy.Name = name.Trim();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.IsBuiltIn = false;
            // De-dupe by name — overwrite an existing saved profile if names collide
            _saved.RemoveAll(p => string.Equals(p.Name, copy.Name, StringComparison.OrdinalIgnoreCase));
            _saved.Add(copy);
        }
        var ok = await PersistSavedAsync();
        if (ok) Changed?.Invoke();
        return ok;
    }

    public async Task<bool> DeleteSavedAsync(string id)
    {
        lock (_lock)
        {
            var removed = _saved.RemoveAll(p => p.Id == id);
            if (removed == 0) return false;
        }
        var ok = await PersistSavedAsync();
        if (ok) Changed?.Invoke();
        return ok;
    }

    // ─── Persistence ──────────────────────────────────────────────────────────────

    private void LoadSaved()
    {
        try
        {
            if (!File.Exists(_profilesPath)) return;
            var json = File.ReadAllText(_profilesPath);
            var list = JsonSerializer.Deserialize<List<CrosshairProfile>>(json);
            if (list != null)
            {
                lock (_lock)
                {
                    _saved.Clear();
                    foreach (var p in list) _saved.Add(p);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load crosshair profiles");
        }
    }

    private async Task<bool> PersistSavedAsync()
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            List<CrosshairProfile> snapshot;
            lock (_lock) snapshot = _saved.Select(p => p.Clone()).ToList();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_profilesPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist crosshair profiles");
            _notifications.ShowError($"Saving profile failed: {ex.Message}");
            return false;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("hotkeyLabel", out var l) && l.ValueKind == JsonValueKind.String) _hotkeyLabel = l.GetString() ?? _hotkeyLabel;
            if (root.TryGetProperty("hotkeyVk", out var vk) && vk.TryGetInt32(out var vki)) _hotkeyVk = vki;
            if (root.TryGetProperty("hotkeyCtrl", out var c)) _hotkeyCtrl = c.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("hotkeyAlt", out var a)) _hotkeyAlt = a.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("hotkeyShift", out var s)) _hotkeyShift = s.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load crosshair settings");
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            var payload = new
            {
                hotkeyLabel = _hotkeyLabel,
                hotkeyVk = _hotkeyVk,
                hotkeyCtrl = _hotkeyCtrl,
                hotkeyAlt = _hotkeyAlt,
                hotkeyShift = _hotkeyShift
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist crosshair settings");
        }
    }

    private CrosshairProfile LoadActiveProfile()
    {
        try
        {
            if (File.Exists(_activeProfilePath))
            {
                var json = File.ReadAllText(_activeProfilePath);
                var restored = JsonSerializer.Deserialize<CrosshairProfile>(json);
                if (restored is not null)
                {
                    // Built-in flag never persists — the restored profile is always treated as
                    // the user's editable working copy, even if it originated from a preset.
                    restored.IsBuiltIn = false;
                    if (string.IsNullOrWhiteSpace(restored.Id)) restored.Id = Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(restored.Name)) restored.Name = "Custom";
                    return restored;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load active crosshair profile; falling back to default");
        }

        var fallback = CrosshairBuiltInPresets.All[0].Clone();
        fallback.IsBuiltIn = false;
        fallback.Name = "Custom";
        return fallback;
    }

    /// <summary>Debounced background write of the active profile. Each call cancels any pending
    /// save and queues a fresh one — so a 1-second slider drag produces a single disk write
    /// after the drag settles, not 25.</summary>
    private void ScheduleActiveProfileSave()
    {
        var previous = Interlocked.Exchange(ref _activeProfileSaveCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var current = _activeProfileSaveCts;
        if (current is null) return;
        var token = current.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ActiveProfileSaveDebounce, token).ConfigureAwait(false);
                await PersistActiveProfileAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer schedule — the latest call will perform the write.
            }
        });
    }

    private async Task PersistActiveProfileAsync(CancellationToken cancellationToken)
    {
        CrosshairProfile snapshot;
        lock (_lock)
        {
            snapshot = _active.Clone();
            snapshot.Id = _active.Id;
        }

        try
        {
            Directory.CreateDirectory(_rootDir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_activeProfilePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation arrived between Delay completing and the write; next schedule will retry.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist active crosshair profile");
        }
    }

    /// <summary>Synchronous flush — used from Dispose so the last edits land before the process
    /// exits, even if a debounced save was still pending.</summary>
    private void PersistActiveProfileSync()
    {
        CrosshairProfile snapshot;
        lock (_lock)
        {
            snapshot = _active.Clone();
            snapshot.Id = _active.Id;
        }

        try
        {
            Directory.CreateDirectory(_rootDir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_activeProfilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush active crosshair profile on shutdown");
        }
    }

    public void Dispose()
    {
        // Cancel any pending debounced write and flush the final state synchronously, otherwise
        // the user's last slider tweak before closing the app is lost.
        try
        {
            var pending = Interlocked.Exchange(ref _activeProfileSaveCts, null);
            pending?.Cancel();
            pending?.Dispose();
            PersistActiveProfileSync();
        }
        catch { /* swallow on shutdown */ }

        try { _overlay.Dispose(); } catch { /* swallow on shutdown */ }
        lock (_previewImageLock)
        {
            _previewImage?.Dispose();
            _previewImage = null;
            _previewRenderBuffer?.Dispose();
            _previewRenderBuffer = null;
        }
    }
}
