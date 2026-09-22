using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Automation.Scripts;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The Turret Manager played against a fake inventory screen drawn the way ARK draws it — panel
/// frames, bordered cells, bullet and shard icons in the colours the classifier was measured on —
/// that changes with what the script clicks and presses. What is pinned: one fill per opening,
/// never on a big container, Max clicking until the turret stops taking, Stacks pressing whole
/// stacks with a re-read in between and the other ammo tried once, and nothing sent once ARK loses
/// focus or the panel closes.
///
/// Shares <c>ArkKeyDefaults</c> with <see cref="ArkKeyScanTests"/>: a start re-checks the scan.
/// </summary>
[Collection("ArkKeyDefaults")]
public sealed class TurretManagerTests
{
    private const int VkT = 0x54;

    [Fact]
    public async Task MaxClicksTransferAllUntilTwoClicksChangeNothing()
    {
        using var rig = new Rig();
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 12));

        await rig.FillOnce();

        Assert.Equal(3, rig.Clicks);
        Assert.Equal(10, rig.Game.InTurret);
        Assert.All(rig.Input.Events.OfType<SimulatedInput.Click>(), c => Assert.Equal(rig.Layout.TransferAll, c.At));
        Assert.DoesNotContain(rig.Toasts, t => t.Level == "warning");
    }

    [Fact]
    public async Task MaxClicksAgainWhenTheGameSwallowedOne()
    {
        using var rig = new Rig();
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 4));
        rig.Game.SwallowClicks = 1;

        await rig.FillOnce();

        Assert.Equal(4, rig.Clicks);
        Assert.Equal(4, rig.Game.InTurret);
    }

    [Fact]
    public async Task MaxStopsAtTheClickCap()
    {
        using var rig = new Rig();
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 30));
        rig.Game.StacksPerClick = 1;

        await rig.FillOnce();

        Assert.Equal(TurretManagerScript.MaxClicks, rig.Clicks);
    }

    [Fact]
    public async Task ATransferAllThatChangesNothingSaysWhy()
    {
        using var rig = new Rig();
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 4));
        rig.Game.ConfirmationBlocks = true;

        await rig.FillOnce();

        Assert.Equal(TurretManagerScript.QuietClicksToStop, rig.Clicks);
        Assert.Contains(rig.Toasts, t => t.Level == "warning" && t.Message.Contains("tooltips"));
    }

    [Fact]
    public async Task StacksMovesTheSetNumberOfWholeStacks()
    {
        using var rig = new Rig();
        rig.Script.Fill = TurretFill.Stacks;
        rig.Script.BulletStacks = 3;
        rig.Game.Player.Add(AmmoKind.Other);
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 5));
        rig.Game.Player.Add(AmmoKind.Shard);

        await rig.FillOnce();

        Assert.Equal(3, rig.Presses);
        Assert.Equal(3, rig.Game.InTurret);
        // The grid closes up behind each stack, so the first bullet is always cell 1.
        Assert.All(rig.Input.Events.OfType<SimulatedInput.MoveTo>(), m => Assert.Equal(rig.Layout.PlayerCell(1), new Point(m.X, m.Y)));
    }

    [Fact]
    public async Task StacksStopWhenTheTurretTakesNothingMore()
    {
        using var rig = new Rig(slots: 5);
        rig.Script.Fill = TurretFill.Stacks;
        rig.Script.BulletStacks = 3;
        rig.Game.InTurret = 4;
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 5));

        await rig.FillOnce();

        Assert.Equal(5, rig.Game.InTurret);
        Assert.Equal(3, rig.Presses); // one that went in, then two that did not
    }

    /// <summary>
    /// An empty Tek turret: bullets are tried first and go nowhere, shards go in — and the next
    /// turret gets shards straight away.
    /// </summary>
    [Fact]
    public async Task StacksTryTheOtherAmmoOnceAndRememberIt()
    {
        using var rig = new Rig(slots: 5, accepts: AmmoKind.Shard);
        rig.Script.Fill = TurretFill.Stacks;
        rig.Script.ShardStacks = 2;
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 3));
        rig.Game.Player.AddRange(Many(AmmoKind.Shard, 4));

        await rig.FillOnce();

        Assert.Equal(2, rig.Game.InTurret);
        Assert.Equal(4, rig.Presses);

        rig.Game.Close();
        await rig.WaitUntil(() => rig.Game.CapturesWhileClosed > 3);
        rig.Game.Reopen(inTurret: 0);
        await rig.WaitUntil(() => rig.Game.InTurret == 2);
        await rig.Idle();

        Assert.Equal(6, rig.Presses);
    }

    [Fact]
    public async Task AmmoAlreadyInTheTurretDecidesTheType()
    {
        using var rig = new Rig(slots: 5, accepts: AmmoKind.Shard);
        rig.Script.Fill = TurretFill.Stacks;
        rig.Game.InTurret = 1;
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 3));
        rig.Game.Player.AddRange(Many(AmmoKind.Shard, 2));

        await rig.FillOnce();

        Assert.Equal(1, rig.Presses);
        Assert.Equal(2, rig.Game.InTurret);
    }

    [Fact]
    public async Task NoAmmoInTheInventorySaysSoAndSendsNothing()
    {
        using var rig = new Rig();
        rig.Game.Player.AddRange(Many(AmmoKind.Other, 3));

        rig.Start();
        await rig.WaitUntil(() => rig.Toasts.Any(t => t.Level == "warning"));

        Assert.Contains(rig.Toasts, t => t.Message.Contains("no Advanced Rifle Bullets"));
        Assert.Equal(0, rig.Clicks + rig.Presses);
    }

    [Fact]
    public async Task AStorageBoxNeverTriggersIt()
    {
        using var rig = new Rig(slots: 45);
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 5));

        rig.Start();
        await rig.WaitUntil(() => rig.Game.Captures > 8);

        Assert.DoesNotContain(rig.Input.Events, e => e is SimulatedInput.Click or SimulatedInput.KeyPress);
    }

    [Fact]
    public async Task ItFillsOncePerOpeningAndAgainAfterTheNextOpen()
    {
        using var rig = new Rig();
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 30));

        await rig.FillOnce();
        var first = rig.Clicks;
        var seen = rig.Game.Captures;
        await rig.WaitUntil(() => rig.Game.Captures > seen + 10);
        Assert.Equal(first, rig.Clicks); // still open, still one fill

        rig.Game.Close();
        await rig.WaitUntil(() => rig.Game.CapturesWhileClosed > 3);
        rig.Game.Reopen(inTurret: 0);
        await rig.WaitUntil(() => rig.Game.InTurret == 10);
        await rig.Idle();

        Assert.Equal(2 * first, rig.Clicks);
        Assert.True(rig.Script.IsRunning);
    }

    [Fact]
    public async Task LosingFocusMidFillStopsTheFillNotTheWatcher()
    {
        using var rig = new Rig();
        rig.Script.Fill = TurretFill.Stacks;
        rig.Script.BulletStacks = 5;
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 8));
        rig.Game.AfterPress = n => { if (n == 2) rig.Foreground.GameIsForeground = false; };

        rig.Start();
        await rig.WaitUntil(() => rig.Presses == 2);
        await Task.Delay(400);

        Assert.Equal(2, rig.Presses);
        Assert.True(rig.Script.IsRunning);
    }

    [Fact]
    public async Task APanelClosedMidFillGetsNothingMore()
    {
        using var rig = new Rig();
        rig.Script.Fill = TurretFill.Stacks;
        rig.Script.BulletStacks = 5;
        rig.Game.Player.AddRange(Many(AmmoKind.Bullet, 8));
        rig.Game.AfterPress = n => { if (n == 1) rig.Game.Close(); };

        rig.Start();
        await rig.WaitUntil(() => rig.Presses == 1);
        await rig.WaitUntil(() => rig.Game.CapturesWhileClosed > 3);

        Assert.Equal(1, rig.Presses);
        Assert.Equal(0, rig.Game.Violations);
    }

    private static IEnumerable<AmmoKind> Many(AmmoKind kind, int count) => Enumerable.Repeat(kind, count);

    /// <summary>The script, its fakes, and the turret screen it plays against.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly RecordingNotificationService _notifications = new();

        public Rig(int slots = 10, AmmoKind accepts = AmmoKind.Bullet)
        {
            Layout = new TurretInventoryLayout(new Rectangle(0, 0, 1920, 1080), 1.0);
            var sampler = new FakeScreenSampler();
            Game = new FakeTurretScreen(Layout, Input, sampler, slots, accepts);
            Script = new TurretManagerScript(
                Input, sampler, new FakeGameDisplayService(), new FakeArkPathProvider(), Foreground,
                new NullAutomationHotkeyService(), _notifications, new RecordingActivityService(),
                new Localizer(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US")),
                NullLogger<TurretManagerScript>.Instance);
            Script.TransferKey = "T";
        }

        public TurretInventoryLayout Layout { get; }
        public RecordingInputSimulator Input { get; } = new();
        public FakeForegroundGate Foreground { get; } = new(gameIsForeground: true);
        public FakeTurretScreen Game { get; }
        public TurretManagerScript Script { get; }
        public IReadOnlyList<RecordingNotificationService.Toast> Toasts => _notifications.Toasts;
        public int Clicks => Input.Events.Count(e => e is SimulatedInput.Click);
        public int Presses => Input.Events.Count(e => e is SimulatedInput.KeyPress { VirtualKey: VkT });

        public void Start() => Assert.True(Script.Start());

        /// <summary>Starts it on an open turret and waits until the fill is over.</summary>
        public async Task FillOnce()
        {
            Start();
            await WaitUntil(() => Clicks + Presses > 0);
            await Idle();
        }

        /// <summary>Waits until no input has gone out for a good number of watcher polls.</summary>
        public async Task Idle()
        {
            var count = -1;
            while (count != Input.Events.Count)
            {
                count = Input.Events.Count;
                var captures = Game.Captures;
                await WaitUntil(() => Game.Captures > captures + 6);
            }
        }

        public async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(10);
            }
            Assert.Fail("The Turret Manager did not reach the expected state within 10 s.");
        }

        public void Dispose() => Script.Dispose();
    }

    /// <summary>
    /// An ARK inventory with a structure open: the three panel frames, the structure's bordered
    /// slots (all rows full for a big container), the player's items packed from the first cell.
    /// Transfer All moves every stack the structure accepts until it is full; the transfer key
    /// moves the hovered stack if it is accepted.
    /// </summary>
    internal sealed class FakeTurretScreen
    {
        private static readonly (byte R, byte G, byte B) World = (25, 55, 50), Frame = (127, 231, 255), Line = (23, 127, 149),
            Olive = (110, 105, 70), Copper = (160, 110, 80), Crystal = (150, 175, 175), Red = (200, 60, 60);

        private static readonly int[] FrameXs = [-878, -249, -220, 219, 248, 877];

        private readonly TurretInventoryLayout _layout;
        private readonly int _slots;
        private readonly AmmoKind _accepts;
        private readonly object _sync = new();
        private Point _cursor;
        private int _presses;
        private int _captures;
        private int _closedCaptures;

        public FakeTurretScreen(TurretInventoryLayout layout, RecordingInputSimulator input, FakeScreenSampler screen, int slots, AmmoKind accepts)
        {
            _layout = layout;
            _slots = slots;
            _accepts = accepts;
            input.Recorded += OnInput;
            screen.Screen = Render;
        }

        public bool Open { get; private set; } = true;
        public List<AmmoKind> Player { get; } = new();
        public int InTurret { get; set; }
        public int SwallowClicks { get; set; }
        public bool ConfirmationBlocks { get; set; }
        public int StacksPerClick { get; set; } = int.MaxValue;
        public Action<int>? AfterPress { get; set; }
        public int Violations { get; private set; }
        public int Captures => Volatile.Read(ref _captures);
        public int CapturesWhileClosed => Volatile.Read(ref _closedCaptures);

        public void Close()
        {
            lock (_sync) { Open = false; _closedCaptures = 0; }
        }

        public void Reopen(int inTurret)
        {
            lock (_sync) { InTurret = inTurret; Open = true; }
        }

        private void OnInput(SimulatedInput e)
        {
            lock (_sync)
            {
                switch (e)
                {
                    case SimulatedInput.Click c:
                        if (!Open || c.At != _layout.TransferAll) { Violations++; break; }
                        if (SwallowClicks > 0) { SwallowClicks--; break; }
                        if (ConfirmationBlocks) break;
                        for (var moved = 0; moved < StacksPerClick && InTurret < _slots; moved++)
                        {
                            var i = Player.IndexOf(_accepts);
                            if (i < 0) break;
                            Player.RemoveAt(i);
                            InTurret++;
                        }
                        break;

                    case SimulatedInput.MoveTo m:
                        _cursor = new Point(m.X, m.Y);
                        break;

                    case SimulatedInput.KeyPress { VirtualKey: VkT }:
                        if (!Open) { Violations++; break; }
                        var cell = Enumerable.Range(0, Player.Count).FirstOrDefault(i => _layout.PlayerCell(i) == _cursor, -1);
                        if (cell >= 0 && Player[cell] == _accepts && InTurret < _slots)
                        {
                            Player.RemoveAt(cell);
                            InTurret++;
                        }
                        AfterPress?.Invoke(++_presses);
                        break;
                }
            }
        }

        private ScreenCapture Render(Rectangle region)
        {
            lock (_sync)
            {
                Interlocked.Increment(ref _captures);
                if (!Open) Interlocked.Increment(ref _closedCaptures);

                var bgra = new byte[region.Width * region.Height * 4];
                for (var p = 0; p < bgra.Length; p += 4) (bgra[p], bgra[p + 1], bgra[p + 2]) = (World.B, World.G, World.R);
                if (!Open) return new ScreenCapture(region.Width, region.Height, bgra);

                var canvas = new Canvas(region, bgra);
                var centre = new Point(960, 540);
                foreach (var x in FrameXs) canvas.Fill(new Rectangle(centre.X + x, 69, 1, 903), Frame);

                Grid(canvas, _layout.TurretGrid, Math.Min(_slots, 18));
                for (var i = 0; i < Math.Min(InTurret, 18); i++) Icon(canvas, _layout.TurretCell(i), _accepts);

                Grid(canvas, _layout.PlayerGrid, Player.Count);
                for (var i = 0; i < Player.Count; i++) Icon(canvas, _layout.PlayerCell(i), Player[i]);

                return new ScreenCapture(region.Width, region.Height, bgra);
            }
        }

        private void Grid(Canvas canvas, Point grid, int cells)
        {
            for (var row = 0; row * 6 < cells; row++)
            {
                var inRow = Math.Min(6, cells - row * 6);
                var top = _layout.RowTop(grid, row);
                for (var column = 0; column <= inRow; column++)
                    canvas.Fill(new Rectangle(_layout.LineX(grid, column), top, 1, (int)_layout.Pitch), Line);
            }
        }

        private static void Icon(Canvas canvas, Point c, AmmoKind kind)
        {
            switch (kind)
            {
                case AmmoKind.Bullet:
                    canvas.Fill(new Rectangle(c.X - 10, c.Y - 10, 20, 35), Olive);
                    canvas.Fill(new Rectangle(c.X - 5, c.Y - 22, 10, 10), Copper);
                    break;
                case AmmoKind.Shard:
                    canvas.Fill(new Rectangle(c.X - 15, c.Y - 15, 30, 30), Crystal);
                    break;
                case AmmoKind.Other:
                    canvas.Fill(new Rectangle(c.X - 15, c.Y - 15, 30, 30), Red);
                    break;
            }
        }

        private readonly record struct Canvas(Rectangle Region, byte[] Bgra)
        {
            public void Fill(Rectangle box, (byte R, byte G, byte B) colour)
            {
                var visible = Rectangle.Intersect(box, Region);
                for (var y = visible.Top; y < visible.Bottom; y++)
                    for (var x = visible.Left; x < visible.Right; x++)
                    {
                        var i = ((y - Region.Top) * Region.Width + (x - Region.Left)) * 4;
                        (Bgra[i], Bgra[i + 1], Bgra[i + 2]) = (colour.B, colour.G, colour.R);
                    }
            }
        }
    }
}

