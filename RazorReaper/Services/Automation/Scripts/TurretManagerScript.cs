using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>How much one opening of a turret gets.</summary>
public enum TurretFill
{
    /// <summary>The player panel's Transfer All, clicked until the turret stops taking — everything that fits.</summary>
    Max,
    /// <summary>A set number of whole stacks per turret, one transfer-key press each.</summary>
    Stacks
}

/// <summary>
/// Turret Manager: switched on, it watches in the background, and every time the player opens a
/// turret's inventory it fills it once — then waits for that panel to close before it looks for
/// the next one. The player opens the turret; the script never touches the turret itself.
///
/// Nothing is calibrated. The panels, the grids and the Transfer All button are placed from the
/// game window and ARK's interface scale (<see cref="TurretInventoryLayout"/>), and what is open
/// and what the player carries are read off pixels, not text (<see cref="TurretInventoryLook"/>):
/// a structure panel with only a few slots is a turret, a copper-and-brass cell is Advanced Rifle
/// Bullets (Auto and Heavy turrets), a pale grey crystal is Element Shards (Tek turret). Only one
/// of the two fits any turret, so the script tries the one the turret already holds, else the one
/// that worked last, and falls back to the other once if nothing moved.
///
/// Every click and press is preceded by the same two checks: ARK is in front, and the turret panel
/// is still open. Either failing ends that fill — never the watcher.
///
/// Switching a turret's power on or off is not part of it; that waits for the owner's decision.
/// </summary>
public sealed class TurretManagerScript : AutomationScriptBase
{
    private const string Key = "turret";

    /// <summary>How often the watcher looks for an open turret.</summary>
    private const int PollMs = 120;

    /// <summary>Polls in a row that must see the turret before it acts — the panel slides in.</summary>
    private const int SightingsToAct = 2;

    /// <summary>Cursor on the stack before the key, so the game has the hover.</summary>
    private const int HoverSettleMs = 50;

    /// <summary>Longest wait, before the lag buffer, for a press or click to show in the turret.</summary>
    private const int VerifyTimeoutMs = 500;

    private const int VerifyPollMs = 40;

    /// <summary>Transfer All clicks per opening, at most.</summary>
    public const int MaxClicks = 10;

    /// <summary>Clicks in a row that changed nothing before Max stops — one swallowed click is normal.</summary>
    public const int QuietClicksToStop = 2;

    /// <summary>Presses in a row that moved nothing before a stack run gives up on that ammo.</summary>
    private const int MissesToStop = 2;

    private readonly IInputSimulator _input;
    private readonly IScreenSampler _sampler;
    private readonly IGameDisplayService _displays;
    private readonly IArkPathProvider _arkPath;

    private TurretInventoryLayout? _layout;
    private double _uiScaling = 1.0;
    private bool _armed = true;
    private int _sightings;
    private TurretInventoryLook.Signature? _lastLogged;

    /// <summary>The ammo that went in last time — the first guess for the next turret on the same wall.</summary>
    private AmmoKind _lastWorked = AmmoKind.Bullet;

    public TurretFill Fill { get; set; } = TurretFill.Max;

    /// <summary>Whole stacks of bullets each turret gets in <see cref="TurretFill.Stacks"/>.</summary>
    public int BulletStacks { get; set; } = 1;

    /// <summary>Whole stacks of shards each Tek turret gets in <see cref="TurretFill.Stacks"/>.</summary>
    public int ShardStacks { get; set; } = 1;

    /// <summary>Added to the waits for the server to answer a transfer — raise it on laggy servers.</summary>
    public int LagBufferMs { get; set; }

    public string TransferKey { get; set; } = "T";

    /// <summary>Whether ARK's inventory tooltips are on — Transfer All then asks for a confirmation.</summary>
    public bool TooltipsOn { get; private set; }

    // ─── Ammo calculator inputs (the page's; kept so they survive a restart) ───
    public int CalcHeavyTurrets { get; set; }
    public int CalcTekTurrets { get; set; }
    public int CalcBullets { get; set; }
    public int CalcShards { get; set; }
    public int CalcBulletStack { get; set; } = TurretAmmoCalculator.DefaultBulletStack;
    public int CalcShardStack { get; set; } = TurretAmmoCalculator.DefaultShardStack;

    public TurretManagerScript(
        IInputSimulator input,
        IScreenSampler sampler,
        IGameDisplayService displays,
        IArkPathProvider arkPath,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<TurretManagerScript> logger)
        : base(Key, "Turret Manager", string.Empty, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _input = input;
        _sampler = sampler;
        _displays = displays;
        _arkPath = arkPath;
        LoadSettings();
        RefreshGameSettings();
    }

