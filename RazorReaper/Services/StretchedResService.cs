using Microsoft.Extensions.Logging;
using RazorReaper.Services.Implementations;
using RazorReaper.Services.Localization;

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

    /// <summary>Which GPU is driving the display — used only to show the right guidance.</summary>
    public enum GpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel
    }

    public sealed record GpuInfo(GpuVendor Vendor, string AdapterName);

    /// <summary>
    /// One monitor the user can aim a resolution change at.
    ///
    /// <paramref name="DeviceName"/> is the Win32 display-device name (<c>\\.\DISPLAY2</c>) — the
    /// same name the crosshair and HUD overlays store for their own monitor pickers, so the three
    /// features agree on what "monitor 2" is. <paramref name="Index"/> is read off that name so
    /// the label does not renumber itself when a screen is unplugged.
    /// </summary>
    public sealed record DisplayMonitor(int Index, string DeviceName, DisplayResolution CurrentMode, bool IsPrimary)
    {
        /// <summary>
        /// English label, for the activity log and log lines. The page builds its own translated
        /// label from the same parts — this one never reaches a localized surface.
        /// </summary>
        public string Label => IsPrimary
            ? $"Monitor {Index} — {CurrentMode.Width}×{CurrentMode.Height} (primary)"
            : $"Monitor {Index} — {CurrentMode.Width}×{CurrentMode.Height}";
    }

    /// <summary>
    /// What a stored device name resolved to. <paramref name="FellBackToPrimary"/> is the case the
    /// page has to say out loud: the monitor a preference names is not attached any more, so the
    /// change is going to the primary display instead of the screen the user last used.
    /// </summary>
    public sealed record MonitorTarget(DisplayMonitor? Monitor, bool FellBackToPrimary)
    {
        public string? DeviceName => Monitor?.DeviceName;
    }

    /// <summary>
    /// The two places on the page that pick a monitor. They are stored apart on purpose: a
    /// stretched preset is usually aimed at the screen the game runs on, and a custom resolution
    /// is just as often aimed at a second screen being set up for something else.
    /// </summary>
    public enum ResolutionFeature
    {
        Stretched,
        Custom
    }

    /// <summary>A curated, known-safe stretched-resolution preset.</summary>
    public sealed record StretchedPreset
    {
        public string Name { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public string AspectLabel { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;

        /// <summary>Same height as the screen's native mode, so it is only scaled sideways.</summary>
        public bool KeepsNativeHeight { get; init; }
    }

    /// <summary>Result of a validation / display-change operation.</summary>
    public sealed record DisplayChangeResult(bool Success, string? Error = null)
    {
        public static DisplayChangeResult Ok() => new(true);
        public static DisplayChangeResult Fail(string error) => new(false, error);
    }

    /// <summary>
    /// SAFE, reversible stretched-resolution controller. Switches a monitor's desktop resolution
    /// through <see cref="IDisplayApi"/> (Win32 ChangeDisplaySettingsEx with CDS_FULLSCREEN: a
    /// temporary, non-persisted change that never survives a reboot), and enforces a mandatory
    /// 15-second auto-revert exactly like Windows' own display-change flow: unless the caller
    /// confirms within the window the previous mode is restored automatically.
    ///
    /// Every mode query and every apply takes a device name. Passing null means the primary
    /// display, which is what the whole service did before monitors were selectable — so a caller
    /// that never heard of monitors, and a settings file written before they existed, both keep
    /// behaving exactly as they did.
    /// </summary>
    public interface IStretchedResService
    {
        /// <summary>Raised on every countdown tick and whenever the pending/confirmed state changes.</summary>
        event Action? StateChanged;

        /// <summary>Every monitor attached to the desktop, primary first in adapter order.</summary>
        IReadOnlyList<DisplayMonitor> GetMonitors();

        /// <summary>
        /// Resolves a stored device name against the monitors that are actually attached, falling
        /// back to the primary display when the named one is gone.
        /// </summary>
        MonitorTarget ResolveMonitor(string? deviceName);

        /// <summary>The device's current desktop resolution (null = primary).</summary>
        DisplayResolution GetCurrentResolution(string? deviceName = null);

        /// <summary>The device's native (largest reported) resolution (null = primary).</summary>
        DisplayResolution GetNativeResolution(string? deviceName = null);

        /// <summary>All distinct desktop modes the driver reports for the device (deduped by size, highest refresh kept).</summary>
        IReadOnlyList<DisplayResolution> GetAvailableModes(string? deviceName = null);

        /// <summary>The stretched presets for the device (null = primary), built from its native height.</summary>
        IReadOnlyList<StretchedPreset> GetPresets(string? deviceName = null);

        /// <summary>The device's GPU vendor + adapter string, best effort (null = primary).</summary>
        GpuInfo GetGpuInfo(string? deviceName = null);

        /// <summary>True while an applied resolution is awaiting the user's "keep" confirmation.</summary>
        bool IsPendingConfirmation { get; }

        /// <summary>Seconds left before the pending change auto-reverts.</summary>
        int SecondsRemaining { get; }

        /// <summary>The resolution in effect before the pending change (the one auto-revert restores).</summary>
        DisplayResolution? PreviousResolution { get; }

        /// <summary>The resolution that was applied and is awaiting confirmation.</summary>
        DisplayResolution? PendingResolution { get; }

        /// <summary>The device the pending change was applied to, so the page can name the screen.</summary>
        string? PendingDeviceName { get; }

        /// <summary>
        /// The device a resolution was last applied to and not taken back — it outlives
        /// <see cref="ConfirmKeep"/>, unlike <see cref="PendingDeviceName"/>, and is cleared again
        /// by a revert or a restore of that same screen. It is what lets the page keep describing
        /// and restoring the monitor that actually changed, rather than whichever one a picker
        /// happens to be aimed at.
        /// </summary>
        string? LastAppliedDeviceName { get; }

        /// <summary>Validates a width/height against sane bounds (does not touch the display).</summary>
        DisplayChangeResult ValidateResolution(int width, int height);

        /// <summary>
        /// Applies the desktop resolution on the given device (null = primary) and starts the
        /// mandatory 15-second auto-revert. The change is temporary (CDS_FULLSCREEN); call
        /// <see cref="ConfirmKeep"/> to keep it.
        /// </summary>
        DisplayChangeResult ApplyResolution(int width, int height, string? deviceName = null);

        /// <summary>Keeps the pending resolution and cancels the auto-revert.</summary>
        void ConfirmKeep();

        /// <summary>Immediately restores the previous resolution and cancels the auto-revert.</summary>
        DisplayChangeResult RevertNow();

        /// <summary>Restores the registry-persisted (native/normal) desktop resolution at any time.</summary>
        DisplayChangeResult RestoreNative(string? deviceName = null);

        /// <summary>Persists the last chosen preset/custom values (NOT the applied state).</summary>
        void SaveLastChoice(int width, int height, bool isCustom);

        /// <summary>Loads the last chosen preset/custom values, or null when none saved.</summary>
        (int Width, int Height, bool IsCustom)? LoadLastChoice();

        /// <summary>Persists the monitor one of the two pickers is aimed at. Null clears it.</summary>
        void SaveDeviceChoice(ResolutionFeature feature, string? deviceName);

        /// <summary>The monitor a picker was last aimed at, or null for "the primary display".</summary>
        string? LoadDeviceChoice(ResolutionFeature feature);

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
        // Public so the page can word its own validation message without a second opinion on
        // what the limits are.
        public const int MinDimension = 640;
        public const int MaxWidth = 7680;
        public const int MaxHeight = 4320;

        // ── Preferences keys (stretchedres.*) ───────────────────────────────
        private const string PrefWidth = "stretchedres.lastWidth";
        private const string PrefHeight = "stretchedres.lastHeight";
        private const string PrefIsCustom = "stretchedres.lastIsCustom";
        private const string PrefStretchedDevice = "stretchedres.device.stretched";
        private const string PrefCustomDevice = "stretchedres.device.custom";

        private readonly ILogger<StretchedResService> _logger;
        private readonly INotificationService _notifications;
        private readonly IActivityService _activity;
        private readonly IArkPathProvider _arkPathProvider;
        private readonly IGameIniService _gameIniService;
        private readonly IDisplayApi _display;
        private readonly IPreferencesStore _preferences;
        private readonly ILocalizer _localizer;

        private readonly object _gate = new();
        private System.Threading.Timer? _revertTimer;

        private DisplayMode _previousMode;
        private bool _hasPrevious;
        private string? _previousDevice;
        private DisplayResolution? _previousResolution;
        private DisplayResolution? _pendingResolution;
        private string? _pendingDevice;

        // Every screen that currently holds a mode this service applied, oldest first. A list and
        // not a single slot because two screens can be stretched at once (a preset on one, a custom
        // mode on the other): taking one of them back must hand the title back to the other, not
        // claim nothing is stretched any more.
        private readonly List<string> _appliedDevices = new();
        private bool _isPending;
        private int _secondsRemaining;

        public StretchedResService(
            ILogger<StretchedResService> logger,
            INotificationService notifications,
            IActivityService activity,
            IArkPathProvider arkPathProvider,
            IGameIniService gameIniService,
            IDisplayApi display,
            IPreferencesStore preferences,
            ILocalizer localizer)
        {
            _logger = logger;
            _notifications = notifications;
            _activity = activity;
            _arkPathProvider = arkPathProvider;
            _gameIniService = gameIniService;
            _display = display;
            _preferences = preferences;
            _localizer = localizer;
        }

        public event Action? StateChanged;

        public bool IsPendingConfirmation { get { lock (_gate) return _isPending; } }
        public int SecondsRemaining { get { lock (_gate) return _secondsRemaining; } }
        public DisplayResolution? PreviousResolution { get { lock (_gate) return _previousResolution; } }
        public DisplayResolution? PendingResolution { get { lock (_gate) return _pendingResolution; } }
        public string? PendingDeviceName { get { lock (_gate) return _pendingDevice; } }
        public string? LastAppliedDeviceName
        {
            get { lock (_gate) return _appliedDevices.Count == 0 ? null : _appliedDevices[^1]; }
        }

        // ────────────────────────────────────────────────────────────────────
        // Monitors
        // ────────────────────────────────────────────────────────────────────

        public IReadOnlyList<DisplayMonitor> GetMonitors()
        {
            try
            {
                var attached = _display.EnumerateAttachedDisplays();
                var monitors = new List<DisplayMonitor>(attached.Count);
                for (var ordinal = 0; ordinal < attached.Count; ordinal++)
                {
                    var adapter = attached[ordinal];
                    var mode = _display.GetCurrentMode(adapter.DeviceName);
                    monitors.Add(new DisplayMonitor(
                        MonitorIndex(adapter.DeviceName, ordinal),
                        adapter.DeviceName,
                        new DisplayResolution(mode?.Width ?? 0, mode?.Height ?? 0, mode?.RefreshHz ?? 0),
                        adapter.IsPrimary));
                }

                // Primary first: it is the default target, and a list that starts somewhere else
                // reads as though the app picked a screen for you.
                return monitors
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.Index)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate monitors");
                return Array.Empty<DisplayMonitor>();
            }
        }

        public MonitorTarget ResolveMonitor(string? deviceName)
        {
            var monitors = GetMonitors();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

            if (string.IsNullOrEmpty(deviceName))
            {
                return new MonitorTarget(primary, false);
            }

            var match = monitors.FirstOrDefault(
                m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

            // A name that no longer matches anything attached is the unplugged case. Falling back
            // silently would aim the next change at a different screen than the one on the label.
            return match is null
                ? new MonitorTarget(primary, true)
                : new MonitorTarget(match, false);
        }

        /// <summary>
        /// The device every Win32 call below is actually made against: the requested one if it is
        /// attached, the primary otherwise, and null when nothing enumerated at all — which hands
        /// user32 back its own default and keeps a machine whose adapters cannot be listed working
        /// exactly as it did before any of this existed.
        /// </summary>
        private string? EffectiveDevice(string? deviceName)
        {
            try
            {
                var attached = _display.EnumerateAttachedDisplays();
                if (attached.Count == 0)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(deviceName))
                {
                    var match = attached.FirstOrDefault(
                        a => string.Equals(a.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        return match.DeviceName;
                    }

                    _logger.LogWarning("Display device {Device} is not attached — falling back to the primary display", deviceName);
                }

                var primary = attached.FirstOrDefault(a => a.IsPrimary) ?? attached[0];
                return primary.DeviceName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve display device {Device}", deviceName);
                return null;
            }
        }

        /// <summary>
        /// "Monitor 2" is read off <c>\\.\DISPLAY2</c> rather than counted, so a label keeps
        /// naming the same screen after a monitor earlier in the list is unplugged. A device name
        /// that carries no number at all falls back to its position in the list.
        /// </summary>
        internal static int MonitorIndex(string deviceName, int ordinal)
        {
            var digits = new string((deviceName ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            return int.TryParse(digits, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : ordinal + 1;
        }

        // ────────────────────────────────────────────────────────────────────
        // Enumeration
        // ────────────────────────────────────────────────────────────────────

        public DisplayResolution GetCurrentResolution(string? deviceName = null)
        {
            try
            {
                var mode = _display.GetCurrentMode(EffectiveDevice(deviceName));
                if (mode is { } m)
                {
                    return new DisplayResolution(m.Width, m.Height, m.RefreshHz);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read current display resolution");
            }

            return new DisplayResolution(0, 0, 0);
        }

        public DisplayResolution GetNativeResolution(string? deviceName = null)
        {
            var modes = GetAvailableModes(deviceName);
            if (modes.Count == 0)
            {
                return GetCurrentResolution(deviceName);
            }

            // The native panel resolution is the largest reported mode.
            return modes.OrderByDescending(m => (long)m.Width * m.Height).First();
        }

        public IReadOnlyList<DisplayResolution> GetAvailableModes(string? deviceName = null)
        {
            var best = new Dictionary<(int, int), int>();
            try
            {
                foreach (var mode in _display.GetSupportedModes(EffectiveDevice(deviceName)))
                {
                    // Skip 4/8/16-bit legacy modes; keep the highest refresh per size.
                    if (mode.BitsPerPel >= 32 && mode.Width > 0 && mode.Height > 0)
                    {
                        var key = (mode.Width, mode.Height);
                        if (!best.TryGetValue(key, out var existing) || mode.RefreshHz > existing)
                        {
                            best[key] = mode.RefreshHz;
                        }
                    }
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

        public IReadOnlyList<StretchedPreset> GetPresets(string? deviceName = null)
            => BuildPresets(GetNativeResolution(deviceName), GetAvailableModes(deviceName));

        /// <summary>
        /// The classic presets were picked for 1080p panels; on a taller one every one of them is
        /// scaled on both axes. A size at the panel's own height is only scaled sideways, the
        /// sharpest a stretch gets, so 4:3 and 5:4 at the native height lead the list — only when
        /// the driver lists them, because an unlisted mode is rejected on apply. The classics stay
        /// as they were: a rejected one explains how to create the mode in the GPU's control panel.
        /// </summary>
        internal static IReadOnlyList<StretchedPreset> BuildPresets(
            DisplayResolution native, IEnumerable<DisplayResolution> modes)
        {
            var listed = modes.Select(m => (m.Width, m.Height)).ToHashSet();
            var h = native.Height;
            bool KeepsHeight(int width, int height) => height == h && width < native.Width;

            var own = new[] { h * 4 / 3, h * 5 / 4 }
                .Where(w => KeepsHeight(w, h) && listed.Contains((w, h))
                            && !ClassicPresets.Any(c => c.Width == w && c.Height == h))
                .Select(w => new StretchedPreset
                {
                    Name = $"{w} × {h}",
                    Width = w,
                    Height = h,
                    AspectLabel = DescribeAspect(w, h),
                    Note = "Native height, sharpest stretch",
                    KeepsNativeHeight = true
                });

            return own
                .Concat(ClassicPresets.Select(c => c with { KeepsNativeHeight = KeepsHeight(c.Width, c.Height) }))
                .ToList();
        }

        public GpuInfo GetGpuInfo(string? deviceName = null)
        {
            try
            {
                var attached = _display.EnumerateAttachedDisplays();
                if (attached.Count > 0)
                {
                    var effective = EffectiveDevice(deviceName);
                    var adapter = attached.FirstOrDefault(
                        a => string.Equals(a.DeviceName, effective, StringComparison.OrdinalIgnoreCase))
                        ?? attached.FirstOrDefault(a => a.IsPrimary)
                        ?? attached[0];

                    var name = adapter.AdapterName ?? string.Empty;
                    return new GpuInfo(ClassifyVendor(name), name.Trim());
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
            // The same two keys the page's own inline validation renders: one wording, so the
            // field's error and the refusal a caller gets back cannot drift apart.
            if (width < MinDimension || height < MinDimension)
                return DisplayChangeResult.Fail(_localizer.T("stretchedres.custom.error.min", MinDimension));
            if (width > MaxWidth || height > MaxHeight)
                return DisplayChangeResult.Fail(_localizer.T("stretchedres.custom.error.max", MaxWidth, MaxHeight));
            return DisplayChangeResult.Ok();
        }

        // ────────────────────────────────────────────────────────────────────
        // Apply / confirm / revert
        // ────────────────────────────────────────────────────────────────────

        public DisplayChangeResult ApplyResolution(int width, int height, string? deviceName = null)
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
                    return DisplayChangeResult.Fail(_localizer.T("stretchedres.service.pending"));
                }
            }

            try
            {
                var target = ResolveMonitor(deviceName);
                var device = target.DeviceName ?? EffectiveDevice(deviceName);

                // Capture the current mode as the exact revert target BEFORE changing anything.
                var current = _display.GetCurrentMode(device);
                if (current is not { } previous)
                {
                    return DisplayChangeResult.Fail(_localizer.T("stretchedres.service.readfailed"));
                }

                // Only the pixel dimensions change; refresh rate and colour depth are carried over.
                var mode = previous with { Width = width, Height = height };

                // Test first so an unsupported mode fails cleanly without ever switching.
                var test = _display.ChangeMode(device, mode, test: true);
                if (test != DisplayChangeCodes.Successful)
                {
                    return DisplayChangeResult.Fail(
                        DescribeMode(_localizer, test, width, height, GetGpuInfo(device).Vendor));
                }

                var apply = _display.ChangeMode(device, mode, test: false);
                if (apply != DisplayChangeCodes.Successful)
                {
                    return DisplayChangeResult.Fail(
                        DescribeMode(_localizer, apply, width, height, GetGpuInfo(device).Vendor));
                }

                lock (_gate)
                {
                    _previousMode = previous;
                    _hasPrevious = true;
                    _previousDevice = device;
                    _previousResolution = new DisplayResolution(previous.Width, previous.Height, previous.RefreshHz);
                    _pendingResolution = new DisplayResolution(width, height, mode.RefreshHz);
                    _pendingDevice = device;
                    // Survives ConfirmKeep on purpose: after the confirmation card is gone, this
                    // is the only record of which screen is the stretched one.
                    RememberApplied(device);
                    _isPending = true;
                    _secondsRemaining = AutoRevertSeconds;
                    StartRevertTimer();
                }

                // The screen's name is built here rather than read off DisplayMonitor.Label: that
                // property is the English one the log lines take, and this sentence is the
                // reader's.
                var where = MonitorLabel(target.Monitor) ?? device ?? _localizer.T("stretchedres.monitor.primary");
                _logger.LogInformation(
                    "Applied stretched resolution {W}x{H} on {Device} (temporary, auto-revert in {S}s)",
                    width, height, device ?? "primary", AutoRevertSeconds);
                _activity.AddActivity(
                    _localizer.T("stretchedres.activity.applied", width, height, where),
                    "warning",
                    "stretchedres.activity.applied");
                RaiseStateChanged();
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply resolution {W}x{H}", width, height);
                return DisplayChangeResult.Fail(_localizer.T("stretchedres.service.applyfailed", ex.Message));
            }
        }

        // Both call sites already hold _gate.
        private void RememberApplied(string? device)
        {
            if (string.IsNullOrEmpty(device))
            {
                return;
            }

            ForgetApplied(device);
            _appliedDevices.Add(device);
        }

        private void ForgetApplied(string? device)
        {
            if (string.IsNullOrEmpty(device))
            {
                return;
            }

            _appliedDevices.RemoveAll(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase));
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
                // Nothing is pending any more, so neither is the device: the record of which
                // screen was changed lives on in _appliedDevices, where a keep cannot stale it.
                _pendingResolution = null;
                _pendingDevice = null;
            }

            _logger.LogInformation("User kept stretched resolution {Res}", kept?.Label);
            if (kept != null)
            {
                _notifications.ShowSuccess(_localizer.T("stretchedres.toast.kept", kept.Label));
                _activity.AddActivity(
                    _localizer.T("stretchedres.activity.kept", kept.Label),
                    "success",
                    "stretchedres.activity.kept");
            }
            RaiseStateChanged();
        }

        public DisplayChangeResult RevertNow()
        {
            var result = RevertInternal("manual");
            if (result.Success)
            {
                _notifications.ShowInfo(_localizer.T("stretchedres.toast.reverted"));
                _activity.AddActivity(
                    _localizer.T("stretchedres.activity.reverted"),
                    "info",
                    "stretchedres.activity.reverted");
            }
            else if (result.Error != null)
            {
                _notifications.ShowError(result.Error);
            }
            RaiseStateChanged();
            return result;
        }

        public DisplayChangeResult RestoreNative(string? deviceName = null)
        {
            // Cancel any pending confirmation first so its timer cannot fire mid-restore.
            string? pending;
            string? lastApplied;
            lock (_gate)
            {
                StopRevertTimer();
                pending = _pendingDevice;
                lastApplied = _appliedDevices.Count == 0 ? null : _appliedDevices[^1];
                _isPending = false;
                _secondsRemaining = 0;
                _pendingResolution = null;
            }

            // A restore with nothing asked for goes to the screen that was just changed, if any —
            // pressing it while a change is pending must not leave that screen where it is, and
            // after the confirmation is gone the kept screen is still the one that is wrong.
            var device = EffectiveDevice(deviceName ?? pending ?? lastApplied);

            try
            {
                var result = _display.ResetToRegistryMode(device);
                if (result != DisplayChangeCodes.Successful)
                {
                    var msg = DescribeResult(_localizer, result);
                    _logger.LogError("RestoreNative failed: {Msg}", msg);
                    _notifications.ShowError(_localizer.T("stretchedres.service.restorefailed", msg));
                    RaiseStateChanged();
                    return DisplayChangeResult.Fail(msg);
                }

                lock (_gate)
                {
                    _hasPrevious = false;
                    _previousResolution = null;
                    _previousDevice = null;
                    _pendingDevice = null;
                    // Only this screen went back to normal; another one may still be stretched.
                    ForgetApplied(device);
                }

                var now = GetCurrentResolution(device);
                _logger.LogInformation("Restored native/normal desktop resolution ({Res})", now.Label);
                _notifications.ShowSuccess(_localizer.T("stretchedres.toast.restored", now.Label));
                _activity.AddActivity(
                    _localizer.T("stretchedres.activity.restored", now.Label),
                    "success",
                    "stretchedres.activity.restored");
                RaiseStateChanged();
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RestoreNative threw");
                _notifications.ShowError(_localizer.T("stretchedres.service.restorefailed", ex.Message));
                RaiseStateChanged();
                return DisplayChangeResult.Fail(ex.Message);
            }
        }

        // Restores _previousMode on the device it was captured from. Caller decides on notifications.
        private DisplayChangeResult RevertInternal(string reason)
        {
            DisplayMode previous;
            string? device;
            bool hasPrev;
            lock (_gate)
            {
                StopRevertTimer();
                hasPrev = _hasPrevious;
                previous = _previousMode;
                device = _previousDevice;
                _isPending = false;
                _secondsRemaining = 0;
            }

            if (!hasPrev)
            {
                return DisplayChangeResult.Fail(_localizer.T("stretchedres.service.noprevious"));
            }

            try
            {
                var result = _display.ChangeMode(device, previous, test: false);
                if (result != DisplayChangeCodes.Successful)
                {
                    // Last resort: reset to the registry-persisted mode.
                    var reset = _display.ResetToRegistryMode(device);
                    if (reset != DisplayChangeCodes.Successful)
                    {
                        var msg = DescribeResult(_localizer, result);
                        _logger.LogError("Revert ({Reason}) failed: {Msg}", reason, msg);
                        return DisplayChangeResult.Fail(msg);
                    }
                }

                lock (_gate)
                {
                    _pendingResolution = null;
                    _pendingDevice = null;
                    ForgetApplied(device);
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

        /// <summary>
        /// One second of the confirmation countdown.
        ///
        /// A <see cref="System.Threading.Timer"/> callback has no caller and no
        /// SynchronizationContext to hand a fault to: anything escaping this method is rethrown
        /// on the pool thread as an UNHANDLED exception and takes the process down with it — the
        /// user loses the app mid-session and the timer never ticks again, so the resolution they
        /// were about to confirm is never reverted either. The body is not obviously safe: the
        /// notification container and the activity log are both shared state written from the
        /// dispatcher, and RaiseStateChanged invokes whatever subscribers a page installed. So the
        /// whole tick owns its faults, exactly like HudOverlayService.OnTick and the guarded
        /// AccessGate/License ticks do.
        /// </summary>
        private void OnRevertTick(object? _)
        {
            try
            {
                RevertTick();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stretched-res revert tick failed");
            }
        }

        private void RevertTick()
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
                    _notifications.ShowWarning(_localizer.T("stretchedres.toast.autoreverted"));
                    _activity.AddActivity(
                        _localizer.T("stretchedres.activity.autoreverted"),
                        "warning",
                        "stretchedres.activity.autoreverted");
                }
                else if (result.Error != null)
                {
                    _notifications.ShowError(_localizer.T("stretchedres.service.autorevertfailed", result.Error));
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
        // Persistence (last chosen preset + target monitor — never the applied state)
        // ────────────────────────────────────────────────────────────────────

        public void SaveLastChoice(int width, int height, bool isCustom)
        {
            try
            {
                _preferences.Set(PrefWidth, width);
                _preferences.Set(PrefHeight, height);
                _preferences.Set(PrefIsCustom, isCustom);
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
                var w = _preferences.Get(PrefWidth, 0);
                var h = _preferences.Get(PrefHeight, 0);
                if (w <= 0 || h <= 0)
                {
                    return null;
                }

                var isCustom = _preferences.Get(PrefIsCustom, false);
                return (w, h, isCustom);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load last stretched-res choice");
                return null;
            }
        }

        public void SaveDeviceChoice(ResolutionFeature feature, string? deviceName)
        {
            try
            {
                // Empty and "the primary display" are the same answer, and storing the empty string
                // keeps a cleared picker from reading as "never chose one" on the next load.
                _preferences.Set(DeviceKey(feature), deviceName ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist the {Feature} monitor choice", feature);
            }
        }

        public string? LoadDeviceChoice(ResolutionFeature feature)
        {
            try
            {
                var stored = _preferences.Get(DeviceKey(feature), string.Empty);
                return string.IsNullOrWhiteSpace(stored) ? null : stored;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load the {Feature} monitor choice", feature);
                return null;
            }
        }

        private static string DeviceKey(ResolutionFeature feature)
            => feature == ResolutionFeature.Custom ? PrefCustomDevice : PrefStretchedDevice;

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
                    return DisplayChangeResult.Fail(_localizer.T("stretchedres.service.arkmissing"));
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
                    // GameIniService's own Error arrives already worded for the reader.
                    return DisplayChangeResult.Fail(
                        result.Error ?? _localizer.T("stretchedres.service.arkwritefailed"));
                }

                _logger.LogInformation("Wrote ARK resolution {W}x{H} to GameUserSettings.ini (backup: {Backup})", width, height, result.BackupPath ?? "none");
                _activity.AddActivity(
                    _localizer.T("stretchedres.activity.arkwritten", width, height),
                    "info",
                    "stretchedres.activity.arkwritten");
                return DisplayChangeResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write ARK resolution {W}x{H}", width, height);
                return DisplayChangeResult.Fail(_localizer.T("stretchedres.ark.toast.failed", ex.Message));
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

        /// <summary>
        /// The screen a message names, in the reader's language — the same two keys the page's own
        /// picker renders, so the activity line and the dropdown agree on what "monitor 2" is
        /// called. Null when nothing was resolved, which is the caller's cue to fall back.
        /// </summary>
        private string? MonitorLabel(DisplayMonitor? monitor)
            => monitor is null
                ? null
                : _localizer.T(
                    monitor.IsPrimary ? "stretchedres.monitor.option.primary" : "stretchedres.monitor.option",
                    monitor.Index, monitor.CurrentMode.Width, monitor.CurrentMode.Height);

        /// <remarks>
        /// Static with the localizer handed in rather than an instance method: the vendor guidance
        /// is asserted on its own, and building a display service to read one sentence would make
        /// that test about everything except the sentence.
        /// </remarks>
        internal static string DescribeMode(ILocalizer localizer, int code, int width, int height, GpuVendor vendor)
        {
            if (code == DisplayChangeCodes.BadMode)
            {
                return localizer.T(
                    "stretchedres.driver.rejected",
                    width, height, DescribeCustomResolutionPath(localizer, vendor));
            }
            return DescribeResult(localizer, code);
        }

        /// <summary>
        /// Where a custom resolution is created, per GPU vendor. The single source of truth for
        /// this wording so the driver-rejection message never drifts from the on-page guidance.
        /// </summary>
        internal static string DescribeCustomResolutionPath(ILocalizer localizer, GpuVendor vendor)
            => localizer.T(vendor switch
            {
                GpuVendor.Nvidia => "stretchedres.driver.path.nvidia",
                GpuVendor.Amd => "stretchedres.driver.path.amd",
                GpuVendor.Intel => "stretchedres.driver.path.intel",
                _ => "stretchedres.driver.path.none"
            });

        private static string DescribeResult(ILocalizer localizer, int code) => code switch
        {
            DisplayChangeCodes.Successful => localizer.T("stretchedres.result.success"),
            DisplayChangeCodes.Restart => localizer.T("stretchedres.result.restart"),
            DisplayChangeCodes.BadMode => localizer.T("stretchedres.result.badmode"),
            DisplayChangeCodes.Failed => localizer.T("stretchedres.result.failed"),
            DisplayChangeCodes.BadFlags => localizer.T("stretchedres.result.badflags"),
            DisplayChangeCodes.BadParam => localizer.T("stretchedres.result.badparam"),
            DisplayChangeCodes.NotUpdated => localizer.T("stretchedres.result.notupdated"),
            DisplayChangeCodes.BadDualView => localizer.T("stretchedres.result.baddualview"),
            _ => localizer.T("stretchedres.result.unknown", code)
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

        private static readonly IReadOnlyList<StretchedPreset> ClassicPresets = new List<StretchedPreset>
        {
            new() { Name = "1440 × 1080", Width = 1440, Height = 1080, AspectLabel = "4:3",  Note = "Popular wide-model stretch" },
            new() { Name = "1280 × 1024", Width = 1280, Height = 1024, AspectLabel = "5:4",  Note = "Classic 5:4 hitbox stretch" },
            new() { Name = "1024 × 768",  Width = 1024, Height = 768,  AspectLabel = "4:3",  Note = "Maximum model width" },
            new() { Name = "1600 × 1080", Width = 1600, Height = 1080, AspectLabel = "40:27", Note = "Mild stretch" },
            new() { Name = "1280 × 960",  Width = 1280, Height = 960,  AspectLabel = "4:3",  Note = "Lighter 4:3 option" }
        };
    }
}
