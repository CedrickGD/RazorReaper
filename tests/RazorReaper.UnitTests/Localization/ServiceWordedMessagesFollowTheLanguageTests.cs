using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Models;
using RazorReaper.Services;
using RazorReaper.Services.FileModifier;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

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
}
