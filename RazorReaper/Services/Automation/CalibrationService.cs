using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Services.Localization;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>A named, resolution-tagged screen point captured from the live cursor position.</summary>
public sealed record CalibrationPoint(string Name, int X, int Y, string Resolution);

/// <summary>
/// A named, resolution-tagged screen rectangle captured from two corner points.
///
/// <paramref name="MonitorDeviceName"/> and <paramref name="MonitorResolution"/> record the
/// display ARK was on when the region — and the reference snapshot taken over it — were captured.
/// Both are null on an entry written before they existed, which is read as "we do not know"
/// rather than as a mismatch: an old calibration that still works must keep working.
/// </summary>
public sealed record CalibrationRegion(
    string Name,
    int Left,
    int Top,
    int Right,
    int Bottom,
    string Resolution,
    string? MonitorDeviceName = null,
    string? MonitorResolution = null)
{
    /// <summary>The region as a <see cref="Rectangle"/>.</summary>
    public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
}

/// <summary>
/// The display a reference was captured on, next to the one the game is on now. Numbers and keys
/// only, no sentence: the Scripts page words it where the row renders, so a language switch
/// re-words it — the same reason a script publishes <c>RegionTitleKey</c> rather than a title.
/// </summary>
/// <param name="StoredIndex">Monitor number at capture time (1-based), or 0 when unknown.</param>
/// <param name="StoredResolution">That monitor's resolution then, e.g. "2560x1440".</param>
/// <param name="CurrentIndex">Monitor number the game is on now, or 0 when unknown.</param>
/// <param name="CurrentResolution">That monitor's resolution now.</param>
public sealed record CalibrationMonitorInfo(
    int StoredIndex,
    string StoredResolution,
    int CurrentIndex,
    string CurrentResolution)
{
    /// <summary>
    /// True when the reference describes a different screen from the one being looked at. Pixels
    /// captured on a 2560x1440 second monitor compared against a 1920x1080 primary are not a low
    /// score, they are noise — and a threshold slider cannot fix noise.
    /// </summary>
    public bool Mismatch =>
        StoredIndex != CurrentIndex
        || !string.Equals(StoredResolution, CurrentResolution, StringComparison.Ordinal);
}

/// <summary>
/// Size rules for a calibrated region. A region is matched pixel-by-pixel against a reference
/// snapshot, so a degenerate one carries no signal: two corners read at the same spot produce a
/// 0x0 rectangle that would "match" anything. Validation lives here as a pure function so it can
/// be tested without a cursor, a screen or a settings file.
/// </summary>
public static class CalibrationRegionRules
{
    /// <summary>Smallest usable width and height, in physical pixels.</summary>
    public const int MinimumSidePx = 4;

    /// <summary>True when the rectangle is large enough on both axes to be worth matching.</summary>
    public static bool IsUsableSize(int left, int top, int right, int bottom)
        => (right - left) >= MinimumSidePx && (bottom - top) >= MinimumSidePx;
}

/// <summary>Progress signal during region capture: which corner (1 or 2) and seconds remaining.</summary>
public sealed record RegionCaptureProgress(int CornerIndex, int SecondsRemaining);

/// <summary>
/// Countdown-based screen calibration. The user hovers the target while a countdown runs; when it
/// hits zero the cursor position is read (<c>GetCursorPos</c>, physical pixels) and stored per
/// primary-screen resolution, so points survive restarts but never fire at the wrong coordinates
/// after a resolution change. Persisted to %LOCALAPPDATA%\RazorReaper\automation-calibration.json.
/// </summary>
public interface ICalibrationService
{
    /// <summary>Resolution key ("WxH", e.g. "2560x1440") lookups are scoped to right now.</summary>
    string CurrentResolutionKey { get; }

    /// <summary>
    /// The display ARK is on right now, or the primary one when it is not running. Null only when
    /// Windows reports no displays at all.
    /// </summary>
    AttachedDisplay? CurrentGameMonitor { get; }

