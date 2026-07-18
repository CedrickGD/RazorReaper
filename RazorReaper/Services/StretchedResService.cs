using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Implementations;

namespace RazorReaper.Services
{
    /// <summary>
    /// A single display mode (desktop resolution + refresh). Width/Height are the pixel
    /// dimensions the desktop is (or would be) set to.
    /// </summary>
    public sealed record DisplayResolution(int Width, int Height, int RefreshHz)
    {
        /// <summary>Width ÷ Height, used to derive the aspect label.</summary>
        public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;

        /// <summary>Compact aspect label ("4:3", "5:4", "16:9", "16:10", "3:2" …).</summary>
        public string AspectLabel => StretchedResService.DescribeAspect(Width, Height);

        public string Label => $"{Width} × {Height}";
    }

    /// <summary>Which GPU is driving the primary display — used only to show the right guidance.</summary>
    public enum GpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel
    }

    public sealed record GpuInfo(GpuVendor Vendor, string AdapterName);

    /// <summary>A curated, known-safe stretched-resolution preset.</summary>
    public sealed class StretchedPreset
    {
        public string Name { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public string AspectLabel { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>Result of a validation / display-change operation.</summary>
    public sealed record DisplayChangeResult(bool Success, string? Error = null)
    {
        public static DisplayChangeResult Ok() => new(true);
        public static DisplayChangeResult Fail(string error) => new(false, error);
    }

    /// <summary>
    /// SAFE, reversible stretched-resolution controller. Switches the DESKTOP resolution
    /// via Win32 ChangeDisplaySettingsEx using CDS_FULLSCREEN (a temporary, non-persisted
    /// change that never survives a reboot), and enforces a mandatory 15-second auto-revert
    /// exactly like Windows' own display-change flow: unless the caller confirms within the
    /// window the previous mode is restored automatically. NVAPI custom-mode creation is
    /// deliberately NOT used — only standard modes the driver already reports are applied.
    /// </summary>
    public interface IStretchedResService
    {
        /// <summary>Raised on every countdown tick and whenever the pending/confirmed state changes.</summary>
        event Action? StateChanged;

        /// <summary>The primary display's current desktop resolution.</summary>
        DisplayResolution GetCurrentResolution();

        /// <summary>The primary display's native (largest reported) resolution.</summary>
        DisplayResolution GetNativeResolution();

        /// <summary>All distinct desktop modes the driver reports (deduped by size, highest refresh kept).</summary>
        IReadOnlyList<DisplayResolution> GetAvailableModes();

        /// <summary>The curated stretched presets.</summary>
        IReadOnlyList<StretchedPreset> GetPresets();

        /// <summary>Primary-display GPU vendor + adapter string (best effort).</summary>
        GpuInfo GetGpuInfo();

        /// <summary>True while an applied resolution is awaiting the user's "keep" confirmation.</summary>
        bool IsPendingConfirmation { get; }

        /// <summary>Seconds left before the pending change auto-reverts.</summary>
        int SecondsRemaining { get; }

        /// <summary>The resolution in effect before the pending change (the one auto-revert restores).</summary>
        DisplayResolution? PreviousResolution { get; }

        /// <summary>The resolution that was applied and is awaiting confirmation.</summary>
        DisplayResolution? PendingResolution { get; }

        /// <summary>Validates a width/height against sane bounds (does not touch the display).</summary>
        DisplayChangeResult ValidateResolution(int width, int height);

        /// <summary>
        /// Applies the desktop resolution and starts the mandatory 15-second auto-revert.
        /// The change is temporary (CDS_FULLSCREEN); call <see cref="ConfirmKeep"/> to keep it.
        /// </summary>
        DisplayChangeResult ApplyResolution(int width, int height);

        /// <summary>Keeps the pending resolution and cancels the auto-revert.</summary>
        void ConfirmKeep();

        /// <summary>Immediately restores the previous resolution and cancels the auto-revert.</summary>
        DisplayChangeResult RevertNow();

        /// <summary>Restores the registry-persisted (native/normal) desktop resolution at any time.</summary>
        DisplayChangeResult RestoreNative();

        /// <summary>Persists the last chosen preset/custom values (NOT the applied state).</summary>
        void SaveLastChoice(int width, int height, bool isCustom);

        /// <summary>Loads the last chosen preset/custom values, or null when none saved.</summary>
        (int Width, int Height, bool IsCustom)? LoadLastChoice();

        /// <summary>
        /// Opt-in: writes the resolution into ARK's GameUserSettings.ini (ResolutionSizeX/Y +
        /// windowed-fullscreen) via the targeted INI editor, which auto-backs-up the file first.
        /// </summary>
        Task<DisplayChangeResult> WriteArkResolutionAsync(int width, int height);

        /// <summary>True while the ARK game process is running (it rewrites GameUserSettings.ini on exit).</summary>
        bool IsArkRunning();
    }

    /// <inheritdoc cref="IStretchedResService"/>
    public sealed class StretchedResService : IStretchedResService, IDisposable
    {
        // ── Auto-revert window ──────────────────────────────────────────────
        private const int AutoRevertSeconds = 15;

        // ── Sane custom bounds ──────────────────────────────────────────────
        private const int MinDimension = 640;
        private const int MaxWidth = 7680;
        private const int MaxHeight = 4320;

        // ── Preferences keys (stretchedres.*) ───────────────────────────────
        private const string PrefWidth = "stretchedres.lastWidth";
        private const string PrefHeight = "stretchedres.lastHeight";
        private const string PrefIsCustom = "stretchedres.lastIsCustom";

        private readonly ILogger<StretchedResService> _logger;
        private readonly INotificationService _notifications;
        private readonly IActivityService _activity;
        private readonly IArkPathProvider _arkPathProvider;
        private readonly IGameIniService _gameIniService;

        private readonly object _gate = new();
        private System.Threading.Timer? _revertTimer;

        private DEVMODE _previousDevMode;
        private bool _hasPrevious;
        private DisplayResolution? _previousResolution;
        private DisplayResolution? _pendingResolution;
        private bool _isPending;
        private int _secondsRemaining;

        public StretchedResService(
            ILogger<StretchedResService> logger,
            INotificationService notifications,
            IActivityService activity,
            IArkPathProvider arkPathProvider,
            IGameIniService gameIniService)
        {
            _logger = logger;
            _notifications = notifications;
            _activity = activity;
            _arkPathProvider = arkPathProvider;
            _gameIniService = gameIniService;
        }

        public event Action? StateChanged;

        public bool IsPendingConfirmation { get { lock (_gate) return _isPending; } }
        public int SecondsRemaining { get { lock (_gate) return _secondsRemaining; } }
        public DisplayResolution? PreviousResolution { get { lock (_gate) return _previousResolution; } }
        public DisplayResolution? PendingResolution { get { lock (_gate) return _pendingResolution; } }

        // ────────────────────────────────────────────────────────────────────
        // Enumeration
        // ────────────────────────────────────────────────────────────────────

        public DisplayResolution GetCurrentResolution()
        {
            try
            {
                var dm = NewDevMode();
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) != 0)
                {
                    return new DisplayResolution((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read current display resolution");
            }

            return new DisplayResolution(0, 0, 0);
        }

        public DisplayResolution GetNativeResolution()
        {
            var modes = GetAvailableModes();
            if (modes.Count == 0)
            {
                return GetCurrentResolution();
            }

            // The native panel resolution is the largest reported mode.
            return modes.OrderByDescending(m => (long)m.Width * m.Height).First();
        }

        public IReadOnlyList<DisplayResolution> GetAvailableModes()
        {
            var best = new Dictionary<(int, int), int>();
            try
            {
                var dm = NewDevMode();
                var i = 0;
                while (EnumDisplaySettings(null, i, ref dm) != 0)
                {
                    var w = (int)dm.dmPelsWidth;
                    var h = (int)dm.dmPelsHeight;
                    var hz = (int)dm.dmDisplayFrequency;
                    // Skip 4/8/16-bit legacy modes; keep the highest refresh per size.
                    if (dm.dmBitsPerPel >= 32 && w > 0 && h > 0)
                    {
                        var key = (w, h);
                        if (!best.TryGetValue(key, out var existing) || hz > existing)
                        {
                            best[key] = hz;
                        }
                    }

                    i++;
                    dm = NewDevMode();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate display modes");
            }

            return best
                .Select(kvp => new DisplayResolution(kvp.Key.Item1, kvp.Key.Item2, kvp.Value))
                .OrderByDescending(m => (long)m.Width * m.Height)
                .ThenByDescending(m => m.Width)
                .ToList();
        }

        public IReadOnlyList<StretchedPreset> GetPresets() => Presets;

        public GpuInfo GetGpuInfo()
        {
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                // iDevNum 0 = the primary display adapter.
                if (EnumDisplayDevices(null, 0, ref dd, 0))
                {
                    var name = dd.DeviceString ?? string.Empty;
                    var vendor = ClassifyVendor(name);
                    return new GpuInfo(vendor, name.Trim());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPU vendor detection failed");
            }

            return new GpuInfo(GpuVendor.Unknown, string.Empty);
        }

        private static GpuVendor ClassifyVendor(string adapter)
        {
            var s = adapter.ToUpperInvariant();
            if (s.Contains("NVIDIA") || s.Contains("GEFORCE") || s.Contains("QUADRO") || s.Contains("RTX") || s.Contains("GTX"))
                return GpuVendor.Nvidia;
            if (s.Contains("AMD") || s.Contains("RADEON") || s.Contains("ATI"))
                return GpuVendor.Amd;
            if (s.Contains("INTEL") || s.Contains("ARC") || s.Contains("UHD") || s.Contains("IRIS"))
                return GpuVendor.Intel;
            return GpuVendor.Unknown;
        }

        // ────────────────────────────────────────────────────────────────────
        // Validation
        // ────────────────────────────────────────────────────────────────────

        public DisplayChangeResult ValidateResolution(int width, int height)
        {
            if (width < MinDimension || height < MinDimension)
                return DisplayChangeResult.Fail($"Resolution must be at least {MinDimension}×{MinDimension}.");
            if (width > MaxWidth || height > MaxHeight)
                return DisplayChangeResult.Fail($"Resolution must not exceed {MaxWidth}×{MaxHeight}.");
            return DisplayChangeResult.Ok();
        }

        // ────────────────────────────────────────────────────────────────────
        // Apply / confirm / revert
        // ────────────────────────────────────────────────────────────────────

        public DisplayChangeResult ApplyResolution(int width, int height)
        {
            var validation = ValidateResolution(width, height);
            if (!validation.Success)
            {
                return validation;
            }

            lock (_gate)
            {
                if (_isPending)
                {
                    return DisplayChangeResult.Fail("Confirm or revert the current change first.");
                }
            }

            try
            {
                // Capture the current mode as the exact revert target BEFORE changing anything.
                var current = NewDevMode();
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref current) == 0)
                {
                    return DisplayChangeResult.Fail("Could not read the current display mode.");
                }

                // Build the target from the current mode, changing only the pixel dimensions so
                // refresh rate and colour depth are preserved.
                var target = current;
                target.dmPelsWidth = (uint)width;
                target.dmPelsHeight = (uint)height;
                target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
                target.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

                // Test first so an unsupported mode fails cleanly without ever switching.
                var test = ChangeDisplaySettingsEx(null, ref target, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
                if (test != DISP_CHANGE_SUCCESSFUL)
                {
                    return DisplayChangeResult.Fail(DescribeMode(test, width, height));
                }

                // CDS_FULLSCREEN = temporary change, not written to the registry — a reboot (or our
                // own auto-revert) always brings the normal desktop resolution back.
                var apply = ChangeDisplaySettingsEx(null, ref target, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
                if (apply != DISP_CHANGE_SUCCESSFUL)
                {
                    return DisplayChangeResult.Fail(DescribeMode(apply, width, height));
                }

                lock (_gate)
                {
                    _previousDevMode = current;
                    _hasPrevious = true;
                    _previousResolution = new DisplayResolution((int)current.dmPelsWidth, (int)current.dmPelsHeight, (int)current.dmDisplayFrequency);
                    _pendingResolution = new DisplayResolution(width, height, (int)target.dmDisplayFrequency);
                    _isPending = true;
                    _secondsRemaining = AutoRevertSeconds;
                    StartRevertTimer();
                }

                _logger.LogInformation("Applied stretched resolution {W}x{H} (temporary, auto-revert in {S}s)", width, height, AutoRevertSeconds);
                _activity.AddActivity($"Applied {width}×{height} — confirm to keep", "warning");
                RaiseStateChanged();
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply resolution {W}x{H}", width, height);
                return DisplayChangeResult.Fail($"Apply failed: {ex.Message}");
            }
        }

        public void ConfirmKeep()
        {
            DisplayResolution? kept;
            lock (_gate)
            {
                if (!_isPending)
                {
                    return;
                }

                StopRevertTimer();
                kept = _pendingResolution;
                _isPending = false;
                _secondsRemaining = 0;
            }

            _logger.LogInformation("User kept stretched resolution {Res}", kept?.Label);
            if (kept != null)
            {
                _notifications.ShowSuccess($"Kept {kept.Label}.");
                _activity.AddActivity($"Kept resolution {kept.Label}", "success");
            }
            RaiseStateChanged();
        }

        public DisplayChangeResult RevertNow()
        {
            var result = RevertInternal("manual");
            if (result.Success)
            {
                _notifications.ShowInfo("Reverted to the previous resolution.");
                _activity.AddActivity("Reverted resolution", "info");
            }
            else if (result.Error != null)
            {
                _notifications.ShowError(result.Error);
            }
            RaiseStateChanged();
            return result;
        }

        public DisplayChangeResult RestoreNative()
        {
            // Cancel any pending confirmation first so its timer cannot fire mid-restore.
            lock (_gate)
            {
                StopRevertTimer();
                _isPending = false;
                _secondsRemaining = 0;
                _pendingResolution = null;
            }

            try
            {
                // Passing a null DEVMODE with no flags resets to the registry-persisted (normal) mode.
                var result = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                if (result != DISP_CHANGE_SUCCESSFUL)
                {
                    var msg = DescribeResult(result);
                    _logger.LogError("RestoreNative failed: {Msg}", msg);
                    _notifications.ShowError($"Restore failed: {msg}");
                    RaiseStateChanged();
                    return DisplayChangeResult.Fail(msg);
                }

                lock (_gate)
                {
                    _hasPrevious = false;
                    _previousResolution = null;
                }

                var now = GetCurrentResolution();
                _logger.LogInformation("Restored native/normal desktop resolution ({Res})", now.Label);
                _notifications.ShowSuccess($"Restored {now.Label}.");
                _activity.AddActivity($"Restored desktop resolution {now.Label}", "success");
                RaiseStateChanged();
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RestoreNative threw");
                _notifications.ShowError($"Restore failed: {ex.Message}");
                RaiseStateChanged();
                return DisplayChangeResult.Fail(ex.Message);
            }
        }

        // Restores _previousDevMode. Caller decides on notifications.
        private DisplayChangeResult RevertInternal(string reason)
        {
            DEVMODE prev;
            bool hasPrev;
            lock (_gate)
            {
                StopRevertTimer();
                hasPrev = _hasPrevious;
                prev = _previousDevMode;
                _isPending = false;
                _secondsRemaining = 0;
            }

            if (!hasPrev)
            {
                return DisplayChangeResult.Fail("No previous resolution to revert to.");
            }

            try
            {
                prev.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
                prev.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                var result = ChangeDisplaySettingsEx(null, ref prev, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
                if (result != DISP_CHANGE_SUCCESSFUL)
                {
                    // Last resort: reset to the registry-persisted mode.
                    var reset = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                    if (reset != DISP_CHANGE_SUCCESSFUL)
                    {
                        var msg = DescribeResult(result);
                        _logger.LogError("Revert ({Reason}) failed: {Msg}", reason, msg);
                        return DisplayChangeResult.Fail(msg);
                    }
                }

                lock (_gate)
                {
                    _pendingResolution = null;
                }

                _logger.LogInformation("Reverted display resolution ({Reason}) to {Res}", reason, _previousResolution?.Label);
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Revert ({Reason}) threw", reason);
                return DisplayChangeResult.Fail(ex.Message);
            }
        }

        private void OnRevertTick(object? _)
        {
            bool fire = false;
            int remaining;
            lock (_gate)
            {
                if (!_isPending)
                {
                    return;
                }

                _secondsRemaining--;
                remaining = _secondsRemaining;
                if (remaining <= 0)
                {
                    fire = true;
                }
            }

            if (fire)
            {
                var result = RevertInternal("auto-timeout");
                if (result.Success)
                {
                    _notifications.ShowWarning("Resolution reverted automatically — no confirmation received.");
                    _activity.AddActivity("Auto-reverted resolution (no confirmation)", "warning");
                }
                else if (result.Error != null)
                {
                    _notifications.ShowError($"Auto-revert failed: {result.Error}");
                }
            }

            RaiseStateChanged();
        }

        private void StartRevertTimer()
        {
            // Assumes _gate is held.
            StopRevertTimer();
            _revertTimer = new System.Threading.Timer(OnRevertTick, null, 1000, 1000);
        }

        private void StopRevertTimer()
        {
            // Assumes _gate is held.
            _revertTimer?.Dispose();
            _revertTimer = null;
        }

        private void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StretchedRes StateChanged handler threw");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Persistence (last chosen preset only — never the applied state)
        // ────────────────────────────────────────────────────────────────────

        public void SaveLastChoice(int width, int height, bool isCustom)
        {
            try
            {
                Preferences.Set(PrefWidth, width);
                Preferences.Set(PrefHeight, height);
                Preferences.Set(PrefIsCustom, isCustom);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist last stretched-res choice");
            }
        }

        public (int Width, int Height, bool IsCustom)? LoadLastChoice()
        {
            try
            {
                var w = Preferences.Get(PrefWidth, 0);
                var h = Preferences.Get(PrefHeight, 0);
                if (w <= 0 || h <= 0)
                {
                    return null;
                }

                var isCustom = Preferences.Get(PrefIsCustom, false);
                return (w, h, isCustom);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load last stretched-res choice");
                return null;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Optional ARK GameUserSettings.ini write (opt-in)
        // ────────────────────────────────────────────────────────────────────

        public bool IsArkRunning()
        {
            try
            {
                return _gameIniService.IsArkRunning();
            }
            catch
            {
                return false;
            }
        }

        public async Task<DisplayChangeResult> WriteArkResolutionAsync(int width, int height)
        {
            var validation = ValidateResolution(width, height);
            if (!validation.Success)
            {
                return validation;
            }

            try
            {
                if (_arkPathProvider.FindArkPath() == null)
                {
                    return DisplayChangeResult.Fail("ARK installation not found.");
                }

                var w = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var h = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var entries = new List<GameIniEntry>
                {
                    new(ShooterSection, "ResolutionSizeX", w),
                    new(ShooterSection, "ResolutionSizeY", h),
                    new(ShooterSection, "LastUserConfirmedResolutionSizeX", w),
                    new(ShooterSection, "LastUserConfirmedResolutionSizeY", h),
                    // 1 = Windowed-Fullscreen (borderless): fills the stretched desktop output.
                    new(ShooterSection, "FullscreenMode", "1"),
                    new(ShooterSection, "LastConfirmedFullscreenMode", "1")
                };

                var result = await _gameIniService.ApplyEntriesAsync(GameIniTarget.GameUserSettings, entries);
                if (!result.Success)
                {
                    return DisplayChangeResult.Fail(result.Error ?? "Failed to write GameUserSettings.ini.");
                }

                _logger.LogInformation("Wrote ARK resolution {W}x{H} to GameUserSettings.ini (backup: {Backup})", width, height, result.BackupPath ?? "none");
                _activity.AddActivity($"Wrote {width}×{height} to ARK's GameUserSettings.ini", "info");
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write ARK resolution {W}x{H}", width, height);
                return DisplayChangeResult.Fail($"ARK write failed: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────────────

        internal static string DescribeAspect(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return string.Empty;
            }

            var gcd = Gcd(width, height);
            var aw = width / gcd;
            var ah = height / gcd;

            // Collapse to the familiar labels; anything unusual is shown as the reduced ratio.
            return (aw, ah) switch
            {
                (4, 3) => "4:3",
                (5, 4) => "5:4",
                (16, 9) => "16:9",
                (16, 10) => "16:10",
                (3, 2) => "3:2",
                (21, 9) => "21:9",
                _ => $"{aw}:{ah}"
            };
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }
            return a == 0 ? 1 : a;
        }

        private static string DescribeMode(int code, int width, int height)
        {
            if (code == DISP_CHANGE_BADMODE)
            {
                return $"Your display driver rejected {width}×{height}. Create it first in NVIDIA Control Panel → Change resolution → Customize, then try again.";
            }
            return DescribeResult(code);
        }

        private static string DescribeResult(int code) => code switch
        {
            DISP_CHANGE_SUCCESSFUL => "Success.",
            DISP_CHANGE_RESTART => "The change requires a restart to take effect.",
            DISP_CHANGE_BADMODE => "The display driver does not support this resolution.",
            DISP_CHANGE_FAILED => "The display driver failed the requested change.",
            DISP_CHANGE_BADFLAGS => "Invalid display-change flags.",
            DISP_CHANGE_BADPARAM => "Invalid display-change parameters.",
            DISP_CHANGE_NOTUPDATED => "Unable to write the new settings to the registry.",
            DISP_CHANGE_BADDUALVIEW => "The change is not supported in a multi-view configuration.",
            _ => $"Display change failed (code {code})."
        };

        public void Dispose()
        {
            lock (_gate)
            {
                StopRevertTimer();
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Curated presets
        // ────────────────────────────────────────────────────────────────────

        private const string ShooterSection = "/Script/ShooterGame.ShooterGameUserSettings";

        private static readonly IReadOnlyList<StretchedPreset> Presets = new List<StretchedPreset>
        {
            new() { Name = "1440 × 1080", Width = 1440, Height = 1080, AspectLabel = "4:3",  Note = "Popular wide-model stretch" },
            new() { Name = "1280 × 1024", Width = 1280, Height = 1024, AspectLabel = "5:4",  Note = "Classic 5:4 hitbox stretch" },
            new() { Name = "1024 × 768",  Width = 1024, Height = 768,  AspectLabel = "4:3",  Note = "Maximum model width" },
            new() { Name = "1600 × 1080", Width = 1600, Height = 1080, AspectLabel = "40:27", Note = "Mild stretch, sharper" },
            new() { Name = "1280 × 960",  Width = 1280, Height = 960,  AspectLabel = "4:3",  Note = "Lighter 4:3 option" }
        };

        // ────────────────────────────────────────────────────────────────────
        // Win32 interop — EnumDisplaySettings / ChangeDisplaySettingsEx / EnumDisplayDevices
        // ────────────────────────────────────────────────────────────────────

        private const int ENUM_CURRENT_SETTINGS = -1;

        // ChangeDisplaySettingsEx dwFlags
        private const uint CDS_TEST = 0x00000002;
        private const uint CDS_FULLSCREEN = 0x00000004;

        // dmFields
        private const uint DM_BITSPERPEL = 0x00040000;
        private const uint DM_PELSWIDTH = 0x00080000;
        private const uint DM_PELSHEIGHT = 0x00100000;
        private const uint DM_DISPLAYFREQUENCY = 0x00400000;

        // ChangeDisplaySettingsEx return codes
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DISP_CHANGE_RESTART = 1;
        private const int DISP_CHANGE_FAILED = -1;
        private const int DISP_CHANGE_BADMODE = -2;
        private const int DISP_CHANGE_NOTUPDATED = -3;
        private const int DISP_CHANGE_BADFLAGS = -4;
        private const int DISP_CHANGE_BADPARAM = -5;
        private const int DISP_CHANGE_BADDUALVIEW = -6;

        private static DEVMODE NewDevMode() => new()
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
        };

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        // Overload used to apply a specific mode.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        // Overload used to reset to the registry-persisted mode (lpDevMode == NULL).
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;

            // Display union: POINTL dmPosition + dmDisplayOrientation + dmDisplayFixedOutput
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }
    }
}