/// <summary>The ammo calculator's sums.</summary>
public sealed class TurretAmmoCalculatorTests
{
    [Fact]
    public void AnEvenSplitHandsOutWholeStacksAndKeepsTheRest()
    {
        // 23 stacks and a half for 4 turrets: 5 stacks each, 3 stacks and 50 over.
        var plan = TurretAmmoCalculator.Plan(turrets: 4, onHand: 2350, stackSize: 100, stacksEach: 5);

        Assert.Equal(new AmmoPlan(StacksPerTurret: 5, AmmoPerTurret: 500, Leftover: 350, TurretsCovered: 4, Missing: 0), plan);
    }

    [Fact]
    public void AFixedNumberOfStacksSaysHowFarItGoes()
    {
        // 12 000 shards, 5 Tek turrets, 3 stacks of 1000 each: 4 turrets, 3000 short.
        var plan = TurretAmmoCalculator.Plan(turrets: 5, onHand: 12_000, stackSize: 1000, stacksEach: 3);

        Assert.Equal(2, plan.StacksPerTurret);
        Assert.Equal(4, plan.TurretsCovered);
        Assert.Equal(3000, plan.Missing);
    }

    [Fact]
    public void AModdedStackSizeIsUsed()
        => Assert.Equal(2, TurretAmmoCalculator.Plan(turrets: 3, onHand: 1500, stackSize: 250, stacksEach: 1).StacksPerTurret);

    [Fact]
    public void NoTurretsMeansEverythingIsLeftOver()
        => Assert.Equal(new AmmoPlan(0, 0, 900, 0, 0), TurretAmmoCalculator.Plan(turrets: 0, onHand: 900, stackSize: 100, stacksEach: 2));

    [Fact]
    public void NonsenseInputsDoNotThrow()
    {
        var plan = TurretAmmoCalculator.Plan(turrets: 2, onHand: -5, stackSize: 0, stacksEach: -1);

        Assert.Equal(0, plan.StacksPerTurret);
        Assert.Equal(0, plan.Missing);
    }
}
