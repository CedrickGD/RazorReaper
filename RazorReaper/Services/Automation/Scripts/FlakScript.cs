using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Armor auto-swap: watches the durability numbers ARK prints next to the equipped armor icons and
/// re-equips a fresh piece the moment one drops below the threshold. Works with any armor set —
/// flak, riot, tek — because it only ever reads numbers and presses hotbar keys.
///
/// Two rules shape the whole design:
///
/// * <b>It must not interrupt the fight.</b> No inventory, no menus, no mouse. Equipping straight
///   from a hotbar slot is the one way ARK swaps a piece without taking the screen away, so the
///   spare set lives on the hotbar and this presses those keys. That is also why each row gets its
///   own key: the helmet slot has to pull the spare helmet, not whatever is on slot 0.
/// * <b>The threshold is absolute.</b> Servers multiply armor durability (100x and up), so a
///   percentage would mean something different on every server; "below 50 points left" always
///   means the same thing. Hence a plain number, defaulted to 50.
/// </summary>
public sealed class FlakScript : CalibratableScriptBase
{
    private const string Key = "flak";

    /// <summary>Armor rows ARK can show at once: helmet, chest, gauntlets, legs, boots, plus a spare.</summary>
    public const int MaxRows = 6;

    private readonly IInputSimulator _input;
    private readonly DurabilityReader _reader;

    /// <summary>Swap as soon as a piece is at or below this many durability points.</summary>
    public int DurabilityThreshold { get; set; } = 50;

    /// <summary>
    /// Hotbar key per armor row, top to bottom as ARK draws them. Empty means "ignore this row" —
    /// a row you have no spare for should never trigger a keypress.
    /// </summary>
    public string[] RowKeys { get; set; } = new string[MaxRows];

    public int ScanIntervalMs { get; set; } = 1000;

    /// <summary>Everything the last scan read, one entry per row — shown live on the Scripts page.</summary>
    public string LastReading { get; private set; } = "";

    /// <summary>Lowest durability the last scan saw, or null when nothing was readable.</summary>
    public int? Lowest { get; private set; }

    /// <summary>When the last swap key went out, so the page can say when that was.</summary>
    public DateTime? LastSwapUtc { get; private set; }

    /// <summary>
    /// OCR is what triggers a key press here, and OCR lies: one obscured frame reads "1 soo" and
    /// the fragment "1" would look like a broken piece. A press therefore needs the same
    /// sub-threshold verdict from two scans in a row, per row.
    /// </summary>
    private readonly bool[] _pendingLow = new bool[MaxRows];

    /// <summary>
    /// A swapped piece keeps reading low until ARK redraws the row, and a spare that is used up
    /// keeps reading low forever. Both would hammer the hotbar, so each row waits after a press.
    /// </summary>
    private readonly DateTime[] _rowCooldownUntil = new DateTime[MaxRows];

    private static readonly TimeSpan RowCooldown = TimeSpan.FromSeconds(6);

    public FlakScript(
        IInputSimulator input,
        DurabilityReader reader,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<FlakScript> logger)
        : base(Key, "Armor Swap", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        _reader = reader;
        LoadSettings();
    }

    /// <summary>No snapshot matching — the region is read as text, so the reference/mask
    /// workflow neither applies nor appears on the page.</summary>
    public override bool UsesReference => false;

    public override string RegionTitle => "Durability numbers";

    protected override bool CanStart(out string? reason)
    {
        if (!HasRegion) { reason = "Calibrate the durability numbers first."; return false; }
        if (RowKeys.All(string.IsNullOrWhiteSpace))
        {
            reason = "Set the hotbar key for at least one armor row.";
            return false;
        }
        reason = null;
        return true;
    }

