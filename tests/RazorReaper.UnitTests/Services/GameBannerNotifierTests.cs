using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Services;

/// <summary>
/// Warnings and errors are repeated over ARK while the game has focus — the only place a player
/// sees why a script stopped. Everything else stays in the app.
/// </summary>
public sealed class GameBannerNotifierTests
{
    private sealed class RecordingBanner : IGameBanner
    {
        public List<(string Message, Rectangle Area, int DurationMs)> Shown { get; } = [];
        public void ShowBanner(string message, Rectangle area, int durationMs) => Shown.Add((message, area, durationMs));
    }

    private readonly NotificationService _toasts = new();
    private readonly FakeForegroundGate _foreground = new(gameIsForeground: true);
    private readonly FakeGameDisplayService _displays = new() { GameClientBounds = new Rectangle(1920, 0, 2560, 1440) };
    private readonly FakePreferencesStore _prefs = new();
    private readonly RecordingBanner _banner = new();
    private long _now = 1_000_000;
    private readonly GameBannerNotifier _notifier;

    public GameBannerNotifierTests()
    {
        _notifier = new GameBannerNotifier(_toasts, _foreground, _displays, _prefs, _banner,
            NullLogger<GameBannerNotifier>.Instance, () => _now);
    }

    [Fact]
    public void WarningsAndErrorsReachTheGameWithTheirOwnDurationOverArksPicture()
    {
        _toasts.ShowWarning("Fed Suit stopped: no transmitter", 4500);
        _toasts.ShowWarningWithCountdown("Closing soon", 8000);
        _toasts.ShowError("Calibration missing", 5000);

        Assert.Equal(
            [
                ("Fed Suit stopped: no transmitter", new Rectangle(1920, 0, 2560, 1440), 4500),
                ("Closing soon", new Rectangle(1920, 0, 2560, 1440), 8000),
                ("Calibration missing", new Rectangle(1920, 0, 2560, 1440), 5000),
            ],
            _banner.Shown);
    }

    [Fact]
    public void SuccessAndInfoNeverDo()
    {
        _toasts.ShowSuccess("Saved");
        _toasts.ShowInfo("Heads up");

        Assert.Empty(_banner.Shown);
    }

    [Fact]
    public void NothingIsDrawnWhileArkIsNotInFront()
    {
        // The client (or anything else) in front: the in-app toast is enough.
        _foreground.GameIsForeground = false;

        _toasts.ShowError("Calibration missing");

        Assert.Empty(_banner.Shown);
    }

    [Fact]
    public void TheSettingIsOnByDefaultAndTurningItOffSilencesTheBanner()
    {
        Assert.True(_notifier.Enabled);

        _notifier.Enabled = false;
        _toasts.ShowError("Calibration missing");

        Assert.False(_notifier.Enabled);
        Assert.Empty(_banner.Shown);
    }

    [Fact]
    public void TheSameMessageShowsOnceInTenSeconds()
    {
        _toasts.ShowWarning("Script stopped");
        _now += 9_999;
        _toasts.ShowWarning("Script stopped");
        Assert.Single(_banner.Shown);

        _now += 1;
        _toasts.ShowWarning("Script stopped");
        Assert.Equal(2, _banner.Shown.Count);
    }

    [Fact]
    public void ADifferentMessageReplacesTheOneOnScreenButDoesNotResetTheRepeatWindow()
    {
        _toasts.ShowWarning("A");
        _toasts.ShowWarning("B");
        _toasts.ShowWarning("A");

        Assert.Equal(["A", "B"], _banner.Shown.Select(s => s.Message));
    }

    [Fact]
    public void WithoutAGameWindowTheMonitorArkIsOnIsUsed()
    {
        _displays.GameClientBounds = Rectangle.Empty;
        _displays.GameWindowBounds = Rectangle.Empty;

        _toasts.ShowError("Calibration missing");

        Assert.Equal(new Rectangle(0, 0, 1920, 1080), Assert.Single(_banner.Shown).Area);
    }

    [Fact]
    public void ABannerThatThrowsNeverReachesTheCaller()
    {
        var notifier = new GameBannerNotifier(_toasts, _foreground, _displays, _prefs, new ThrowingBanner(),
            NullLogger<GameBannerNotifier>.Instance, () => _now);

        var ex = Record.Exception(() => _toasts.ShowError("Calibration missing"));

        Assert.Null(ex);
        GC.KeepAlive(notifier);
    }

    private sealed class ThrowingBanner : IGameBanner
    {
        public void ShowBanner(string message, Rectangle area, int durationMs) => throw new InvalidOperationException("no window");
    }
}