    public override bool UsesVision => true;

    /// <summary>Until the owner has watched it fill real turrets, the turret check itself is an inference.</summary>
    public override bool IsExperimental => true;

    protected override void OnStarting()
    {
        try
        {
            if (!Preferences.ContainsKey($"{Key}.transfer"))
                TransferKey = ArkKeyDefaults.For(ArkActions.TransferItem, "T");
        }
        catch (Exception ex) { Logger.LogDebug(ex, "Turret Manager key re-resolve failed"); }

        RefreshGameSettings();
        _armed = true;
        _sightings = 0;
        _layout = null;
    }

    private void RefreshGameSettings()
    {
        try
        {
            var ark = _arkPath.FindArkPath();
            _uiScaling = ArkInventoryLayout.ReadUiScaling(ark);
            TooltipsOn = TurretInventoryLook.ReadTooltipsEnabled(ark);
        }
        catch (Exception ex) { Logger.LogDebug(ex, "Turret Manager could not read GameUserSettings.ini"); }
    }

    protected override Task RunAsync(CancellationToken ct) => RunLoopAsync(PollMs, WatchAsync, foregroundOnly: true, ct);

    /// <summary>One look: act on a turret seen twice in a row, re-arm once it is gone.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        var layout = Layout();
        if (layout is null) return;

        var capture = _sampler.CaptureRegion(layout.WatchRegion);
        if (capture.IsEmpty) return;

        var seen = TurretInventoryLook.Read(capture, layout.WatchRegion.Location, layout);
        // Every change while an inventory frame is (or was) up, at Information: what the check
        // saw on the owner's real turrets is the number the next tuning round needs.
        if (_lastLogged != seen)
        {
            var framed = seen.FrameHits >= 3 || _lastLogged?.FrameHits >= 3;
            _lastLogged = seen;
            if (framed) Logger.LogInformation("Turret Manager sees {Signature} → turret: {IsTurret}", seen, seen.IsTurret);
        }

        if (!seen.IsTurret)
        {
            _sightings = 0;
            _armed = true;
            return;
        }

        if (!_armed || ++_sightings < SightingsToAct) return;

