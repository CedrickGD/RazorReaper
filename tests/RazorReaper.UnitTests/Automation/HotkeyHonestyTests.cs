using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Models;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// Binding F12 to a script produced a toast — "it may be in use by another app" — and then a
/// field that still read F12, a tile that still read F12, a value that survived a restart, and a
/// key that did nothing in game. Three and a half seconds after the toast there was nothing left
/// on screen to say the binding was dead, and the silent retry on the next app start said less.
///
/// Two things were wrong. The state: a stored combo and a live registration are not the same
/// fact, and only one of them was ever shown. And the wording: F8 is RazorReaper's own crosshair
/// default, so "another app" was the app blaming a stranger for its own key.
/// </summary>
public sealed class HotkeyHonestyTests
{
    private const int F8 = 0x77;
    private const int F9 = 0x78;
    private const int F12 = 0x7B;

    /// <summary>The whole point: the text is stored, nothing is listening, and the script says so.</summary>
    [Fact]
    public void AKeyWindowsRefusedDoesNotCountAsBound()
    {
        using var script = new HotkeyScript("F12", refuse: F12);

        Assert.Equal("F12", script.StartStopHotkey);
        Assert.True(script.HotkeyFailed);
    }

    /// <summary>
    /// And the restart, which is where it used to go quiet altogether: the constructor's retry
    /// never notified, so a key that failed once failed unannounced forever after. The answer is
    /// read off the registration rather than remembered, so it comes back on its own.
    /// </summary>
    [Fact]
    public void AKeyThatFailsAgainOnStartupIsStillFlagged()
    {
        using var restarted = new HotkeyScript("F12", refuse: F12);

        Assert.Empty(restarted.Warnings);
        Assert.True(restarted.HotkeyFailed);
    }

    [Fact]
    public void AKeyThatRegisteredIsNotFlagged()
    {
        using var script = new HotkeyScript("F9");

        Assert.False(script.HotkeyFailed);
    }

    /// <summary>No hotkey bound is a valid choice, and not a failure to report.</summary>
    [Fact]
    public void NoHotkeyAtAllIsNotAFailure()
    {
        using var script = new HotkeyScript(string.Empty);

        Assert.False(script.HotkeyFailed);
    }

    /// <summary>A rebind onto a key that is free clears it, and the page hears about it.</summary>
    [Fact]
    public void ARebindThatTakesClearsTheFailure()
    {
        using var script = new HotkeyScript("F12", refuse: F12);
        var changes = 0;
        script.Changed += () => changes++;

        Assert.True(script.HotkeyFailed);

        script.StartStopHotkey = "F9";
        script.SaveHotkey();

        Assert.False(script.HotkeyFailed);
        Assert.True(changes > 0, "nothing told the page the key came alive");
    }

    /// <summary>
    /// The wording. F8 is the crosshair overlay's own default, and the registry knows every key
    /// this app holds — so the message names the neighbour instead of inventing a stranger.
    /// </summary>
    [Fact]
    public void AKeyAnotherRazorReaperFeatureHoldsNamesThatFeature()
    {
        using var script = new HotkeyScript(string.Empty, refuse: F8)
        {
            Registry = new StubRegistry(Binding("crosshair:toggle", "Crosshair overlay", "F8"))
        };

        script.StartStopHotkey = "F8";
        script.SaveHotkey();

        var warning = Assert.Single(script.Warnings);
        Assert.Equal(
            script.Words.T("scripts.toast.hotkey.conflict", "F8", "Crosshair overlay"),
            warning.Message);
    }

