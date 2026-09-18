using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Models;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests;

/// <summary>
/// Which screen a resolution change lands on.
///
/// Before this, every call in the service passed <c>null</c> as the display-device name, which
/// user32 reads as "the display this thread is on" — the primary, always. On a two-monitor desk
/// that is a feature with a silent, unfixable default: the stretched mode the user set up for
/// the second screen went to the first one, and nothing in the UI ever said so.
///
/// So the device name is now carried end to end, and these are the three things that has to
/// mean. It arrives: an apply aimed at DISPLAY2 is handed to DISPLAY2 and the revert goes back
/// to the same screen rather than the primary. It survives a restart: the two pickers store
/// their monitor apart, so a stretched preset on the game screen and a custom mode on the
/// second one both come back where they were left. And it fails visibly: a monitor that is not
/// plugged in any more falls back to the primary and says it did, because the alternative is
/// changing a different screen than the label names.
/// </summary>
public sealed class StretchedResMonitorTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";
    private const string Display3 = @"\\.\DISPLAY3";

    // ---- The list the page shows --------------------------------------------

    [Fact]
    public void MonitorsAreListedWithTheirNumberSizeAndPrimaryFlag()
    {
        using var service = Build(TwoMonitors());

        var monitors = service.GetMonitors();

        Assert.Equal(2, monitors.Count);
        Assert.Equal("Monitor 1 — 2560×1440 (primary)", monitors[0].Label);
        Assert.Equal("Monitor 2 — 1920×1080", monitors[1].Label);
        Assert.True(monitors[0].IsPrimary);
        Assert.False(monitors[1].IsPrimary);
    }

    /// <summary>
    /// The primary heads the list wherever the adapter enumeration puts it: it is the default
    /// target, and a picker that opens on some other screen reads as though the app chose one.
    /// </summary>
    [Fact]
    public void ThePrimaryMonitorIsListedFirstEvenWhenItEnumeratesSecond()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display1, 1920, 1080)
            .WithMonitor(Display2, 2560, 1440, isPrimary: true);
        using var service = Build(api);

        var monitors = service.GetMonitors();

        Assert.Equal(Display2, monitors[0].DeviceName);
        Assert.True(monitors[0].IsPrimary);
    }

    /// <summary>
    /// The number in the label is read off the device name, not counted. Unplug DISPLAY1 and the
    /// screen the user knows as monitor 2 has to keep saying 2 — renumbering it to 1 would move
    /// every label one screen to the left while the stored preference stayed put.
    /// </summary>
    [Fact]
    public void TheMonitorNumberComesFromTheDeviceNameNotItsPositionInTheList()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display2, 1920, 1080, isPrimary: true)
            .WithMonitor(Display3, 3840, 2160);
        using var service = Build(api);

        var monitors = service.GetMonitors();

        Assert.Equal(2, monitors[0].Index);
        Assert.Equal(3, monitors[1].Index);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 0, 1)]
    [InlineData(@"\\.\DISPLAY4", 2, 4)]
    [InlineData(@"\\.\DISPLAY12", 0, 12)]
    [InlineData("", 0, 1)]
    [InlineData("HEADLESS", 2, 3)]
    public void AnUnnumberedDeviceNameFallsBackToItsPositionInTheList(string deviceName, int ordinal, int expected)
        => Assert.Equal(expected, StretchedResService.MonitorIndex(deviceName, ordinal));

    // ---- Which device every query is made against ----------------------------

    [Fact]
    public void AModeQueryWithNoDeviceReadsThePrimaryDisplay()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display1, 1920, 1080)
            .WithMonitor(Display2, 2560, 1440, isPrimary: true);
        using var service = Build(api);

        Assert.Equal("2560 × 1440", service.GetCurrentResolution().Label);
    }

    [Fact]
    public void AModeQueryWithADeviceReadsThatDisplay()
    {
        using var service = Build(TwoMonitors());

        Assert.Equal("1920 × 1080", service.GetCurrentResolution(Display2).Label);
        Assert.Equal("2560 × 1440", service.GetCurrentResolution(Display1).Label);
    }

    [Fact]
    public void TheNativeResolutionIsTheLargestModeOfTheChosenDisplay()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display1, 2560, 1440, isPrimary: true)
            .WithMonitor(Display2, 1280, 1024, supported:
            [
                new DisplayMode(1920, 1080, 60, 32),
                new DisplayMode(1280, 1024, 75, 32),
            ]);
        using var service = Build(api);

        Assert.Equal("1920 × 1080", service.GetNativeResolution(Display2).Label);
    }

    [Fact]
    public void TheGpuIsReadFromTheAdapterDrivingTheChosenDisplay()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display1, 2560, 1440, isPrimary: true, adapterName: "NVIDIA GeForce RTX 4080")
            .WithMonitor(Display2, 1920, 1080, adapterName: "Intel(R) UHD Graphics 770");
        using var service = Build(api);

        Assert.Equal(GpuVendor.Nvidia, service.GetGpuInfo().Vendor);
        Assert.Equal(GpuVendor.Intel, service.GetGpuInfo(Display2).Vendor);
    }

    // ---- Which device an apply lands on --------------------------------------

    [Fact]
    public void AnApplyWithNoDeviceGoesToThePrimaryDisplay()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display1, 1920, 1080)
            .WithMonitor(Display2, 2560, 1440, isPrimary: true);
        using var service = Build(api);

        Assert.True(service.ApplyResolution(1440, 1080).Success);

        Assert.All(api.Changes, change => Assert.Equal(Display2, change.Device));
    }

    [Fact]
    public void AnApplyAimedAtTheSecondMonitorIsTestedAndAppliedOnTheSecondMonitor()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        Assert.True(service.ApplyResolution(1440, 1080, Display2).Success);

        Assert.Collection(
            api.Changes,
            test =>
            {
                Assert.Equal(Display2, test.Device);
                Assert.True(test.Test);
            },
            apply =>
            {
                Assert.Equal(Display2, apply.Device);
                Assert.False(apply.Test);
                Assert.Equal(1440, apply.Mode.Width);
                Assert.Equal(1080, apply.Mode.Height);
            });

        Assert.Equal(Display2, service.PendingDeviceName);
        Assert.Equal("1920 × 1080", service.PreviousResolution!.Label);
    }

    /// <summary>The refresh rate and colour depth of the screen being changed are carried over.</summary>
    [Fact]
    public void AnApplyKeepsTheChosenDisplaysRefreshRate()
    {
        var api = new FakeDisplayApi()
            .WithMonitor(Display1, 2560, 1440, isPrimary: true, refreshHz: 240)
            .WithMonitor(Display2, 1920, 1080, refreshHz: 60);
        using var service = Build(api);

        service.ApplyResolution(1440, 1080, Display2);

        var apply = api.Changes.Last();
        Assert.Equal(60, apply.Mode.RefreshHz);
        Assert.Equal(32, apply.Mode.BitsPerPel);
    }

    /// <summary>
    /// The revert target is the screen that was changed. Sending it to the primary instead would
    /// leave the second monitor stretched forever and resize a screen nobody touched.
    /// </summary>
    [Fact]
    public void RevertingGoesBackToTheDisplayTheChangeWasAppliedTo()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        service.ApplyResolution(1440, 1080, Display2);
        Assert.True(service.RevertNow().Success);

        var revert = api.Changes.Last();
        Assert.Equal(Display2, revert.Device);
        Assert.Equal(1920, revert.Mode.Width);
        Assert.Equal(1080, revert.Mode.Height);
    }

    /// <summary>
    /// "Restore native" pressed during the countdown means the screen that is currently wrong,
    /// not the primary — that is the button someone reaches for when the second monitor is
    /// unreadable.
    /// </summary>
    [Fact]
    public void RestoreNativeDuringAPendingChangeResetsTheDisplayThatWasChanged()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        service.ApplyResolution(1440, 1080, Display2);
        Assert.True(service.RestoreNative().Success);

        Assert.Equal(Display2, Assert.Single(api.Resets));
        Assert.False(service.IsPendingConfirmation);
    }

    // ---- Which screen is actually the stretched one --------------------------

    /// <summary>
    /// <see cref="IStretchedResService.PendingDeviceName"/> is gone the moment the user presses
    /// Keep, and with it the only record of which screen was changed. The page needs that record
    /// afterwards, not during: the status card and the single "Restore native" button both have to
    /// keep pointing at the stretched screen once the confirmation card has disappeared.
    /// </summary>
    [Fact]
    public void TheScreenAResolutionWasAppliedToOutlivesTheConfirmation()
    {
        using var service = Build(TwoMonitors());

        Assert.Null(service.LastAppliedDeviceName);

        service.ApplyResolution(1440, 1080, Display2);
        service.ConfirmKeep();

        Assert.Null(service.PendingDeviceName);
        Assert.Equal(Display2, service.LastAppliedDeviceName);
    }

    /// <summary>
    /// Both sections of the page can have a mode on a different screen at the same time — a preset
    /// on the game monitor, a custom mode on the second one. Taking one of them back leaves the
    /// other one stretched, so it is the other one the page then has to describe.
    /// </summary>
    [Fact]
    public void TakingOneScreenBackHandsTheTitleToTheOtherStretchedScreen()
    {
        using var service = Build(TwoMonitors());

        service.ApplyResolution(1440, 1080, Display2);
        service.ConfirmKeep();
        service.ApplyResolution(1280, 1024, Display1);
        Assert.Equal(Display1, service.LastAppliedDeviceName);

        service.RevertNow();

        Assert.Equal(Display2, service.LastAppliedDeviceName);
    }

    [Fact]
    public void RestoringTheStretchedScreenLeavesNoneBehind()
    {
        using var service = Build(TwoMonitors());

        service.ApplyResolution(1440, 1080, Display2);
        service.ConfirmKeep();
        Assert.True(service.RestoreNative(Display2).Success);

        Assert.Null(service.LastAppliedDeviceName);
    }

    /// <summary>
    /// The same defence the pending case already had, one step later: after Keep there is no
    /// pending device left, and a restore with nothing asked for used to land on the primary while
    /// the second screen stayed stretched.
    /// </summary>
    [Fact]
    public void RestoreNativeWithNoDeviceGoesToTheKeptScreenNotThePrimary()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        service.ApplyResolution(1440, 1080, Display2);
        service.ConfirmKeep();
        Assert.True(service.RestoreNative().Success);

        Assert.Equal(Display2, Assert.Single(api.Resets));
    }

    // ---- The monitor that is not there any more ------------------------------

    [Fact]
    public void AStoredMonitorThatIsStillAttachedResolvesToItselfWithoutFallingBack()
    {
        using var service = Build(TwoMonitors());

        var target = service.ResolveMonitor(Display2);

        Assert.Equal(Display2, target.DeviceName);
        Assert.False(target.FellBackToPrimary);
    }

    /// <summary>Asking for nothing is not a fallback — the primary is the documented default.</summary>
    [Fact]
    public void NoStoredMonitorResolvesToThePrimaryWithoutReportingAFallback()
    {
        using var service = Build(TwoMonitors());

        var target = service.ResolveMonitor(null);

        Assert.Equal(Display1, target.DeviceName);
        Assert.False(target.FellBackToPrimary);
    }

    [Fact]
    public void AnUnpluggedMonitorResolvesToThePrimaryAndSaysSo()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        api.Unplug(Display2);
        var target = service.ResolveMonitor(Display2);

        Assert.Equal(Display1, target.DeviceName);
        Assert.True(target.FellBackToPrimary);
        Assert.True(target.Monitor!.IsPrimary);
    }

    /// <summary>
    /// The fallback is not only a label: the apply itself has to go somewhere real. A device name
    /// user32 does not know fails the call outright, so an unplugged preference would turn every
    /// preset into an error message until the user noticed the picker.
    /// </summary>
    [Fact]
    public void AnApplyAimedAtAnUnpluggedMonitorLandsOnThePrimary()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        api.Unplug(Display2);
        Assert.True(service.ApplyResolution(1440, 1080, Display2).Success);

        Assert.All(api.Changes, change => Assert.Equal(Display1, change.Device));
    }

    [Fact]
    public void AModeQueryForAnUnpluggedMonitorReadsThePrimary()
    {
        var api = TwoMonitors();
        using var service = Build(api);

        api.Unplug(Display2);

        Assert.Equal("2560 × 1440", service.GetCurrentResolution(Display2).Label);
    }

    /// <summary>
    /// A machine whose adapters cannot be enumerated at all still works exactly as the service
    /// did before monitors were selectable: null goes to user32, which uses its own default.
    /// </summary>
    [Fact]
    public void WithNoEnumerableDisplaysEveryCallFallsBackToTheWin32Default()
    {
        var api = new FakeDisplayApi();
        using var service = Build(api);

        Assert.Empty(service.GetMonitors());
        Assert.Null(service.ResolveMonitor(Display2).Monitor);
        Assert.Equal(new DisplayResolution(0, 0, 0), service.GetCurrentResolution());
    }

    // ---- The two pickers remember their own monitor ---------------------------

    [Fact]
    public void EachPickerStoresItsMonitorSeparatelyAndReadsItBack()
    {
        var preferences = new FakePreferencesStore();
        using var service = Build(TwoMonitors(), preferences);

        service.SaveDeviceChoice(ResolutionFeature.Stretched, Display1);
        service.SaveDeviceChoice(ResolutionFeature.Custom, Display2);

        Assert.Equal(Display1, service.LoadDeviceChoice(ResolutionFeature.Stretched));
        Assert.Equal(Display2, service.LoadDeviceChoice(ResolutionFeature.Custom));
    }

    [Fact]
    public void AMonitorChoiceSurvivesANewServiceOverTheSamePreferences()
    {
        var preferences = new FakePreferencesStore();
        using (var first = Build(TwoMonitors(), preferences))
        {
            first.SaveDeviceChoice(ResolutionFeature.Custom, Display2);
        }

        using var second = Build(TwoMonitors(), preferences);

        Assert.Equal(Display2, second.LoadDeviceChoice(ResolutionFeature.Custom));
        Assert.Null(second.LoadDeviceChoice(ResolutionFeature.Stretched));
    }

    /// <summary>Picking "the primary display" back clears the stored name rather than pinning it.</summary>
    [Fact]
    public void ClearingAPickerStoresNoDeviceAtAll()
    {
        var preferences = new FakePreferencesStore();
        using var service = Build(TwoMonitors(), preferences);

        service.SaveDeviceChoice(ResolutionFeature.Stretched, Display2);
        service.SaveDeviceChoice(ResolutionFeature.Stretched, null);

        Assert.Null(service.LoadDeviceChoice(ResolutionFeature.Stretched));
    }

    [Fact]
    public void NothingWasEverPickedReadsAsNull()
    {
        using var service = Build(TwoMonitors());

        Assert.Null(service.LoadDeviceChoice(ResolutionFeature.Stretched));
        Assert.Null(service.LoadDeviceChoice(ResolutionFeature.Custom));
    }

    /// <summary>
    /// The size choice is stored where it always was, so an install that has been picking
    /// resolutions since before monitors were selectable opens on the one it left.
    /// </summary>
    [Fact]
    public void TheLastSizeChoiceStillRoundTripsAlongsideTheMonitorChoice()
    {
        var preferences = new FakePreferencesStore();
        using var service = Build(TwoMonitors(), preferences);

        service.SaveLastChoice(1600, 1080, isCustom: true);
        service.SaveDeviceChoice(ResolutionFeature.Custom, Display2);

        var last = service.LoadLastChoice();
        Assert.Equal((1600, 1080, true), last);
        Assert.Equal(Display2, service.LoadDeviceChoice(ResolutionFeature.Custom));
    }

    // ---- Harness --------------------------------------------------------------

    private static FakeDisplayApi TwoMonitors() => new FakeDisplayApi()
        .WithMonitor(Display1, 2560, 1440, isPrimary: true)
        .WithMonitor(Display2, 1920, 1080);

    private static StretchedResService Build(IDisplayApi display, IPreferencesStore? preferences = null)
        => new(
            NullLogger<StretchedResService>.Instance,
            new StubNotificationService(),
            new StubActivityService(),
            new StubArkPathProvider(),
            new StubGameIniService(),
            display,
            preferences ?? new FakePreferencesStore());

    private sealed class StubNotificationService : INotificationService
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

    private sealed class StubActivityService : IActivityService
    {
        public event EventHandler<ActivityItem>? ActivityAdded { add { } remove { } }

        public void AddActivity(string title, string type = "info", string? key = null) { }
        public IReadOnlyList<ActivityItem> GetRecentActivities() => [];
        public void ClearActivities() { }
    }

    private sealed class StubArkPathProvider : IArkPathProvider
    {
        public string? FindArkPath() => null;
        public string? GetBaseDeviceProfilesPath() => null;
        public bool IsValidArkPath(string path) => false;
    }

    private sealed class StubGameIniService : IGameIniService
    {
        public IReadOnlyList<GameIniPreset> GetBuiltInPresets() => [];
        public string? GetIniPath(GameIniTarget target) => null;
        public bool IniFileExists(GameIniTarget target) => false;
        public bool IsArkRunning() => false;
        public Task<GameIniApplyResult> ApplyPresetAsync(GameIniPreset preset) => throw new NotSupportedException();
        public Task<GameIniApplyResult> ApplyEntriesAsync(GameIniTarget target, IReadOnlyList<GameIniEntry> entries)
            => throw new NotSupportedException();
        public List<GameIniBackup> ListBackups() => [];
        public Task<GameIniApplyResult> RestoreBackupAsync(GameIniBackup backup) => throw new NotSupportedException();
        public bool DeleteBackup(GameIniBackup backup) => false;
        public Task<GameIniDraft?> LoadDraftAsync() => Task.FromResult<GameIniDraft?>(null);
        public Task<bool> SaveDraftAsync(GameIniDraft draft) => Task.FromResult(false);
    }
}
