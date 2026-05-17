using System.Drawing;
using System.Runtime.InteropServices;
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
/// </summary>
public class CrosshairService : ICrosshairService, IDisposable
{
    private readonly ILogger<CrosshairService> _logger;
    private readonly INotificationService _notifications;

    private readonly string _rootDir;
    private readonly string _imagesDir;
    private readonly string _profilesPath;
    private readonly string _settingsPath;

    private readonly object _lock = new();
    private readonly List<CrosshairProfile> _saved = new();
    private CrosshairProfile _active;
    private bool _overlayActive;

    private readonly CrosshairOverlayWindow _overlay;

    // Preview-side image cache. Decoded once per ImagePath; reused across rapid preview re-renders
    // (animation timer ticks). Separate from the overlay's cache because both consumers may need to
    // own a frame at the same instant without cross-thread surprises.
    private readonly object _previewImageLock = new();
    private AnimatedImage? _previewImage;
    private string? _previewImagePath;
    private bool _previewLoadInFlight;
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

    public event Action? Changed;
    public event Action? ShowAppRequested;
    public event Action? QuitRequested;

    public bool IsOverlayActive => _overlayActive;
    public CrosshairProfile ActiveProfile { get { lock (_lock) return _active; } }

    public CrosshairService(ILogger<CrosshairService> logger, INotificationService notifications)
    {
        _logger = logger;
        _notifications = notifications;

        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "Crosshairs");
        _imagesDir = Path.Combine(_rootDir, "Images");
        _profilesPath = Path.Combine(_rootDir, "profiles.json");
        _settingsPath = Path.Combine(_rootDir, "settings.json");