    /// <summary>
    /// The row whose label is a description rather than a feature name — the crosshair is listed
    /// under a dictionary key — is named in the reader's language, the same way the hotkeys page
    /// names it. Blaming "hotkeys.crosshair.name" would be worse than blaming another app.
    /// </summary>
    [Fact]
    public void ANamedRowIsBlamedInTheReadersLanguage()
    {
        using var script = new HotkeyScript(string.Empty, refuse: F8, language: "de-DE")
        {
            Registry = new StubRegistry(
                Binding("crosshair:toggle", "Crosshair overlay", "F8", nameKey: "hotkeys.crosshair.name"))
        };

        script.StartStopHotkey = "F8";
        script.SaveHotkey();

        var german = script.Words.T("hotkeys.crosshair.name");
        Assert.Contains(german, Assert.Single(script.Warnings).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real external case still reads as one. Nothing in this app holds F12 — Steam does —
    /// so there is no neighbour to name and the old wording is the honest one.
    /// </summary>
    [Fact]
    public void AKeyNoRazorReaperFeatureHoldsStillBlamesAnotherApp()
    {
        using var script = new HotkeyScript(string.Empty, refuse: F12)
        {
            Registry = new StubRegistry()
        };

        script.StartStopHotkey = "F12";
        script.SaveHotkey();

        var warning = Assert.Single(script.Warnings);
        Assert.Equal(script.Words.T("scripts.toast.hotkey.inuse", "F12"), warning.Message);
    }

    /// <summary>A script never accuses itself of holding its own key.</summary>
    [Fact]
    public void AScriptIsNotItsOwnConflict()
    {
        using var script = new HotkeyScript(string.Empty, refuse: F12);
        script.Registry = new StubRegistry(Binding($"script:{script.ScriptKey}", "Effects", "F12"));

        script.StartStopHotkey = "F12";
        script.SaveHotkey();

        Assert.Equal(
            script.Words.T("scripts.toast.hotkey.inuse", "F12"),
            Assert.Single(script.Warnings).Message);
    }

    /// <summary>
    /// The toast is gone in three seconds; the warning on the row and the tile is what a player
    /// still sees after a restart, where no toast fires at all. It has to name the same neighbour
    /// — a generic "another program has it" there would put the original lie back, in the one
    /// place that lasts.
    /// </summary>
    [Fact]
    public void ThePersistentWarningNamesTheSameNeighbourAsTheToast()
    {
        using var script = new HotkeyScript(string.Empty, refuse: F8)
        {
            Registry = new StubRegistry(Binding("crosshair:toggle", "Crosshair overlay", "F8"))
        };

        script.StartStopHotkey = "F8";
        script.SaveHotkey();

        Assert.True(script.HotkeyFailed);
        Assert.Equal("Crosshair overlay", script.HotkeyConflictOwner);
    }

    /// <summary>
    /// And where nothing here holds the key, the warning has no one to name — Steam is holding
    /// F12 and "another program" is the honest sentence.
    /// </summary>
    [Fact]
    public void ThePersistentWarningNamesNoOneWhenTheKeyIsHeldOutside()
    {
        using var script = new HotkeyScript("F12", refuse: F12) { Registry = new StubRegistry() };

        Assert.True(script.HotkeyFailed);
        Assert.Null(script.HotkeyConflictOwner);
    }

    /// <summary>A key that registered has no conflict to report, and no registry to scan for one.</summary>
    [Fact]
    public void AKeyThatRegisteredNamesNoOwner()
    {
        using var script = new HotkeyScript("F9")
        {
            Registry = new StubRegistry(Binding("crosshair:toggle", "Crosshair overlay", "F9"))
        };

        Assert.Null(script.HotkeyConflictOwner);
    }

    /// <summary>
    /// What "the same key" means. The field writes "Ctrl + F8", the crosshair stores whatever it
    /// was given, and a text compare would let the two sit on one key while each believed it was
    /// alone.
    /// </summary>
    [Theory]
    [InlineData("F8", "f8", true)]
    [InlineData("Ctrl + F8", "ctrl+f8", true)]
    [InlineData("Ctrl + F8", "F8 + Ctrl", true)]
    [InlineData("F8", "Ctrl + F8", false)]
    [InlineData("Ctrl + F8", "Alt + F8", false)]
    [InlineData("F8", "F9", false)]
    [InlineData("F8", "", false)]
    [InlineData("F8", "Ctrl", false)]
    public void TheSameKeySpelledDifferentlyIsTheSameKey(string a, string b, bool same)
        => Assert.Equal(same, HotkeyParser.SameCombo(a, b));

    /// <summary>
    /// A key two features were refused is held by a third party, not by either of them. The
    /// crosshair lost F12 to Steam as well, so naming it as the script's neighbour would trade one
    /// wrong culprit for another.
    /// </summary>
    [Fact]
    public void ANeighbourThatWasRefusedTheKeyTooIsNotBlamed()
    {
        using var script = new HotkeyScript(string.Empty, refuse: F12)
        {
            Registry = new StubRegistry(Binding("crosshair:toggle", "Crosshair overlay", "F12", failed: true))
        };

        script.StartStopHotkey = "F12";
        script.SaveHotkey();

        Assert.Null(script.HotkeyConflictOwner);
        Assert.Equal(script.Words.T("scripts.toast.hotkey.inuse", "F12"), Assert.Single(script.Warnings).Message);
    }

    // The two keys that are not scripts: the Auto Clicker's and the crosshair's. Both pages and
    // the hotkeys list read the registry row, so that row is what these ask.

    /// <summary>
    /// The Auto Clicker's key refused at app start. No toast has anywhere to show yet, so the row
    /// is the only place left to say it.
    /// </summary>
    [Fact]
    public void TheAutoClickersRefusedKeyIsFlaggedQuietlyAtStartup()
    {
        using var app = new AppKeys(clickerRefused: true);

        Assert.False(app.Clicker.IsBound);
        Assert.True(app.Row("/autoclicker").HasFailed!());
        Assert.Empty(app.Warnings);
    }

    /// <summary>A rebind onto a key a script holds names the script, in the toast and on the row.</summary>
    [Fact]
    public void TheAutoClickersRefusedKeyNamesTheScriptHoldingIt()
    {
        using var script = new HotkeyScript(AutoClickerHotkey.Display);
        using var app = new AppKeys(clickerRefused: true, script: script);

        app.Clicker.Rebind();

        Assert.Equal(
            app.Words.T("scripts.toast.hotkey.conflict", AutoClickerHotkey.Display, "Effects"),
            Assert.Single(app.Warnings).Message);
        Assert.Equal("Effects", app.Row("/autoclicker").FailureOwner!());
    }

    /// <summary>Held outside the app: no neighbour to name, and the Auto Clicker does not name itself.</summary>
    [Fact]
    public void TheAutoClickersKeyHeldOutsideStillBlamesAnotherApp()
    {
        using var app = new AppKeys(clickerRefused: true);

        app.Clicker.Rebind();

        Assert.Equal(
            app.Words.T("autoclicker.toast.hotkeyinuse", AutoClickerHotkey.Display),
            Assert.Single(app.Warnings).Message);
        Assert.Null(app.Row("/autoclicker").FailureOwner!());
    }

    [Fact]
    public void TheAutoClickersFlagClearsOnceTheKeyComesFree()
    {
        using var app = new AppKeys(clickerRefused: true);

        app.Hotkeys.Refuse.Clear();
        app.Clicker.Rebind();

        Assert.True(app.Clicker.IsBound);
        Assert.False(app.Row("/autoclicker").HasFailed!());
    }

    /// <summary>Released on purpose while its page records a new key: unbound, but nothing refused it.</summary>
    [Fact]
    public void AnAutoClickerKeyReleasedForRecordingIsNotAFailure()
    {
        using var app = new AppKeys(clickerRefused: false);

        app.Clicker.IsSuspended = true;

        Assert.False(app.Clicker.IsBound);
        Assert.False(app.Row("/autoclicker").HasFailed!());
    }

    /// <summary>The crosshair's key refused because a script holds it: flagged, and the script is named.</summary>
    [Fact]
    public void TheCrosshairsRefusedKeyNamesTheScriptHoldingIt()
    {
        using var script = new HotkeyScript("F8");
        using var app = new AppKeys(crosshair: new CrosshairKey("F8", failed: true), script: script);

        var row = app.Row("/crosshair");

        Assert.True(row.HasFailed!());
        Assert.Equal("Effects", row.FailureOwner!());
    }

    private static HotkeyBinding Binding(string id, string name, string combo, string? nameKey = null, bool failed = false)
        => new()
        {
            Id = id,
            Name = name,
            NameKey = nameKey,
            GroupKey = "hotkeys.group.overlays",
            DescriptionKey = "hotkeys.crosshair.description",
            OwnerRoute = "/crosshair",
            Get = () => combo,
            Set = _ => { },
            HasFailed = () => failed
        };

    /// <summary>A registry holding exactly what a test put in it, matched the real way.</summary>
    private sealed class StubRegistry(params HotkeyBinding[] bindings) : IHotkeyRegistry
    {
        public IReadOnlyList<HotkeyBinding> GetBindings() => bindings;

        public IReadOnlyList<HotkeyBinding> ForRoute(string route)
            => bindings.Where(b => b.OwnerRoute == route).ToArray();

        public HotkeyBinding? OwnerOf(string? combo, string? exceptId = null)
            => bindings.FirstOrDefault(b => b.Id != exceptId && b.Holds(combo));
    }

    /// <summary>
    /// The real hotkey registry over the Auto Clicker's real binder. The binder takes the stored
    /// key — the default, in a test host — and <c>clickerRefused</c> has Windows turn it down
    /// before the binder first asks.
    /// </summary>
    private sealed class AppKeys : IDisposable
    {
        private readonly RecordingNotificationService _toasts = new();
        private readonly HotkeyRegistry _registry;

        public AppKeys(bool clickerRefused = false, ICrosshairService? crosshair = null, AutomationScriptBase? script = null)
        {
            if (clickerRefused) Hotkeys.Refuse.Add(AutoClickerHotkey.Code);

            Clicker = new AutoClickerHotkeyBinder(
                Hotkeys, _toasts, new IdleClicker(), Words, NullLogger<AutoClickerHotkeyBinder>.Instance);
            _registry = new HotkeyRegistry(
                script is null ? [] : [script], crosshair ?? new CrosshairKey("", failed: false), Clicker, Words);
            Clicker.ResolveHotkeyRegistry = () => _registry;
        }

        public FakeAutomationHotkeyService Hotkeys { get; } = new();

        public ILocalizer Words { get; } = new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

        public AutoClickerHotkeyBinder Clicker { get; }

        public IReadOnlyList<RecordingNotificationService.Toast> Warnings
            => _toasts.Toasts.Where(t => t.Level == "warning").ToArray();

        /// <summary>The row a page reads for its own key.</summary>
        public HotkeyBinding Row(string route) => Assert.Single(_registry.ForRoute(route));

        public void Dispose() => Clicker.Dispose();
    }

    /// <summary>The binder only toggles the clicker on a key press, and no test here presses one.</summary>
    private sealed class IdleClicker : IAutoClickerRuntime
    {
        public bool IsRunning => false;

        public int ClickCount => 0;

        public DateTime? NextClickTime => null;

        public AutoClickerConfig Config { get; } = new();

        public event Action? StateChanged { add { } remove { } }

        public void Configure(AutoClickerConfig config) { }

        public Task StartAsync() => Task.CompletedTask;

        public void Stop() { }

        public Task ToggleAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// The crosshair as the registry reads it: a stored key, and whether Windows took it. The real
    /// service opens an overlay window and a tray icon, which a test host must not.
    /// </summary>
    private sealed class CrosshairKey(string label, bool failed) : ICrosshairService
    {
        public (string Label, int VirtualKey, bool Ctrl, bool Alt, bool Shift) GetHotkey() => (label, 0, false, false, false);

        public bool HotkeyFailed => failed;

        public bool IsOverlayActive => false;

        public event Action? Changed { add { } remove { } }

        public event Action? LibraryChanged { add { } remove { } }

        public event Action? ShowAppRequested { add { } remove { } }

        public event Action? QuitRequested { add { } remove { } }

        public event Action? ApplyUpdateRequested { add { } remove { } }

        // Nothing below is asked for by the registry.
        public CrosshairProfile ActiveProfile => throw new NotSupportedException();
        public bool HasAnimatedActiveImage => throw new NotSupportedException();
        public string ImportsFolderPath => throw new NotSupportedException();
        public void SetUpdateReadyLabel(string? versionLabel) => throw new NotSupportedException();
        public IReadOnlyList<CrosshairProfile> GetBuiltInPresets() => throw new NotSupportedException();
        public IReadOnlyList<CrosshairProfile> GetSavedProfiles() => throw new NotSupportedException();
        public IReadOnlyList<MonitorInfo> GetMonitors() => throw new NotSupportedException();
        public void UpdateActive(CrosshairProfile profile) => throw new NotSupportedException();
        public void LoadProfile(CrosshairProfile profile) => throw new NotSupportedException();
        public void StartOverlay() => throw new NotSupportedException();
        public void StopOverlay() => throw new NotSupportedException();
        public void ToggleOverlay() => throw new NotSupportedException();
        public Task<bool> SaveAsAsync(string name) => throw new NotSupportedException();
        public Task<bool> DeleteSavedAsync(string id) => throw new NotSupportedException();
        public byte[] RenderPreviewPng(double phase = 0.25) => throw new NotSupportedException();
        public Task<string?> ImportImageAsync(Stream source, string fileName) => throw new NotSupportedException();
        public IReadOnlyList<string> GetImportedImagePaths() => throw new NotSupportedException();
        public byte[]? RenderThumbnailPng(string imagePath, int size = 72) => throw new NotSupportedException();
        public Task<bool> CopyImportsFolderPathAsync() => throw new NotSupportedException();
        public bool DeleteImportedImage(string path) => throw new NotSupportedException();
        public void UseImportedImage(string path) => throw new NotSupportedException();
        public void SetHotkey(string displayLabel, int virtualKey, bool ctrl, bool alt, bool shift) => throw new NotSupportedException();
    }

    /// <summary>
    /// A script that is nothing but the shared hotkey plumbing. Its run loop never starts — every
    /// question here is asked of a script standing still.
    /// </summary>
    private sealed class HotkeyScript : AutomationScriptBase
    {
        private readonly RecordingNotificationService _toasts;

        public HotkeyScript(string defaultHotkey, int refuse = 0, string language = "en-US")
            : this(new RecordingNotificationService(), new FakeAutomationHotkeyService(),
                   new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo(language)),
                   defaultHotkey, refuse)
        {
        }

        private HotkeyScript(
            RecordingNotificationService toasts,
            FakeAutomationHotkeyService hotkeys,
            ILocalizer localizer,
            string defaultHotkey,
            int refuse)
            // A key of its own per instance: the base loads and saves through Preferences, which
            // on this machine may be a real store that outlives the run. A shared key would let
            // one test's saved combo become the next test's starting point.
            : base($"test-hotkey-{Guid.NewGuid():N}", "Effects", defaultHotkey,
                   new FakeForegroundGate(gameIsForeground: false),
                   Refusing(hotkeys, refuse),
                   toasts,
                   new RecordingActivityService(),
                   localizer,
                   NullLogger.Instance)
        {
            _toasts = toasts;
            Words = localizer;
        }

        /// <summary>The dictionary this script speaks, so a test asserts wording without quoting it.</summary>
        public ILocalizer Words { get; }

        /// <summary>The registry the conflict lookup reaches for. Null is "there is none".</summary>
        public IHotkeyRegistry? Registry
        {
            get => ResolveHotkeyRegistry();
            set => ResolveHotkeyRegistry = () => value;
        }

        public IReadOnlyList<RecordingNotificationService.Toast> Warnings
            => _toasts.Toasts.Where(t => t.Level == "warning").ToArray();

        /// <summary>
        /// The refusal has to be in place before the base constructor registers the default key,
        /// which is the app-start path this whole file is about.
        /// </summary>
        private static FakeAutomationHotkeyService Refusing(FakeAutomationHotkeyService hotkeys, int virtualKey)
        {
            if (virtualKey > 0) hotkeys.Refuse.Add(virtualKey);
            return hotkeys;
        }

        protected override Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