        _armed = false;
        _sightings = 0;
        try
        {
            await FillAsync(layout, seen.Slots, ct);
        }
        catch (FillAborted why)
        {
            Logger.LogInformation("Turret Manager: fill stopped — {Reason}", why.Message);
        }
    }

    /// <summary>The layout for the window as it is now; rebuilt only when the window moved or resized.</summary>
    private TurretInventoryLayout? Layout()
    {
        var client = _displays.GameClientBounds;
        if (client.Width <= 0 || client.Height <= 0) return null;
        if (_layout is { } current && current.Client == client) return current;
        return _layout = new TurretInventoryLayout(client, _uiScaling);
    }

    private async Task FillAsync(TurretInventoryLayout layout, int slots, CancellationToken ct)
    {
        var player = ScanPlayer(layout);
        var bullets = player.Count(k => k == AmmoKind.Bullet);
        var shards = player.Count(k => k == AmmoKind.Shard);
        Logger.LogInformation("Turret Manager: turret with {Slots} slots; player shows {Bullets} bullet and {Shards} shard stacks of {Cells} items",
            slots, bullets, shards, player.Length);

        if (bullets == 0 && shards == 0)
        {
            Warn(Localizer.T("scripts.turret.toast.noammo"), "scripts.turret.toast.noammo");
            return;
        }

        if (Fill == TurretFill.Max)
        {
            await FillMaxAsync(layout, ct);
            return;
        }

        // Ammo already in the turret is the one that fits; otherwise the one that fitted last, then the other.
        var order = TurretAmmo(layout, slots) is { } inTurret
            ? [inTurret]
            : _lastWorked == AmmoKind.Shard ? new[] { AmmoKind.Shard, AmmoKind.Bullet } : new[] { AmmoKind.Bullet, AmmoKind.Shard };

        foreach (var kind in order)
        {
            if ((kind == AmmoKind.Bullet ? bullets : shards) == 0) continue;
            var moved = await FillStacksAsync(layout, kind, kind == AmmoKind.Bullet ? BulletStacks : ShardStacks, ct);
            if (moved > 0)
            {
                _lastWorked = kind;
                TryActivity(Localizer.T("scripts.turret.activity.filled", moved), "success", "scripts.turret.activity.filled");
                return;
            }
        }

        Warn(Localizer.T("scripts.turret.toast.tooknothing"), "scripts.turret.toast.tooknothing");
    }

    /// <summary>Transfer All until two clicks in a row change nothing, at most <see cref="MaxClicks"/>.</summary>
    private async Task FillMaxAsync(TurretInventoryLayout layout, CancellationToken ct)
    {
        int clicks = 0, quiet = 0, changed = 0;
        while (clicks < MaxClicks && quiet < QuietClicksToStop)
        {
            var before = _sampler.CaptureRegion(layout.TurretCellsRegion);
            EnsureStillOpen(layout);
            await _input.ClickAsync(MouseButton.Left, layout.TransferAll, ct: ct);
            clicks++;
            ReportEffect();

            if (await ChangedAsync(layout, before, ct)) { changed++; quiet = 0; }
            else quiet++;
        }

        Logger.LogInformation("Turret Manager: Transfer All {Clicks} clicks, {Changed} changed the turret", clicks, changed);
        if (changed == 0)
        {
            Warn(Localizer.T("scripts.turret.toast.transferall"), "scripts.turret.toast.transferall");
            return;
        }
        TryActivity(Localizer.T("scripts.turret.activity.filled", changed), "success", "scripts.turret.activity.filled");
    }

    /// <summary>
    /// Up to <paramref name="stacks"/> presses of the transfer key on the first stack of
    /// <paramref name="kind"/>, the grid re-read before each (it closes up behind a stack that
    /// left). Returns the presses that moved something into the turret.
    /// </summary>
    private async Task<int> FillStacksAsync(TurretInventoryLayout layout, AmmoKind kind, int stacks, CancellationToken ct)
    {
        var vk = HotkeyParser.TryParseKey(TransferKey, out var k) ? k : 'T';
        int moved = 0, misses = 0;
        while (moved < Math.Clamp(stacks, 1, 100) && misses < MissesToStop)
        {
            var index = Array.IndexOf(ScanPlayer(layout), kind);
            if (index < 0) break; // none of it left in view

            var before = _sampler.CaptureRegion(layout.TurretCellsRegion);
            var cell = layout.PlayerCell(index);
            EnsureStillOpen(layout);
            _input.MoveTo(cell.X, cell.Y);
            await _input.DelayAsync(HoverSettleMs, ct: ct);
            EnsureStillOpen(layout);
            await _input.KeyPressAsync(vk, ct: ct);
            ReportEffect();

            if (await ChangedAsync(layout, before, ct)) { moved++; misses = 0; }
            else misses++;
        }

        Logger.LogInformation("Turret Manager: {Kind} — {Moved} of {Stacks} stacks went in", kind, moved, stacks);
        return moved;
    }

    /// <summary>
    /// Whether the turret's cells changed since <paramref name="before"/>, polled until the server
    /// has had <see cref="VerifyTimeoutMs"/> plus the lag buffer to answer. Judged on the turret's
    /// side only: an ARK tooltip over the player's grid would fake a change there.
    /// </summary>
    private async Task<bool> ChangedAsync(TurretInventoryLayout layout, ScreenCapture before, CancellationToken ct)
    {
        // A stack arriving redraws a whole icon; a number ticking up still moves ~60 px at 1080p.
        var needed = Math.Max(20, (int)(60 * layout.Scale * layout.Scale));
        var polls = Math.Max(1, (VerifyTimeoutMs + Math.Clamp(LagBufferMs, 0, 2000)) / VerifyPollMs);
        for (var i = 0; i < polls; i++)
        {
            await _input.DelayAsync(VerifyPollMs, ct: ct);
            var now = _sampler.CaptureRegion(layout.TurretCellsRegion);
            if (TurretInventoryLook.ChangedPixels(before, now) >= needed) return true;
        }
        return false;
    }

    /// <summary>The check before every click and press: ARK in front, the turret panel still up.</summary>
    private void EnsureStillOpen(TurretInventoryLayout layout)
    {
        if (!Foreground.IsGameForeground()) throw new FillAborted("ARK lost focus");
        var capture = _sampler.CaptureRegion(layout.WatchRegion);
        if (!TurretInventoryLook.Read(capture, layout.WatchRegion.Location, layout).IsTurret)
            throw new FillAborted("the turret panel closed");
    }

    private AmmoKind[] ScanPlayer(TurretInventoryLayout layout)
    {
        var capture = _sampler.CaptureRegion(layout.PlayerRegion);
        return capture.IsEmpty ? [] : TurretInventoryLook.ScanPlayer(capture, layout.PlayerRegion.Location, layout);
    }

    private AmmoKind? TurretAmmo(TurretInventoryLayout layout, int slots)
    {
        var capture = _sampler.CaptureRegion(layout.TurretCellsRegion);
        return capture.IsEmpty ? null : TurretInventoryLook.TurretAmmo(capture, layout.TurretCellsRegion.Location, layout, slots);
    }

    /// <summary>Toast, which also shows over ARK while it has focus.</summary>
    private void Warn(string message, string key)
    {
        try { Notifications.ShowWarning(message); }
        catch { /* notifications are best-effort */ }
        TryActivity(message, "warning", key);
    }

    private sealed class FillAborted(string reason) : Exception(reason);

    public void SaveSettings()
    {
        Normalize();
        TransferKey = string.IsNullOrWhiteSpace(TransferKey) ? "T" : TransferKey.Trim();
        try
        {
            Preferences.Set($"{Key}.fill", (int)Fill);
            Preferences.Set($"{Key}.bulletstacks", BulletStacks);
            Preferences.Set($"{Key}.shardstacks", ShardStacks);
            Preferences.Set($"{Key}.lagbuffer", LagBufferMs);
            Preferences.Set($"{Key}.transfer", TransferKey);
            Preferences.Set($"{Key}.calc.heavy", CalcHeavyTurrets);
            Preferences.Set($"{Key}.calc.tek", CalcTekTurrets);
            Preferences.Set($"{Key}.calc.bullets", CalcBullets);
            Preferences.Set($"{Key}.calc.shards", CalcShards);
            Preferences.Set($"{Key}.calc.bulletstack", CalcBulletStack);
            Preferences.Set($"{Key}.calc.shardstack", CalcShardStack);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Turret Manager SaveSettings failed"); }
        RefreshGameSettings();
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            var fill = Preferences.Get($"{Key}.fill", (int)TurretFill.Max);
            Fill = Enum.IsDefined(typeof(TurretFill), fill) ? (TurretFill)fill : TurretFill.Max;
            BulletStacks = Preferences.Get($"{Key}.bulletstacks", 1);
            ShardStacks = Preferences.Get($"{Key}.shardstacks", 1);
            LagBufferMs = Preferences.Get($"{Key}.lagbuffer", 0);
            TransferKey = Preferences.Get($"{Key}.transfer", ArkKeyDefaults.For(ArkActions.TransferItem, "T"));
            CalcHeavyTurrets = Preferences.Get($"{Key}.calc.heavy", 0);
            CalcTekTurrets = Preferences.Get($"{Key}.calc.tek", 0);
            CalcBullets = Preferences.Get($"{Key}.calc.bullets", 0);
            CalcShards = Preferences.Get($"{Key}.calc.shards", 0);
            CalcBulletStack = Preferences.Get($"{Key}.calc.bulletstack", TurretAmmoCalculator.DefaultBulletStack);
            CalcShardStack = Preferences.Get($"{Key}.calc.shardstack", TurretAmmoCalculator.DefaultShardStack);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Turret Manager LoadSettings failed"); }
        Normalize();
    }

    private void Normalize()
    {
        BulletStacks = Math.Clamp(BulletStacks, 1, 100);
        ShardStacks = Math.Clamp(ShardStacks, 1, 100);
        LagBufferMs = Math.Clamp(LagBufferMs, 0, 1000);
        CalcHeavyTurrets = Math.Clamp(CalcHeavyTurrets, 0, 10000);
        CalcTekTurrets = Math.Clamp(CalcTekTurrets, 0, 10000);
        CalcBullets = Math.Clamp(CalcBullets, 0, 100_000_000);
        CalcShards = Math.Clamp(CalcShards, 0, 100_000_000);
        CalcBulletStack = Math.Clamp(CalcBulletStack, 1, 1_000_000);
        CalcShardStack = Math.Clamp(CalcShardStack, 1, 1_000_000);
    }

    /// <summary>The calculator's even split for the numbers on the page.</summary>
    public (AmmoPlan Bullets, AmmoPlan Shards) Plan() => (
        TurretAmmoCalculator.Plan(CalcHeavyTurrets, CalcBullets, CalcBulletStack, BulletStacks),
        TurretAmmoCalculator.Plan(CalcTekTurrets, CalcShards, CalcShardStack, ShardStacks));

    /// <summary>The calculator's "Use": its per-turret stacks become the Stacks settings.</summary>
    public void UsePlan()
    {
        var (bullets, shards) = Plan();
        if (CalcHeavyTurrets > 0 && bullets.StacksPerTurret > 0) BulletStacks = bullets.StacksPerTurret;
        if (CalcTekTurrets > 0 && shards.StacksPerTurret > 0) ShardStacks = shards.StacksPerTurret;
        Fill = TurretFill.Stacks;
        SaveSettings();
    }
}