        try
        {
            Directory.CreateDirectory(_imagesDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create crosshair storage dir at {Dir}", _rootDir);
        }

        _active = BuiltIns[0].Clone();
        _active.IsBuiltIn = false;
        _active.Name = "Custom";

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

    private void OnHotkeyToggle()
    {
        ToggleOverlay();
        try
        {
            if (_overlayActive)
                _notifications.ShowInfo("Crosshair overlay enabled.");
            else
                _notifications.ShowInfo("Crosshair overlay disabled.");
        }
        catch { /* notifications can fail in odd shutdown paths */ }
    }

    public IReadOnlyList<CrosshairProfile> GetBuiltInPresets()
    {
        // Return cloned copies — Clone() mints a fresh GUID, so re-pin the original ID so the
        // caller can still compare against the built-in's stable id, and re-set IsBuiltIn so
        // the page can recognise them. The static BuiltIns list itself is never exposed; this
        // prevents any caller from accidentally mutating a base preset.
        return BuiltIns.Select(p =>
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

    public byte[] RenderPreviewPng(double phase = 0.25)
    {
        CrosshairProfile snapshot;
        lock (_lock) snapshot = _active.Clone();

        EnsurePreviewImage(snapshot);

        lock (_previewImageLock)
        {
            var frame = _previewImage?.FrameAt(_previewStart);
            // PreviewMaxBound keeps the editor pane render cheap. Source images render at native
            // resolution into a small canvas via DrawImage — bicubic downscale is fast enough that
            // the per-tick cost stays comfortably under one frame's budget.
            var canvasSize = CrosshairRenderer.ComputeCanvasSize(snapshot, frame, PreviewMaxBound);
            if (_previewRenderBuffer == null || _previewRenderBufferSize != canvasSize)
            {
                _previewRenderBuffer?.Dispose();
                _previewRenderBuffer = new Bitmap(canvasSize, canvasSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                _previewRenderBufferSize = canvasSize;
            }
            CrosshairRenderer.RenderInto(_previewRenderBuffer, snapshot, phase, frame);

            using var ms = new MemoryStream();
            _previewRenderBuffer.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Make sure the preview-side AnimatedImage matches the active profile's ImagePath. Loading
    /// happens off the caller's thread — synchronously decoding a large GIF here would freeze the
    /// Blazor render loop. The render proceeds with whatever the cache currently has; once the
    /// background load lands, Changed fires and the page re-renders.
    /// </summary>
    private void EnsurePreviewImage(CrosshairProfile snapshot)
    {
        if (snapshot.Type != CrosshairType.Image
            || string.IsNullOrWhiteSpace(snapshot.ImagePath)
            || !File.Exists(snapshot.ImagePath))
        {
            lock (_previewImageLock)
            {
                if (_previewImage != null)
                {
                    _previewImage.Dispose();
                    _previewImage = null;
                    _previewImagePath = null;
                }
            }
            return;
        }

        string? loadPath = null;
        lock (_previewImageLock)
        {
            var matches = string.Equals(_previewImagePath, snapshot.ImagePath, StringComparison.OrdinalIgnoreCase);
            if (!matches && !_previewLoadInFlight)
            {
                _previewLoadInFlight = true;
                loadPath = snapshot.ImagePath;
            }
        }

        if (loadPath != null)
        {
            Task.Run(() =>
            {
                AnimatedImage? loaded = null;
                Exception? err = null;
                try { loaded = AnimatedImage.Load(loadPath); }
                catch (Exception ex) { err = ex; }

                lock (_previewImageLock)
                {
                    _previewImage?.Dispose();
                    _previewImage = loaded;
                    _previewImagePath = loaded != null ? loadPath : null;
                    _previewLoadInFlight = false;
                }

                if (err != null)
                {
                    _logger.LogWarning(err, "Preview image load failed for {Path}", loadPath);
                    try { _notifications.ShowError($"Couldn't load image: {err.Message}"); } catch { }
                }
                Changed?.Invoke();
            });
        }
    }

    public bool HasAnimatedActiveImage
    {
        get
        {
            lock (_previewImageLock)
            {
                return _previewImage?.IsAnimated == true;
            }
        }
    }

    public async Task<string?> ImportImageAsync(Stream source, string fileName)
    {
        try
        {
            var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
            if (!AllowedImageExt.Contains(ext))
            {
                _notifications.ShowError($"Unsupported image type: {ext}");
                return null;
            }
            Directory.CreateDirectory(_imagesDir);
            var dest = Path.Combine(_imagesDir, $"{Guid.NewGuid():N}{ext}");
            await using (var fs = File.Create(dest))
            {
                await source.CopyToAsync(fs);
            }
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image import failed for {File}", fileName);
            _notifications.ShowError($"Image import failed: {ex.Message}");
            return null;
        }
    }

    public async Task<CrosshairProfile?> ImportWorkshopAsync(string path)
    {
        try
        {
            string? imageCandidate = null;
            string? configCandidate = null;

            if (File.Exists(path))
            {
                var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (AllowedImageExt.Contains(ext)) imageCandidate = path;
                else if (ConfigExt.Contains(ext)) configCandidate = path;
                else
                {
                    // Some workshop bundles are .zip-ish — try as folder if it's a dir, else give up.
                    _notifications.ShowWarning($"Unrecognized workshop file type: {ext}");
                    return null;
                }
            }
            else if (Directory.Exists(path))
            {
                imageCandidate = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => AllowedImageExt.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()));
                configCandidate = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => ConfigExt.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()));
            }
            else
            {
                _notifications.ShowError("Workshop path doesn't exist.");
                return null;
            }

            // Start from the current active profile (so things like monitor/offset/hotkey carry over),
            // then layer image + parsed config on top.
            var profile = ActiveProfile.Clone();
            profile.Id = Guid.NewGuid().ToString("N");
            profile.IsBuiltIn = false;
            profile.Name = Path.GetFileNameWithoutExtension(imageCandidate ?? configCandidate ?? path);
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "Imported";

            if (imageCandidate != null)
            {
                await using var fs = File.OpenRead(imageCandidate);
                var stored = await ImportImageAsync(fs, Path.GetFileName(imageCandidate));
                if (stored != null)
                {
                    profile.Type = CrosshairType.Image;
                    profile.ImagePath = stored;
                    profile.ImageScale = ComputeDefaultImageScale(stored);
                }
            }

            if (configCandidate != null)
            {
                TryApplyConfig(configCandidate, profile);
            }

            if (imageCandidate == null && configCandidate == null)
            {
                _notifications.ShowError("No usable image or config found in workshop file.");
                return null;
            }

            return profile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workshop import failed for {Path}", path);
            _notifications.ShowError($"Workshop import failed: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<string> GetImportedImagePaths()
    {
        try
        {
            if (!Directory.Exists(_imagesDir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(_imagesDir)
                .Where(f => AllowedImageExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list imported images");
            return Array.Empty<string>();
        }
    }

    public byte[]? RenderThumbnailPng(string imagePath, int size = 72)
    {
        try
        {
            if (!File.Exists(imagePath)) return null;
            using var fs = File.OpenRead(imagePath);
            using var src = Image.FromStream(fs);
            // For animated images we just thumbnail the first frame — keeps the grid snappy and the
            // library card from ballooning the DOM with megabytes of base64 GIF data.
            using var thumb = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Aspect-preserving letterbox into the square thumb.
            var aspect = (double)src.Width / src.Height;
            int w, h;
            if (aspect >= 1) { w = size; h = (int)(size / aspect); }
            else { h = size; w = (int)(size * aspect); }
            var x = (size - w) / 2;
            var y = (size - h) / 2;
            g.DrawImage(src, x, y, w, h);

            using var ms = new MemoryStream();
            thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render thumbnail for {Path}", imagePath);
            return null;
        }
    }

    public void OpenImportsFolder()
    {
        // Kick off async work — the WinRT launcher and StorageFolder calls are async-only.
        // Fire-and-forget; the user clicks a button, the result is "did a window appear",
        // and any failure surfaces as a notification.
        _ = OpenImportsFolderInternalAsync();
    }

    private async Task OpenImportsFolderInternalAsync()
    {
        Directory.CreateDirectory(_imagesDir);

        // Canonicalise — handles a missing trailing slash, ./.. components, case quirks, etc.
        // The "Der Pfad ist nicht verfügbar" dialog we kept hitting was showing a malformed path
        // (literal space in the middle) which is a strong hint that something between us and
        // Explorer is munging the string. Path.GetFullPath gives us a known-clean canonical form.
        string path;
        try { path = Path.GetFullPath(_imagesDir); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFullPath failed for {Path}", _imagesDir);
            _notifications.ShowError("Couldn't resolve folder path.");
            return;
        }

        if (!Directory.Exists(path))
        {
            _notifications.ShowError($"Folder missing: {path}");
            return;
        }

        _logger.LogInformation("Open folder: {Path}", path);

        // Strategy 1 — WinRT Launcher. This is the modern, canonical API for "open this folder".
        // It does *not* go through our process; the shell launches Explorer on its end with the
        // right verb and arguments. Bypasses every command-line-parsing quirk Process.Start has.
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            if (folder != null)
            {
                var ok = await Windows.System.Launcher.LaunchFolderAsync(folder);
                _logger.LogInformation("LaunchFolderAsync → {Ok}", ok);
                if (ok) return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WinRT LaunchFolderAsync threw");
        }

        // Strategy 2 — Shell PIDL. Same call Explorer uses internally when you double-click a
        // folder in another Explorer window.
        if (TryOpenViaShellPidl(path)) return;

        // Strategy 3 — explorer.exe with the path as a single positional argument (canonicalised).
        if (TryProcess("explorer.exe", $"\"{path}\"")) return;

        // Last resort — copy path to clipboard so the user can paste it themselves.
        _notifications.ShowError("Couldn't open Explorer. Path copied to clipboard — paste it into the Explorer address bar.");
        await CopyImportsFolderPathAsync();
    }

    private bool TryOpenViaShellPidl(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            // SHParseDisplayName turns a path into a shell PIDL. If the path resolves, the rest
            // bypasses any command-line dispatch quirks.
            var hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                _logger.LogWarning("SHParseDisplayName returned 0x{Hr:X} for {Path}", hr, path);
                return false;
            }
            var openHr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
            if (openHr != 0)
            {
                _logger.LogWarning("SHOpenFolderAndSelectItems returned 0x{Hr:X}", openHr);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shell PIDL open threw");
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    private bool TryProcess(string file, string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
            });
            return p != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process.Start({File} {Args}) threw", file, args);
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    public string ImportsFolderPath => _imagesDir;

    public async Task<bool> CopyImportsFolderPathAsync()
    {
        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(_imagesDir);
            _notifications.ShowSuccess($"Copied: {_imagesDir}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed");
            _notifications.ShowError($"Couldn't copy path: {ex.Message}");
            return false;
        }
    }

    private const int SW_SHOWNORMAL = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        string lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    public bool DeleteImportedImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            // Clear preview cache if it points at this file — otherwise we'd hold a file lock
            // (and the file would be re-rendered with a stale image hanging in memory).
            lock (_previewImageLock)
            {
                if (string.Equals(_previewImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _previewImage?.Dispose();
                    _previewImage = null;
                    _previewImagePath = null;
                }
            }

            File.Delete(path);

            // If the active profile was using this image, drop it so the overlay stops trying to draw a
            // deleted file. We don't switch type — user may want to import a replacement right after.
            bool wasActive = false;
            CrosshairProfile snapshot;
            lock (_lock)
            {
                if (string.Equals(_active.ImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _active.ImagePath = null;
                    wasActive = true;
                }
                snapshot = _active;
            }
            if (wasActive)
            {
                _overlay.Update(snapshot, _overlayActive);
                Changed?.Invoke();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete imported image {Path}", path);
            _notifications.ShowError($"Delete failed: {ex.Message}");
            return false;
        }
    }

    public void UseImportedImage(string path)
    {
        if (!File.Exists(path))
        {
            _notifications.ShowError("That image is no longer on disk.");
            return;
        }
        CrosshairProfile snapshot;
        lock (_lock)
        {
            _active.Type = CrosshairType.Image;
            _active.ImagePath = path;
            // Right-size on each library/picker selection — a 1080p source at 100% scale would render
            // multiple-monitors wide. If the user wants it bigger they bump the Scale slider.
            _active.ImageScale = ComputeDefaultImageScale(path);
            snapshot = _active;
        }
        if (_overlayActive) _overlay.Show(snapshot);
        Changed?.Invoke();
    }

    /// <summary>Pick an ImageScale that renders ~128px on the longest side for fresh selections.
    /// Small source images (≤128px) stay at 100%. Cropped/cleaned-up tiny crosshair PNGs render
    /// untouched; oversized photos shrink to a sensible default the user can scale up from.</summary>
    private int ComputeDefaultImageScale(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var src = Image.FromStream(fs);
            var maxDim = Math.Max(src.Width, src.Height);
            if (maxDim <= 128) return 100;
            return Math.Clamp((int)Math.Round(128.0 / maxDim * 100.0), 5, 100);
        }
        catch
        {
            return 100;
        }
    }

    public CrosshairProfile? ImportFromCode(string code)
    {
        try
        {
            var parsed = CrosshairCodeParsers.TryParse(code, ActiveProfile);
            if (parsed == null)
            {
                _notifications.ShowError("Couldn't recognise that code (Valorant or CSGO format expected).");
                return null;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Code import failed");
            _notifications.ShowError($"Code import failed: {ex.Message}");
            return null;
        }
    }

    private void TryApplyConfig(string configPath, CrosshairProfile profile)
    {
        try
        {
            var text = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            // Try JSON first; fall back to simple key=value/INI parsing.
            Dictionary<string, string>? kv = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                kv = FlattenJson(doc.RootElement);
            }
            catch { /* not JSON — fine */ }

            kv ??= ParseKeyValue(text);

            if (kv.TryGetValue("color", out var color)) profile.Color = NormalizeColor(color) ?? profile.Color;
            if (kv.TryGetValue("outlinecolor", out var oc)) profile.OutlineColor = NormalizeColor(oc) ?? profile.OutlineColor;
            if (kv.TryGetValue("outline", out var ot) && int.TryParse(ot, out var oti)) profile.OutlineThickness = Math.Clamp(oti, 0, 6);
            if (kv.TryGetValue("size", out var sz) && int.TryParse(sz, out var szi)) profile.Size = Math.Clamp(szi, 1, 200);
            if (kv.TryGetValue("length", out var ln) && int.TryParse(ln, out var lni)) profile.Size = Math.Clamp(lni, 1, 200);
            if (kv.TryGetValue("thickness", out var th) && int.TryParse(th, out var thi)) profile.Thickness = Math.Clamp(thi, 1, 20);
            if (kv.TryGetValue("gap", out var gp) && int.TryParse(gp, out var gpi)) profile.Gap = Math.Clamp(gpi, 0, 100);
            if (kv.TryGetValue("opacity", out var op) && int.TryParse(op, out var opi)) profile.Opacity = Math.Clamp(opi, 0, 100);
            if (kv.TryGetValue("rotation", out var rt) && int.TryParse(rt, out var rti)) profile.Rotation = ((rti % 360) + 360) % 360;
            if (kv.TryGetValue("dot", out var dt) && bool.TryParse(dt, out var dtb)) profile.ShowDot = dtb;
            if (kv.TryGetValue("centerdot", out var cd) && bool.TryParse(cd, out var cdb)) profile.ShowDot = cdb;
            if (kv.TryGetValue("dotsize", out var ds) && int.TryParse(ds, out var dsi)) profile.DotSize = Math.Clamp(dsi, 1, 50);
            if (kv.TryGetValue("type", out var ty)) profile.Type = ParseType(ty) ?? profile.Type;
            if (kv.TryGetValue("style", out var st)) profile.Type = ParseType(st) ?? profile.Type;
            if (kv.TryGetValue("name", out var nm) && !string.IsNullOrWhiteSpace(nm)) profile.Name = nm.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workshop config parse failed for {Path}", configPath);
        }
    }

    private static Dictionary<string, string> FlattenJson(JsonElement el)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(el);
        return dict;

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(prop.Value);
                    else
                        dict[prop.Name] = prop.Value.ToString() ?? "";
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Walk(item);
            }
        }
    }

    private static Dictionary<string, string> ParseKeyValue(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
            var eq = line.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            dict[key] = value;
        }
        return dict;
    }

    private static string? NormalizeColor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("#")) return raw;
        if (raw.Length is 6 or 8 && raw.All(c => Uri.IsHexDigit(c))) return "#" + raw;
        // rgb(r,g,b)
        if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var inner = raw[(raw.IndexOf('(') + 1)..raw.LastIndexOf(')')];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        return null;
    }

    private static CrosshairType? ParseType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "cross" or "classic" or "default" or "plus" => CrosshairType.Cross,
            "dot" or "point" => CrosshairType.Dot,
            "circle" or "ring" or "o" => CrosshairType.Circle,
            "t" or "tstyle" or "t-style" => CrosshairType.TStyle,
            "image" or "custom" or "png" => CrosshairType.Image,
            _ => null
        };
    }

    public void SetHotkey(string displayLabel, int virtualKey, bool ctrl, bool alt, bool shift)
    {
        _hotkeyLabel = displayLabel;
        _hotkeyVk = virtualKey;
        _hotkeyCtrl = ctrl;
        _hotkeyAlt = alt;
        _hotkeyShift = shift;

        _overlay.UnregisterHotkey();
        if (virtualKey > 0)
            _overlay.RegisterHotkey(virtualKey, ctrl, alt, shift);

        _ = PersistSettingsAsync();
        Changed?.Invoke();
    }

    public (string Label, int VirtualKey, bool Ctrl, bool Alt, bool Shift) GetHotkey()
        => (_hotkeyLabel, _hotkeyVk, _hotkeyCtrl, _hotkeyAlt, _hotkeyShift);

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

    public void Dispose()
    {
        try { _overlay.Dispose(); } catch { /* swallow on shutdown */ }
        lock (_previewImageLock)
        {
            _previewImage?.Dispose();
            _previewImage = null;
            _previewRenderBuffer?.Dispose();
            _previewRenderBuffer = null;
        }
    }

    private static readonly HashSet<string> AllowedImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private static readonly HashSet<string> ConfigExt = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".ini", ".cfg", ".txt", ".crosshair" };

    // ─── Built-in presets ─────────────────────────────────────────────────────────

    private static readonly List<CrosshairProfile> BuiltIns = new()
    {
        new CrosshairProfile
        {
            Id = "builtin-valorant",
            Name = "Valorant",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#00FF66",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 6,
            Thickness = 2,
            Gap = 3,
            Opacity = 100,
            ShowDot = false,
        },
        new CrosshairProfile
        {
            Id = "builtin-cs",
            Name = "CS Classic",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#00FFFF",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 1,
            Gap = 4,
            Opacity = 100,
            ShowDot = false,
        },
        new CrosshairProfile
        {
            Id = "builtin-sniper",
            Name = "Sniper",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#FF1744",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 40,
            Thickness = 1,
            Gap = 6,
            Opacity = 100,
            ShowDot = true,
            DotSize = 1,
        },
        new CrosshairProfile
        {
            Id = "builtin-dot",
            Name = "Tactical Dot",
            IsBuiltIn = true,
            Type = CrosshairType.Dot,
            Color = "#FFEE00",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            DotSize = 3,
            Opacity = 100,
        },
        new CrosshairProfile
        {
            Id = "builtin-circle",
            Name = "Ring",
            IsBuiltIn = true,
            Type = CrosshairType.Circle,
            Color = "#8b5cf6",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 2,
            Opacity = 90,
            ShowDot = true,
            DotSize = 2,
        },
        new CrosshairProfile
        {
            Id = "builtin-t",
            Name = "T-Style",
            IsBuiltIn = true,
            Type = CrosshairType.TStyle,
            Color = "#FFFFFF",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 8,
            Thickness = 2,
            Gap = 4,
            Opacity = 100,
        },
        new CrosshairProfile
        {
            Id = "builtin-rainbow",
            Name = "Rainbow Pulse",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#FF0000",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 3,
            Gap = 5,
            Opacity = 100,
            ShowDot = true,
            DotSize = 2,
            Animation = CrosshairAnimation.Pulse,
            AnimationSpeed = 6,
            Rainbow = true,
        },
        new CrosshairProfile
        {
            Id = "builtin-breath",
            Name = "Breathing Ring",
            IsBuiltIn = true,
            Type = CrosshairType.Circle,
            Color = "#a78bfa",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 14,
            Thickness = 2,
            Opacity = 100,
            Animation = CrosshairAnimation.Breath,
            AnimationSpeed = 3,
        },
    };
}