    protected override Task RunAsync(CancellationToken ct)
    {
        Array.Clear(_pendingLow);
        Array.Fill(_rowCooldownUntil, DateTime.MinValue);

        return RunLoopAsync(ScanIntervalMs, async c =>
        {
            if (!TryGetRegion(out var region)) return;

            var rows = await _reader.ReadAsync(region, c);
            var values = rows.Where(r => r.Value is not null).Select(r => r.Value!.Value).ToList();

            Lowest = values.Count > 0 ? values.Min() : null;
            // "1500 · ? · 42": an unreadable row stays visible as ?, so a bad calibration shows
            // itself instead of silently shrinking the list.
            LastReading = rows.Count > 0
                ? string.Join(" · ", rows.Select(r => r.Value?.ToString() ?? "?"))
                : "nothing readable";
            RaiseChanged();

            var now = DateTime.UtcNow;
            foreach (var band in rows)
            {
                // Slot comes from where the row sits on screen, not from its position in the
                // list. A piece that breaks stops rendering, and with list positions every row
                // beneath it would shift up one slot — the script would then press the boots key
                // for the chest piece. The Y coordinate stays put.
                var slot = SlotOf(band, region.Height);
                if (slot < 0 || slot >= MaxRows) continue;

                var key = RowKeys[slot];
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (now < _rowCooldownUntil[slot]) continue;

                if (band.Value is not { } value || value > DurabilityThreshold)
                {
                    _pendingLow[slot] = false;
                    continue;
                }

                // A key that cannot be sent must not silently become hotbar 0 — that would pull
                // whatever sits in slot 0 in the middle of a fight.
                if (!HotkeyParser.TryParseKey(key, out var vk))
                {
                    Logger.LogWarning("Armor swap: row {Slot} key '{Key}' is not a usable hotbar key — skipped",
                        slot + 1, key);
                    _pendingLow[slot] = false;
                    continue;
                }

                // First sub-threshold scan only arms; the second one fires. One frame of OCR
                // noise then costs a scan interval, not an armor piece off the hotbar.
                if (!_pendingLow[slot])
                {
                    _pendingLow[slot] = true;
                    continue;
                }
                _pendingLow[slot] = false;
                _rowCooldownUntil[slot] = now + RowCooldown;

                // The scan above takes a few hundred ms; stopping the script or alt-tabbing in
                // that window must not still produce a keystroke into whatever is now in front.
                if (c.IsCancellationRequested || !Foreground.IsGameForeground()) return;

                Logger.LogInformation("Armor swap: row {Slot} at {Value} (<= {Threshold}) — pressing '{Key}'",
                    slot + 1, value, DurabilityThreshold, key);

                await _input.KeyPressAsync(vk, ct: c);
                LastSwapUtc = DateTime.UtcNow;
                TryActivity($"Armor swapped — row {slot + 1} was at {value}", "success");
                RaiseChanged();
            }
        }, foregroundOnly: true, ct);
    }

    /// <summary>
    /// Which armor slot a detected row belongs to, from its vertical position inside the
    /// calibrated region. ARK draws the rows evenly spaced, so the region divided into
    /// <see cref="MaxRows"/> equal bands maps a row's centre onto a stable slot even when
    /// rows above it disappear.
    /// </summary>
    private static int SlotOf(DurabilityBand band, int regionHeight)
    {
        if (regionHeight <= 0) return -1;
        var centre = (band.Top + band.Bottom) / 2.0;
        var slot = (int)(centre * MaxRows / regionHeight);
        return Math.Clamp(slot, 0, MaxRows - 1);
    }

    protected override void OnStopped()
    {
        LastReading = "";
        Lowest = null;
        LastSwapUtc = null;
        Array.Clear(_pendingLow);
        Array.Fill(_rowCooldownUntil, DateTime.MinValue);
    }

    public void SaveSettings()
    {
        DurabilityThreshold = Math.Clamp(DurabilityThreshold, 1, 100000);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 500, 10000);
        NormalizeKeys();
        try
        {
            Preferences.Set($"{Key}.threshold2", DurabilityThreshold);
            Preferences.Set($"{Key}.scaninterval", ScanIntervalMs);
            Preferences.Set($"{Key}.rowkeys", string.Join(",", RowKeys));
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Armor swap SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            // ".threshold2": the original ".threshold" held a match percentage from the
            // snapshot-matching version, which would read as an absurd durability if reused.
            DurabilityThreshold = Preferences.Get($"{Key}.threshold2", 50);
            ScanIntervalMs = Preferences.Get($"{Key}.scaninterval", 1000);

            var stored = Preferences.Get($"{Key}.rowkeys", "");
            if (stored.Length > 0)
            {
                var parts = stored.Split(',');
                for (var i = 0; i < MaxRows && i < parts.Length; i++) RowKeys[i] = parts[i];
            }
            else
            {
                // Migration from the single-key version: whatever it held becomes row 1.
                var legacy = Preferences.Get($"{Key}.equip", "");
                if (legacy.Length > 0) RowKeys[0] = legacy;
            }
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Armor swap LoadSettings failed"); }

        DurabilityThreshold = Math.Clamp(DurabilityThreshold, 1, 100000);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 500, 10000);
        NormalizeKeys();
    }

    private void NormalizeKeys()
    {
        for (var i = 0; i < MaxRows; i++)
        {
            // Commas are the separator in the persisted form, so one inside a key would split
            // the whole mapping on the next load and shift every slot.
            RowKeys[i] = (RowKeys[i] ?? "").Trim().Replace(",", "");
        }
    }

    /// <summary>True when the key can actually be sent — the page uses it to flag a typo early.</summary>
    public static bool IsUsableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && HotkeyParser.TryParseKey(key, out _);
}
