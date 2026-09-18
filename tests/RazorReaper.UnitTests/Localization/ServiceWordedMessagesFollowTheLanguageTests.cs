using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Models;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.FileModifier;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Point = System.Drawing.Point;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// A message a service words for a page, in two languages.
///
/// The scan next door proves a service that holds an <see cref="ILocalizer"/> hands no English
/// literal to a toast. It cannot prove the string actually changes when the language does, and
/// that is the half a reader notices: a service resolving its wording once — in a field, in a
/// constructor, in a cached record — passes every literal scan in the suite and still says the
/// same thing in a German window as it did in an English one.
///
/// So one service is driven for real. <see cref="FileModifierService"/> refuses an empty path
/// before it touches the disk, which makes that refusal the cheapest real message in the app to
/// ask for twice.
///
/// And one automation script, which is the same risk with nothing to inject into: the seventeen
/// scripts share the localizer <see cref="AutomationScriptBase"/> holds, and they are singletons
/// built at app start — before the language has been read back from preferences on a cold start,
/// and long before the picker is touched on a warm one. A refusal resolved in a constructor would
/// be right exactly once.
/// </summary>
public sealed class ServiceWordedMessagesFollowTheLanguageTests
{
    [Fact]
    public async Task TheFileModifierWordsTheSameRefusalInTheLanguageThatIsActive()
    {
        var localizer = new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));
        var service = Build(localizer);

        Assert.Equal("No file selected.", (await service.RemoveAsync("")).Message);

        localizer.SetLanguage(AppLanguages.German);
        Assert.Equal("Keine Datei ausgewählt.", (await service.RemoveAsync("")).Message);

        localizer.SetLanguage(AppLanguages.Russian);
        Assert.Equal("Файл не выбран.", (await service.RemoveAsync("")).Message);
    }

    /// <summary>
    /// And a second switch, back the other way: a service that reads the dictionary once and
    /// keeps the answer would pass the test above on its first two lines and fail here.
    /// </summary>
    [Fact]
    public async Task SwitchingBackRestoresTheEnglishWording()
    {
        var localizer = new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));
        var service = Build(localizer);

        localizer.SetLanguage(AppLanguages.SimplifiedChinese);
        var chinese = (await service.RemoveAsync("")).Message;

        localizer.SetLanguage(AppLanguages.English);
        var english = (await service.RemoveAsync("")).Message;

        Assert.NotEqual(chinese, english);
        Assert.Equal("No file selected.", english);
    }

    /// <summary>
    /// And the same proof one layer down, where there is no service at all. An automation script
    /// words its own refusals through the localizer <see cref="AutomationScriptBase"/> holds for
    /// all seventeen of them, so the thing under test is that the inherited localizer is read at
    /// the moment Start is pressed rather than once in a constructor that runs at app start — the
    /// scripts are singletons registered before anyone has picked a language.
    ///
    /// Astro is the cheapest one to ask: it refuses to start unless ARK is the foreground window,
    /// which needs neither a screen capture nor a run loop.
    /// </summary>
    [Fact]
    public void AScriptRefusingToStartSaysSoInTheLanguageThatIsActive()
    {
        var localizer = new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));
        var notifications = new RecordingNotifications();
        using var script = new AstroScript(
            new NoInput(),
            new ArkIsNotInFront(),
            new NoHotkeys(),
            notifications,
            new SilentActivity(),
            localizer,
            NullLogger<AstroScript>.Instance);

        Assert.False(script.Start());
        Assert.Equal("Focus ARK first — Astro fires its sequence immediately on start.", notifications.Last);

        localizer.SetLanguage(AppLanguages.German);
        Assert.False(script.Start());
        Assert.Equal("Fokussiere zuerst ARK — Astro startet seine Sequenz sofort.", notifications.Last);

        localizer.SetLanguage(AppLanguages.Russian);
        Assert.False(script.Start());
        Assert.Equal(
            "Сначала переключитесь в ARK — Astro запускает последовательность сразу после старта.",
            notifications.Last);

        // Back to English: a script that read the dictionary once would still be speaking Russian.
        localizer.SetLanguage(AppLanguages.English);
        Assert.False(script.Start());
        Assert.Equal("Focus ARK first — Astro fires its sequence immediately on start.", notifications.Last);
    }

    private static FileModifierService Build(ILocalizer localizer)
        => new(
            NullLogger<FileModifierService>.Instance,
            new NoArk(),
            new NoProcesses(),
            new SilentNotifications(),
            new SilentActivity(),
            localizer);

    // ---- Doubles ------------------------------------------------------------
    // The refusal under test happens before any of these is asked anything; they exist so the
    // constructor can run.

    private sealed class NoArk : IArkPathProvider
    {
        public string? FindArkPath() => null;

        public string? GetBaseDeviceProfilesPath() => null;

        public bool IsValidArkPath(string path) => false;
    }

    private sealed class NoProcesses : IProcessService
    {
        public Process[] GetProcessesByName(string processName) => [];

        public bool IsProcessRunning(string processName) => false;

        public string? GetExecutablePath(Process process) => null;

        public Process? Start(string filePath) => null;

        public void Kill(Process process) { }
    }

    private sealed class SilentNotifications : INotificationService
    {
        public event Action<NotificationMessage>? OnNotificationAdded { add { } remove { } }

        public event Action<string>? OnNotificationRemoved { add { } remove { } }

        public void ShowSuccess(string message, int durationMs = 3500) { }

        public void ShowError(string message, int durationMs = 5000) { }

        public void ShowWarning(string message, int durationMs = 4500) { }

        public void ShowWarningWithCountdown(string message, int durationMs) { }

        public void ShowInfo(string message, int durationMs = 3500) { }

        public void RemoveNotification(string id) { }
    }

    private sealed class SilentActivity : IActivityService
    {
        public event EventHandler<ActivityItem>? ActivityAdded { add { } remove { } }

        public void AddActivity(string title, string type = "info") { }

        public IReadOnlyList<ActivityItem> GetRecentActivities() => [];

        public void ClearActivities() { }
    }

    /// <summary>Keeps the last message so the refusal can be read back, once per language.</summary>
    private sealed class RecordingNotifications : INotificationService
    {
        public string? Last { get; private set; }

        public event Action<NotificationMessage>? OnNotificationAdded { add { } remove { } }

        public event Action<string>? OnNotificationRemoved { add { } remove { } }

        public void ShowSuccess(string message, int durationMs = 3500) => Last = message;

        public void ShowError(string message, int durationMs = 5000) => Last = message;

        public void ShowWarning(string message, int durationMs = 4500) => Last = message;

        public void ShowWarningWithCountdown(string message, int durationMs) => Last = message;

        public void ShowInfo(string message, int durationMs = 3500) => Last = message;

        public void RemoveNotification(string id) { }
    }

    /// <summary>The refusal happens before a key is ever sent, so nothing here is reached.</summary>
    private sealed class NoInput : IInputSimulator
    {
        public void KeyDown(int virtualKey) { }

        public void KeyUp(int virtualKey) { }

        public Task KeyPressAsync(int virtualKey, int holdMs = 40, double jitter = 0, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task TypeTextAsync(string text, int perCharDelayMs = 20, double jitter = 0, CancellationToken ct = default)
            => Task.CompletedTask;

        public Point GetCursorPosition() => Point.Empty;

        public void MoveTo(int x, int y) { }

        public void MoveBy(int dx, int dy) { }

        public void MouseDown(MouseButton button) { }

        public void MouseUp(MouseButton button) { }

        public Task ClickAsync(MouseButton button, Point? at = null, int holdMs = 30, double jitter = 0, CancellationToken ct = default)
            => Task.CompletedTask;

        public void Scroll(int detents) { }

        public Task DelayAsync(int delayMs, double jitter = 0, CancellationToken ct = default)
            => Task.CompletedTask;

        public int ApplyJitter(int delayMs, double jitter) => delayMs;
    }

    /// <summary>What makes Astro refuse.</summary>
    private sealed class ArkIsNotInFront : IForegroundGate
    {
        public bool IsGameForeground() => false;
    }

    /// <summary>Astro binds no hotkey by default, so nothing here is reached either.</summary>
    private sealed class NoHotkeys : IAutomationHotkeyService
    {
        public int RegisterHotkey(int virtualKey, bool ctrl, bool alt, bool shift, Action callback) => 0;

        public void UnregisterHotkey(int registrationId) { }

        public bool IsRegistered(int registrationId) => false;

        public void UnregisterAll() { }

        public void Dispose() { }
    }
}
