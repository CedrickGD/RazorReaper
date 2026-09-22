using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// What one Fed-Suit cycle is made of, played against a fake transmitter whose screen follows
/// what the macro did to it.
///
/// The first version pressed the transfer key twenty times wherever the cursor happened to be.
/// The second moved the pieces on fixed timers and, after two or three suits on a real server,
/// left one behind and closed the transmitter on an odd count. So everything here is about the
/// count: every picked piece goes in, a press the game dropped is pressed again, a slow set is
/// waited for, and a piece that will not go stops the run instead of cycling on.
///
/// Shares <c>ArkKeyDefaults</c> with <see cref="ArkKeyScanTests"/>: the macro rescans its open
/// and transfer keys through a process-wide static that those tests swap out.
/// </summary>
[Collection("ArkKeyDefaults")]
public sealed class FedSuitCycleTests
{
    private static readonly Rectangle FullHd = new(0, 0, 1920, 1080);

    /// <summary>The slot list is head, chest, legs down the left column, then hands and feet.</summary>
    [Theory]
    [InlineData(FedSuitPieces.Head, new[] { 0 })]
    [InlineData(FedSuitPieces.Chest, new[] { 1 })]
    [InlineData(FedSuitPieces.Legs, new[] { 2 })]
    [InlineData(FedSuitPieces.Hands, new[] { 3 })]
    [InlineData(FedSuitPieces.Feet, new[] { 4 })]
    [InlineData(FedSuitPieces.Hands | FedSuitPieces.Chest, new[] { 1, 3 })]
    [InlineData(FedSuitPieces.Legs | FedSuitPieces.Feet, new[] { 2, 4 })]
    [InlineData(FedSuitPieces.All, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(FedSuitPieces.None, new int[0])]
    public void EachPickedPieceMapsToItsSlot(FedSuitPieces pieces, int[] slots)
        => Assert.Equal(slots, FedSuitSettings.SlotsFor(pieces));

    [Fact]
    public async Task ThreeRunsPutThreeWholeSuitsInTheTransmitter()
    {
        var rig = new Rig();
        rig.Configure(s => s.Runs = 3);

        await rig.RunToEnd();

        Assert.Equal(15, rig.Game.InTransmitter);
        Assert.Equal(0, rig.Game.Violations);
        Assert.Contains(rig.Toasts, t => t.Level == "info" && t.Message.Contains("cycles: 3, pieces moved: 15"));
    }

    /// <summary>
    /// The transmitter opens with its own tab in front, and the transfer key moves what the panel
    /// in front is showing — so the player's tab is clicked after the open key and before any
    /// piece is pressed.
    /// </summary>
    [Fact]
    public async Task ThePlayerTabIsClickedBeforeAnythingIsTransferred()
    {
        var rig = new Rig();
        rig.Configure(s => s.Runs = 1);

        await rig.RunToEnd();

        var events = rig.Input.Events.ToList();
        var open = events.FindIndex(e => e is SimulatedInput.KeyPress { VirtualKey: VkF });
        var click = events.FindIndex(e => e is SimulatedInput.Click);
        var transfer = events.FindIndex(e => e is SimulatedInput.KeyPress { VirtualKey: VkT });

        Assert.True(open < click && click < transfer, $"open {open}, click {click}, transfer {transfer}");
        Assert.Equal(ArkInventoryLayout.PlayerTab(FullHd, 1.0), ((SimulatedInput.Click)events[click]).At);
    }

    [Fact]
    public async Task OnlyThePickedPiecesAreMoved()
    {
        var rig = new Rig();
        rig.Configure(s =>
        {
            s.Runs = 2;
            s.Pieces = FedSuitPieces.Hands | FedSuitPieces.Chest;
        });

        await rig.RunToEnd();

        Assert.Equal(4, rig.Game.InTransmitter);
        Assert.Equal(new[] { true, false, true, false, true }, rig.Game.Worn);

        // The cursor never even visits the three that stay on.
        var slots = ArkInventoryLayout.ArmorSlots(FullHd, 1.0);
        var visited = rig.Input.Events.OfType<SimulatedInput.MoveTo>().Select(m => new Point(m.X, m.Y)).ToHashSet();
        Assert.DoesNotContain(slots[0], visited);
        Assert.DoesNotContain(slots[2], visited);
        Assert.DoesNotContain(slots[4], visited);
    }

    /// <summary>
    /// The bug the owner hit: a press that landed before the game had the piece under the cursor
    /// did nothing, and the cycle closed anyway. Now the piece that stayed is pressed again.
    /// </summary>
    [Fact]
    public async Task APressTheGameDroppedIsPressedAgain()
    {
        var rig = new Rig();
        rig.Game.DroppedPresses.UnionWith([3, 7, 8]);
        rig.Configure(s => s.Runs = 3);

        await rig.RunToEnd();

        Assert.Equal(15, rig.Game.InTransmitter);
        Assert.Contains(rig.Toasts, t => t.Message.Contains("cycles: 3, pieces moved: 15"));
    }

    /// <summary>The other half of it: a server slow to hand out the next set gets waited for.</summary>
    [Fact]
    public async Task ASetThatArrivesLateIsWaitedFor()
    {
        var rig = new Rig();
        rig.Game.SetArrivesAfterCaptures = 12;
        rig.Configure(s => s.Runs = 3);

        await rig.RunToEnd();

        Assert.Equal(15, rig.Game.InTransmitter);
        Assert.Equal(0, rig.Game.Violations);
    }

    /// <summary>A panel slow to close is waited for too: the next open key never lands on it.</summary>
    [Fact]
    public async Task ATransmitterSlowToCloseIsWaitedFor()
    {
        var rig = new Rig();
        rig.Game.ClosesAfterCaptures = 8;
        rig.Configure(s => s.Runs = 3);

        await rig.RunToEnd();

        Assert.Equal(15, rig.Game.InTransmitter);
        Assert.Equal(0, rig.Game.Violations);
    }

    /// <summary>
    /// A piece that will not leave — here because the transmitter is full — gets three presses and
    /// then stops the run. Carrying on would only stack up odd counts, and the toast says what
    /// was moved so the player can check it against the transmitter.
    /// </summary>
    [Fact]
    public async Task APieceThatWillNotMoveStopsTheRun()
    {
        var rig = new Rig();
        rig.Game.Capacity = 7;

        await rig.RunToEnd(); // Runs = 0: only the stuck piece can end it

        Assert.Equal(7, rig.Game.InTransmitter);
        Assert.Equal(2, rig.Input.Events.Count(e => e is SimulatedInput.KeyPress { VirtualKey: VkF }));
        Assert.Equal(5 + 2 + (3 * 3), rig.Input.Events.Count(e => e is SimulatedInput.KeyPress { VirtualKey: VkT }));
        Assert.Contains(rig.Toasts, t =>
            t.Level == "warning" && t.Message.Contains("did not move") && t.Message.Contains("cycles: 1, pieces moved: 7"));
    }

    [Fact]
    public void NoPiecePickedRefusesToStart()
    {
        var rig = new Rig();
        rig.Configure(s => s.Pieces = FedSuitPieces.None);

        Assert.False(rig.Macro.Start(alreadyMetered: true));
        Assert.False(rig.Macro.IsRunning);
        Assert.Contains(rig.Toasts, t => t.Level == "warning" && t.Message.Contains("at least one piece"));
    }

    /// <summary>Zero is the default and means the loop runs until somebody stops it.</summary>
    [Fact]
    public async Task ZeroRunsKeepsGoing()
    {
        var rig = new Rig();
        rig.Game.Capacity = int.MaxValue;

        await rig.Start();
        await WaitUntil(() => rig.Game.InTransmitter >= 20);

        Assert.True(rig.Macro.IsRunning);
        rig.Macro.Stop();
        await WaitUntil(() => !rig.Macro.IsRunning);
    }

    /// <summary>
    /// An open key that changed nothing on screen means no inventory opened — out of reach,
    /// switched off, or a key that found nothing to open. Everything after this step is clicks
    /// and cursor jumps that land in gameplay, so the run ends before the tab click.
    /// </summary>
    [Fact]
    public async Task AnOpenKeyThatChangedNothingStopsTheCycleBeforeTheTabClick()
    {
        var rig = new Rig();
        rig.Game.OpenKeyWorks = false;

        await rig.RunToEnd();

        Assert.DoesNotContain(rig.Input.Events, e => e is SimulatedInput.Click);
        Assert.Contains(rig.Toasts, t => t.Level == "warning" && t.Message.Contains("did not open"));
    }

    /// <summary>
    /// A run started again right after a stuck stop, which leaves the transmitter open on the
    /// player's tab. The open key does nothing to an open panel, so the screen does not change
    /// and the run ends before a single click or transfer — the toast asks to close it first.
    /// </summary>
    [Fact]
    public async Task ARunStartedOnAnOpenTransmitterStopsBeforeTheTabClick()
    {
        var rig = new Rig();
        rig.Game.LeaveOpen();

        await rig.RunToEnd();

        Assert.Equal(1, rig.Input.Events.Count(e => e is SimulatedInput.KeyPress { VirtualKey: VkF }));
        Assert.DoesNotContain(rig.Input.Events, e => e is SimulatedInput.Click or SimulatedInput.KeyPress { VirtualKey: VkT });
        Assert.Equal(0, rig.Game.InTransmitter);
        Assert.Contains(rig.Toasts, t => t.Level == "warning" && t.Message.Contains("did not open"));
    }

    /// <summary>
    /// The answer a live screen actually gives, between "all black" and "all white": the
    /// transmitter's own particle beam — or wind-blown foliage, or a creature walking past — moves
    /// one of the five sample points while the inventory is still shut. One point out of five is
    /// background noise, not a panel.
    /// </summary>
    [Fact]
    public async Task OnePointOfFiveMovingIsNoiseAndNotAnOpenInventory()
    {
        var head = ArkInventoryLayout.ArmorSlots(FullHd, 1.0)[0];
        var rig = new Rig(new FakeScreenSampler { NoisyBox = new Rectangle(head.X - 20, head.Y - 20, 41, 41) });

        await rig.RunToEnd();

        Assert.DoesNotContain(rig.Input.Events, e => e is SimulatedInput.Click);
        Assert.Contains(rig.Toasts, t => t.Level == "warning" && t.Message.Contains("did not open"));
    }

    /// <summary>An alt-tab mid-run stops it before a single key goes to whatever took the focus.</summary>
    [Fact]
    public async Task ARunWithoutARKInFrontSendsNothing()
    {
        var rig = new Rig();
        rig.Foreground.GameIsForeground = false;

        await rig.RunToEnd();

        Assert.DoesNotContain(rig.Input.Events, e => e is SimulatedInput.KeyPress or SimulatedInput.Click);
        Assert.Contains(rig.Toasts, t => t.Level == "warning" && t.Message.Contains("could not run"));
    }

    /// <summary>
    /// A player at interface scale 0.7: every slot and the tab are where the scaled panel draws
    /// them, and the sample box still sits inside a slot that is smaller than at 1.0.
    /// </summary>
    [Fact]
    public async Task ASmallInterfaceScaleIsFollowed()
    {
        var install = ArkInventoryLayoutTests.WriteGameUserSettings("UIScaling=0.700000");
        var rig = new Rig(arkRoot: install, uiScale: 0.7);
        rig.Configure(s => s.Runs = 2);

        await rig.RunToEnd();

        Assert.Equal(10, rig.Game.InTransmitter);
        Assert.Equal(0, rig.Game.Violations);
    }

    /// <summary>
    /// The half of the old open guard which lives in the engine: a subscriber that stops from the
    /// step callback must keep that step from running.
    /// </summary>
    [Fact]
    public async Task AStepTheSubscriberStoppedAtIsNeverExecuted()
    {
        var input = new RecordingInputSimulator();
        var engine = new MacroEngine(
            input,
            new NoProcesses(),
            Options.Create(new AppConfiguration()),
            new RecordingActivityService(),
            new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
            NullLogger<MacroEngine>.Instance);

        var runner = engine.GetRunner("stop-from-callback");
        runner.StepStarted += (step, _) => { if (step == 1) runner.Stop(); };

        var ran = await runner.RunAsync(new MacroSequence
        {
            Steps = [MacroStep.KeyPress(0x54), MacroStep.ClickAt(10, 20)],
            RepeatCount = 1
        });

        Assert.False(ran);
        Assert.Contains(input.Events, e => e is SimulatedInput.KeyPress);
        Assert.DoesNotContain(input.Events, e => e is SimulatedInput.Click);
    }

    private const int VkF = 0x46;
    private const int VkT = 0x54;
    private const int VkEsc = 0x1B;

    /// <summary>The macro, its fakes, and the transmitter it plays against.</summary>
    private sealed class Rig
    {
        private readonly FakeMacroEngine _engine = new();
        private readonly RecordingNotificationService _notifications = new();

        public Rig(FakeScreenSampler? sampler = null, string? arkRoot = null, double uiScale = 1.0)
        {
            // A test that brings its own screen plays against that screen, not the transmitter.
            var screen = sampler ?? new FakeScreenSampler();
            Game = sampler is null
                ? new FakeTransmitter(Input, screen, FullHd, uiScale)
                : new FakeTransmitter(new RecordingInputSimulator(), new FakeScreenSampler(), FullHd, uiScale);

            Macro = new FedSuitMacro(
                _engine,
                new FakeGameDisplayService(),
                new FakeArkPathProvider(arkRoot),
                screen,
                Input,
                Foreground,
                _notifications,
                new RecordingActivityService(),
                new CountingUsageGateService(),
                new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
                NullLogger<FedSuitMacro>.Instance);

            // Pinned rather than scanned: the defaults come out of whatever Input.ini this machine has.
            Configure(s =>
            {
                s.OpenKey = "F";
                s.TransferKey = "T";
                s.ExitKey = "Esc";
            });
        }

        public RecordingInputSimulator Input { get; } = new();

        public FakeForegroundGate Foreground { get; } = new(gameIsForeground: true);

        public FakeTransmitter Game { get; }

        public FedSuitMacro Macro { get; }

        public IReadOnlyList<RecordingNotificationService.Toast> Toasts => _notifications.Toasts;

        public void Configure(Action<FedSuitSettings> edit)
        {
            var settings = Macro.Settings;
            edit(settings);
            Macro.UpdateSettings(settings);
        }

        /// <summary>Starts the run and plays the runner's focus step, which the fake runner does not.</summary>
        public async Task Start()
        {
            Assert.True(Macro.Start(alreadyMetered: true));
            var runner = _engine.Runner("fed-suit");
            await WaitUntil(() => runner.LastSequence is not null);
            runner.FireStep(1, 1);
        }

        public async Task RunToEnd()
        {
            await Start();
            await WaitUntil(() => !Macro.IsRunning);
        }
    }

    /// <summary>
    /// A Tek Transmitter and the player standing at it, as far as the macro can see and touch
    /// them: flat brightness for the world, the transmitter's own tab and the player's panel, and
    /// a box per armour slot that is bright with a piece in it, dim without, and a little brighter
    /// under the cursor. Opening the transmitter puts a fresh piece in every empty slot.
    /// </summary>
    private sealed class FakeTransmitter
    {
        private const byte World = 20, TransmitterTab = 90, Panel = 70, EmptySlot = 45, Piece = 200, Hover = 25;

        private readonly Point[] _slots;
        private readonly Point _tab;
        private readonly int _slotHalf;
        private bool _playerTab;
        private Point _cursor;
        private int _presses;
        private int _moved;
        private int _capturesSinceOpen;
        private bool _setPending;
        private int _closingIn = -1;

        public FakeTransmitter(RecordingInputSimulator input, FakeScreenSampler screen, Rectangle client, double uiScale)
        {
            _slots = ArkInventoryLayout.ArmorSlots(client, uiScale);
            _tab = ArkInventoryLayout.PlayerTab(client, uiScale);
            _slotHalf = (int)(30 * ArkInventoryLayout.Scale(client, uiScale));
            input.Recorded += OnInput;
            screen.Screen = Render;
        }

        public bool Open { get; private set; }

        /// <summary>The state a stuck stop leaves behind: the panel open on the player's tab.</summary>
        public void LeaveOpen() => (Open, _playerTab) = (true, true);

        /// <summary>Which of the five slots hold a piece, in the layout's slot order.</summary>
        public bool[] Worn { get; } = [true, true, true, true, true];

        public int InTransmitter => Volatile.Read(ref _moved);

        public int Capacity { get; set; } = 100;

        public bool OpenKeyWorks { get; set; } = true;

        /// <summary>Transfer presses (1-based, counted over the whole run) the game does nothing with.</summary>
        public HashSet<int> DroppedPresses { get; } = new();

        /// <summary>Captures after the open before the new set shows on the player.</summary>
        public int SetArrivesAfterCaptures { get; set; }

        /// <summary>Captures after the exit key before the panel is gone.</summary>
        public int ClosesAfterCaptures { get; set; }

        /// <summary>Input the real game would have taken the wrong way: an open key on an open panel, a click or transfer with nothing to catch it.</summary>
        public int Violations { get; private set; }

        private void OnInput(SimulatedInput e)
        {
            switch (e)
            {
                case SimulatedInput.KeyPress { VirtualKey: VkF }:
                    if (Open) { Violations++; break; }
                    if (!OpenKeyWorks) break;
                    Open = true;
                    _playerTab = false;
                    _setPending = true;
                    _capturesSinceOpen = 0;
                    break;

                case SimulatedInput.KeyPress { VirtualKey: VkEsc }:
                    if (!Open) { Violations++; break; }
                    _closingIn = ClosesAfterCaptures;
                    if (_closingIn == 0) Close();
                    break;

                case SimulatedInput.Click c:
                    if (!Open || c.At != _tab) { Violations++; break; }
                    _playerTab = true;
                    _cursor = c.At.Value;
                    break;

                case SimulatedInput.MoveTo m:
                    _cursor = new Point(m.X, m.Y);
                    break;

                case SimulatedInput.KeyPress { VirtualKey: VkT }:
                    if (!Open || !_playerTab) { Violations++; break; }
                    if (DroppedPresses.Contains(++_presses)) break;
                    var slot = Array.IndexOf(_slots, _cursor);
                    if (slot < 0 || !Worn[slot] || _moved >= Capacity) break;
                    Worn[slot] = false;
                    Interlocked.Increment(ref _moved);
                    break;
            }
        }

        private void Close()
        {
            Open = false;
            _closingIn = -1;
        }

        private ScreenCapture Render(Rectangle region)
        {
            if (_closingIn > 0 && --_closingIn == 0) Close();
            if (Open && _setPending && ++_capturesSinceOpen > SetArrivesAfterCaptures)
            {
                Array.Fill(Worn, true);
                _setPending = false;
            }

            var background = !Open ? World : _playerTab ? Panel : TransmitterTab;
            var bgra = new byte[region.Width * region.Height * 4];
            Array.Fill(bgra, background);

            if (Open && _playerTab)
            {
                for (var i = 0; i < _slots.Length; i++)
                {
                    var box = new Rectangle(_slots[i].X - _slotHalf, _slots[i].Y - _slotHalf, 2 * _slotHalf + 1, 2 * _slotHalf + 1);
                    var level = (byte)((Worn[i] ? Piece : EmptySlot) + (box.Contains(_cursor) ? Hover : 0));
                    var visible = Rectangle.Intersect(box, region);
                    for (var y = visible.Top; y < visible.Bottom; y++)
                        Array.Fill(bgra, level, ((y - region.Top) * region.Width + (visible.Left - region.Left)) * 4, visible.Width * 4);
                }
            }

            return new ScreenCapture(region.Width, region.Height, bgra);
        }
    }

    /// <summary>The engine only looks at processes for a focus step, and these sequences have none.</summary>
    private sealed class NoProcesses : IProcessService
    {
        public System.Diagnostics.Process[] GetProcessesByName(string processName) => [];

        public bool IsProcessRunning(string processName) => false;

        public string? GetExecutablePath(System.Diagnostics.Process process) => null;

        public System.Diagnostics.Process? Start(string filePath) => null;

        public void Kill(System.Diagnostics.Process process) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Fed Suit did not reach the expected state within 10 s.");
    }
}