    /// <summary>
    /// The whole stored entry for the current resolution — the rectangle plus which display it was
    /// captured on — or null when there is none. <see cref="TryGetRegion"/> answers the rectangle
    /// question; this one answers "and is it still describing the screen we are looking at?".
    /// </summary>
    CalibrationRegion? GetRegion(string name);

    /// <summary>
    /// Records the display ARK is on right now against a stored region. Called when the region is
    /// calibrated and again when a reference snapshot is taken over it, because the snapshot is
    /// the thing later compared and it belongs to the screen it was taken from.
    /// </summary>
    void StampRegionMonitor(string name);

    /// <summary>True while a point or region capture countdown is in progress.</summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Counts down, then captures the cursor position as a named point for the current resolution.
    /// Returns null when cancelled or when another capture is already running.
    /// </summary>
    /// <param name="countdown">Receives the remaining seconds each tick (last report is 0).</param>
    Task<CalibrationPoint?> CapturePointAsync(string name, int countdownSeconds = 3, IProgress<int>? countdown = null, CancellationToken ct = default);

    /// <summary>
    /// Two-point corner capture: counts down and reads the cursor once for each opposite corner,
    /// then stores the normalized rectangle. Returns null when cancelled or already capturing.
    /// </summary>
    Task<CalibrationRegion?> CaptureRegionAsync(string name, int countdownSeconds = 3, IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default);

    /// <summary>True when a point with this name exists for the current resolution.</summary>
    bool HasPoint(string name);

    /// <summary>Gets a stored point for the current resolution.</summary>
    bool TryGetPoint(string name, out Point point);

    /// <summary>True when a region with this name exists for the current resolution.</summary>
    bool HasRegion(string name);

    /// <summary>Gets a stored region for the current resolution.</summary>
    bool TryGetRegion(string name, out Rectangle region);

    /// <summary>All stored points for the current resolution.</summary>
    IReadOnlyList<CalibrationPoint> GetPoints();

    /// <summary>All stored regions for the current resolution.</summary>
    IReadOnlyList<CalibrationRegion> GetRegions();

    /// <summary>Deletes a point stored for the current resolution. Returns true when something was removed.</summary>
    bool DeletePoint(string name);

    /// <summary>Deletes a region stored for the current resolution. Returns true when something was removed.</summary>
    bool DeleteRegion(string name);
}

