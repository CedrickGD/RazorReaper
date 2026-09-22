using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using RazorReaper.Services.Automation;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Implementations;

/// <summary>The native half of <see cref="GameBannerNotifier"/>: one banner drawn over the game.</summary>
public interface IGameBanner
{
    /// <summary>
    /// Shows <paramref name="message"/> at the top centre of <paramref name="area"/> (virtual-desktop
    /// pixels) for <paramref name="durationMs"/>, replacing whatever banner is on screen.
    /// </summary>
    void ShowBanner(string message, Rectangle area, int durationMs);
}

/// <summary>
/// Repeats every warning and error toast over ARK while the game has focus. A script that stops
/// mid-game otherwise says why only in the client window, which nobody sees while playing. Windows
/// notifications are no substitute: Windows 11 switches Do-Not-Disturb on by itself while a game
/// runs and swallows them.
/// </summary>
public sealed class GameBannerNotifier
{
    public const string PrefKey = "notifications.game_banner";

    /// <summary>The same text is shown at most once in this window — a looping script repeats itself.</summary>
    private const long RepeatWindowMs = 10_000;

    private readonly IForegroundGate _foreground;
    private readonly IGameDisplayService _displays;
    private readonly IPreferencesStore _prefs;
    private readonly IGameBanner _banner;
    private readonly ILogger<GameBannerNotifier> _logger;
    private readonly Func<long> _clock;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _shownAt = new(StringComparer.Ordinal);

    public GameBannerNotifier(
        INotificationService notifications,
        IForegroundGate foreground,
        IGameDisplayService displays,
        IPreferencesStore prefs,
        IGameBanner banner,
        ILogger<GameBannerNotifier> logger,
        Func<long>? clock = null)
    {
        _foreground = foreground;
        _displays = displays;
        _prefs = prefs;
        _banner = banner;
        _logger = logger;
        _clock = clock ?? (() => Environment.TickCount64);
        notifications.OnNotificationAdded += OnNotificationAdded;
    }

    public bool Enabled
    {
        get => _prefs.Get(PrefKey, true);
        set => _prefs.Set(PrefKey, value);
    }

    private void OnNotificationAdded(NotificationMessage notification)
    {
        try
        {
            if (notification.Type is not (NotificationType.Warning or NotificationType.Error)) return;
            // ARK in front already means the client is not: there is one foreground window.
            if (!Enabled || !_foreground.IsGameForeground()) return;

            var now = _clock();
            lock (_gate)
            {
                foreach (var stale in _shownAt.Where(e => now - e.Value >= RepeatWindowMs).Select(e => e.Key).ToList())
                    _shownAt.Remove(stale);
                if (!_shownAt.TryAdd(notification.Message, now)) return;
            }

            var area = _displays.GameClientBounds;
            if (area.IsEmpty) area = _displays.GameMonitor?.Bounds ?? Rectangle.Empty;
            if (area.IsEmpty) return;

            _banner.ShowBanner(notification.Message, area, notification.DurationMs);
        }
        catch (Exception ex)
        {
            // The in-app toast has already gone out; the banner is only its echo.
            _logger.LogDebug(ex, "Game banner skipped");
        }
    }
}