/// <summary>Default <see cref="ICalibrationService"/> implementation.</summary>
public sealed class CalibrationService : ICalibrationService
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RazorReaper",
        "automation-calibration.json");

    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;
    private readonly ILocalizer _localizer;
    private readonly IGameDisplayService? _displays;
    private readonly ILogger<CalibrationService> _logger;
    private readonly object _storeLock = new();
    private CalibrationStore? _store;
    private int _capturing; // 0 = idle, 1 = capturing (Interlocked)

    public CalibrationService(
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<CalibrationService> logger,
        IGameDisplayService? displays = null)
    {
        _notifications = notifications;
        _activity = activity;
        _localizer = localizer;
        _logger = logger;
        _displays = displays;
    }

    /// <summary>
    /// Still the primary screen's resolution, and deliberately so: it is the store's partition
    /// key, and re-basing it on the game's monitor would orphan every calibration already on
    /// disk. What it never was is a statement about the screen the region lives on — that is what
    /// <see cref="CalibrationRegion.MonitorResolution"/> now records, and what the mismatch check
    /// reads.
    /// </summary>
    public string CurrentResolutionKey
        => $"{GetSystemMetrics(SM_CXSCREEN)}x{GetSystemMetrics(SM_CYSCREEN)}";

    public AttachedDisplay? CurrentGameMonitor
    {
        get
        {
            try { return _displays?.GameMonitor; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the game's display — treating it as unknown");
                return null;
            }
        }
    }

    public bool IsCapturing => Volatile.Read(ref _capturing) == 1;

    public async Task<CalibrationPoint?> CapturePointAsync(
        string name, int countdownSeconds = 3, IProgress<int>? countdown = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
        {
            _notifications.ShowWarning(_localizer.T("scripts.cal.toast.busy"));
            return null;
        }

        try
        {
            await RunCountdownAsync(countdownSeconds, s => countdown?.Report(s), ct);
            if (!GetCursorPos(out var pt))
            {
                _notifications.ShowError(_localizer.T("scripts.cal.toast.nocursor"));
                return null;
            }

            var point = new CalibrationPoint(name.Trim(), pt.X, pt.Y, CurrentResolutionKey);
            lock (_storeLock)
            {
                var store = LoadStore();
                store.Points.RemoveAll(p => SameEntry(p.Name, p.Resolution, point.Name, point.Resolution));
                store.Points.Add(point);
                SaveStore(store);
            }

            _notifications.ShowSuccess(_localizer.T("scripts.cal.toast.pointcaptured", point.Name, point.X, point.Y));
            _activity.AddActivity(_localizer.T("scripts.cal.activity.pointcaptured", point.Name), "success", "scripts.cal.activity.pointcaptured");
            return point;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Point capture failed for '{Name}'", name);
            _notifications.ShowError(_localizer.T("scripts.cal.toast.pointfailed"));
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _capturing, 0);
        }
    }

    public async Task<CalibrationRegion?> CaptureRegionAsync(
        string name, int countdownSeconds = 3, IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
        {
            _notifications.ShowWarning(_localizer.T("scripts.cal.toast.busy"));
            return null;
        }

        try
        {
            await RunCountdownAsync(countdownSeconds, s => progress?.Report(new RegionCaptureProgress(1, s)), ct);
            if (!GetCursorPos(out var corner1))
            {
                _notifications.ShowError(_localizer.T("scripts.cal.toast.nocursor"));
                return null;
            }

            await RunCountdownAsync(countdownSeconds, s => progress?.Report(new RegionCaptureProgress(2, s)), ct);
            if (!GetCursorPos(out var corner2))
            {
                _notifications.ShowError(_localizer.T("scripts.cal.toast.nocursor"));
                return null;
            }

            var monitor = CurrentGameMonitor;
            var region = new CalibrationRegion(
                name.Trim(),
                Math.Min(corner1.X, corner2.X),
                Math.Min(corner1.Y, corner2.Y),
                Math.Max(corner1.X, corner2.X),
                Math.Max(corner1.Y, corner2.Y),
                CurrentResolutionKey,
                monitor?.DeviceName,
                monitor?.ResolutionKey);

            // Both corners landing on (nearly) the same pixel used to be stored as a 0x0 region and
            // reported as a success, which then enabled "Capture reference" against nothing. Keep
            // whatever was calibrated before rather than replacing it with an unusable rectangle.
            if (!CalibrationRegionRules.IsUsableSize(region.Left, region.Top, region.Right, region.Bottom))
            {
                _logger.LogWarning(
                    "Region capture for '{Name}' rejected: {Width}x{Height} is below the {Min}px minimum",
                    region.Name, region.Right - region.Left, region.Bottom - region.Top,
                    CalibrationRegionRules.MinimumSidePx);
                _notifications.ShowWarning(_localizer.T(
                    "scripts.cal.toast.regiontoosmall",
                    region.Right - region.Left,
                    region.Bottom - region.Top));
                return null;
            }

            lock (_storeLock)
            {
                var store = LoadStore();
                store.Regions.RemoveAll(r => SameEntry(r.Name, r.Resolution, region.Name, region.Resolution));
                store.Regions.Add(region);
                SaveStore(store);
            }

            _notifications.ShowSuccess(_localizer.T(
                "scripts.cal.toast.regioncaptured",
                region.Name,
                region.Right - region.Left,
                region.Bottom - region.Top));
            _activity.AddActivity(_localizer.T("scripts.cal.activity.regioncaptured", region.Name), "success", "scripts.cal.activity.regioncaptured");
            return region;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Region capture failed for '{Name}'", name);
            _notifications.ShowError(_localizer.T("scripts.cal.toast.regionfailed"));
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _capturing, 0);
        }
    }

    public bool HasPoint(string name) => TryGetPoint(name, out _);

    public bool TryGetPoint(string name, out Point point)
    {
        point = Point.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            var match = LoadStore().Points.FirstOrDefault(p => SameEntry(p.Name, p.Resolution, name, resolution));
            if (match is null) return false;
            point = new Point(match.X, match.Y);
            return true;
        }
    }

    public bool HasRegion(string name) => TryGetRegion(name, out _);

    public bool TryGetRegion(string name, out Rectangle region)
    {
        region = Rectangle.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            var match = LoadStore().Regions.FirstOrDefault(r => SameEntry(r.Name, r.Resolution, name, resolution));
            if (match is null) return false;
            region = match.ToRectangle();
            return true;
        }
    }

    public CalibrationRegion? GetRegion(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            return LoadStore().Regions.FirstOrDefault(r => SameEntry(r.Name, r.Resolution, name, resolution));
        }
    }

    public void StampRegionMonitor(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var monitor = CurrentGameMonitor;
        if (monitor is null) return;   // nothing to record is better than recording a guess

        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            var store = LoadStore();
            var index = store.Regions.FindIndex(r => SameEntry(r.Name, r.Resolution, name, resolution));
            if (index < 0) return;

            var existing = store.Regions[index];
            var stamped = existing with
            {
                MonitorDeviceName = monitor.DeviceName,
                MonitorResolution = monitor.ResolutionKey
            };

            // Nothing moved — skip the write. A snapshot capture per scan session rewriting the
            // whole store would be a file write per press for no change at all.
            if (stamped == existing) return;

            store.Regions[index] = stamped;
            SaveStore(store);
        }
    }

    public IReadOnlyList<CalibrationPoint> GetPoints()
    {
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            return LoadStore().Points
                .Where(p => string.Equals(p.Resolution, resolution, StringComparison.Ordinal))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<CalibrationRegion> GetRegions()
    {
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            return LoadStore().Regions
                .Where(r => string.Equals(r.Resolution, resolution, StringComparison.Ordinal))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool DeletePoint(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            var store = LoadStore();
            var removed = store.Points.RemoveAll(p => SameEntry(p.Name, p.Resolution, name, resolution));
            if (removed > 0) SaveStore(store);
            return removed > 0;
        }
    }

    public bool DeleteRegion(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var resolution = CurrentResolutionKey;
        lock (_storeLock)
        {
            var store = LoadStore();
            var removed = store.Regions.RemoveAll(r => SameEntry(r.Name, r.Resolution, name, resolution));
            if (removed > 0) SaveStore(store);
            return removed > 0;
        }
    }

    // ─── Internals ─────────────────────────────────────────────────────────────

    private static async Task RunCountdownAsync(int seconds, Action<int> report, CancellationToken ct)
    {
        for (var s = Math.Max(1, seconds); s > 0; s--)
        {
            try { report(s); }
            catch { /* progress subscriber errors are not ours */ }
            await Task.Delay(1000, ct);
        }
        try { report(0); }
        catch { /* progress subscriber errors are not ours */ }
    }

    private static bool SameEntry(string name, string resolution, string otherName, string otherResolution)
        => string.Equals(name, otherName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(resolution, otherResolution, StringComparison.Ordinal);

    private CalibrationStore LoadStore()
    {
        if (_store is not null) return _store;
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                _store = JsonSerializer.Deserialize<CalibrationStore>(json) ?? new CalibrationStore();
            }
            else
            {
                _store = new CalibrationStore();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load calibration store — starting empty");
            _store = new CalibrationStore();
        }
        return _store;
    }

    private void SaveStore(CalibrationStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save calibration store");
        }
    }

    private sealed class CalibrationStore
    {
        public List<CalibrationPoint> Points { get; set; } = new();
        public List<CalibrationRegion> Regions { get; set; } = new();
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVEPOINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NATIVEPOINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
