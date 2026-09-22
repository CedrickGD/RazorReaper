using System.Globalization;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>What one inventory cell holds, as far as the turret script cares.</summary>
public enum AmmoKind
{
    Empty,
    /// <summary>Advanced Rifle Bullets — Auto and Heavy turrets.</summary>
    Bullet,
    /// <summary>Element Shards — Tek turrets.</summary>
    Shard,
    Other
}

/// <summary>
/// Where ARK draws the three-panel inventory a structure opens with, worked out from the game's
/// client area and interface scale like <see cref="ArkInventoryLayout"/>, so nobody calibrates it.
/// Offsets from the client centre, read off a 1920x1080 borderless client at <c>UIScaling=1.0</c>
/// (centre 960,540): panel frames at x 82/711 (player), 740/1179 (middle), 1208/1837 (structure),
/// y 69 to 971; both inventory grids start at y 232 with a 93 px pitch, the player's at x 117,
/// the structure's at x 1243; the player's Transfer All button (⇄) is centred at 354,186.
/// </summary>
public sealed class TurretInventoryLayout
{
    private const int Columns = 6;

    /// <summary>Player grid rows scanned for ammo — the seventh ends at y 883, the eighth is cut off.</summary>
    public const int PlayerRows = 7;

    private static readonly Point PlayerGridOffset = new(-843, -308);
    private static readonly Point TurretGridOffset = new(283, -308);
    private static readonly Point TransferAllOffset = new(-606, -354);

    /// <summary>The three frame lines the watcher checks: the middle panel's right edge and both edges of the structure panel.</summary>
    internal static readonly int[] FrameXOffsets = [219, 248, 877];

    /// <summary>Heights the frame lines are checked at — high enough that a short middle panel still reaches them.</summary>
    internal static readonly int[] FrameYOffsets = [-390, -290, -190];

    private const double MeasuredPitch = 93.0;

    public TurretInventoryLayout(Rectangle client, double uiScaling)
    {
        Client = client;
        Scale = ArkInventoryLayout.Scale(client, uiScaling);
        Pitch = MeasuredPitch * Scale;
        PlayerGrid = Project(PlayerGridOffset);
        TurretGrid = Project(TurretGridOffset);
        TransferAll = Project(TransferAllOffset);

        var pad = (int)Math.Ceiling(8 * Scale);
        var left = Project(new Point(FrameXOffsets[0], FrameYOffsets[0]));
        var right = Project(new Point(FrameXOffsets[^1], 0));
        WatchRegion = Rectangle.FromLTRB(
            left.X - pad, left.Y - pad, right.X + pad + 1, TurretGrid.Y + (int)Math.Ceiling(3 * Pitch) + pad);
        TurretCellsRegion = Rectangle.FromLTRB(
            TurretGrid.X, TurretGrid.Y, TurretGrid.X + (int)Math.Ceiling(Columns * Pitch) + 1,
            TurretGrid.Y + (int)Math.Ceiling(2 * Pitch) + 1);
        PlayerRegion = Rectangle.FromLTRB(
            PlayerGrid.X - pad, PlayerGrid.Y - pad,
            PlayerGrid.X + (int)Math.Ceiling(Columns * Pitch) + pad + 1,
            PlayerGrid.Y + (int)Math.Ceiling(PlayerRows * Pitch) + pad + 1);
    }

    public Rectangle Client { get; }

    public double Scale { get; }

    public double Pitch { get; }

    /// <summary>Top-left corner of the player grid (its first line crossing).</summary>
    public Point PlayerGrid { get; }

    /// <summary>Top-left corner of the structure's grid.</summary>
    public Point TurretGrid { get; }

    /// <summary>The player panel's Transfer All button.</summary>
    public Point TransferAll { get; }

    /// <summary>Everything the open-turret check reads, in one capture.</summary>
    public Rectangle WatchRegion { get; }

    /// <summary>The structure's first two rows — what a transfer changes.</summary>
    public Rectangle TurretCellsRegion { get; }

    /// <summary>The scanned rows of the player grid.</summary>
    public Rectangle PlayerRegion { get; }

    public Point FramePoint(int xOffset, int yOffset) => Project(new Point(xOffset, yOffset));

    /// <summary>Centre of cell <paramref name="index"/> (row-major) of the player grid.</summary>
    public Point PlayerCell(int index) => Cell(PlayerGrid, index);

    /// <summary>Centre of cell <paramref name="index"/> (row-major) of the structure's grid.</summary>
    public Point TurretCell(int index) => Cell(TurretGrid, index);

    /// <summary>X of vertical grid line <paramref name="column"/> (0–6) of a grid starting at <paramref name="grid"/>.</summary>
    public int LineX(Point grid, int column) => grid.X + (int)Math.Round(column * Pitch);

    public int RowTop(Point grid, int row) => grid.Y + (int)Math.Round(row * Pitch);

    private Point Cell(Point grid, int index) => new(
        grid.X + (int)((index % Columns + 0.5) * Pitch),
        grid.Y + (int)((index / Columns + 0.5) * Pitch));

    private Point Project(Point offset) => new(
        Client.Left + Client.Width / 2 + (int)Math.Round(offset.X * Scale),
        Client.Top + Client.Height / 2 + (int)Math.Round(offset.Y * Scale));
}

/// <summary>
/// The pixel tests the turret script is built on — no text, so the game language does not matter.
///
/// <b>Open turret.</b> A structure inventory draws a bright 1 px frame round each panel
/// (≈127,231,255 on the owner's screen) and one bordered cell per slot it has: the grid lines of a
/// real slot are ≈23,127,149 against a ≈19,53,54 cell, while the grid ARK draws behind a panel
/// with no slot there is a faint ≈22,64,66 over the world. So the number of bordered cells in the
/// structure grid is the number of slots it has, and a turret is a structure with only a few:
/// measured on the owner's screenshots, a Tek Generator (100 slots) and a Tek Transmitter's
/// tribute tab both border all three scanned rows, a 15-slot container borders 6/6/3.
///
/// <b>Ammo.</b> Share of an item cell's interior (the top label and bottom weight line skipped,
/// as in <see cref="ArmorSlotLook"/>) that is olive/brass (the bullet casing), copper (its tip),
/// cool pale grey (the shard crystal), lavender (Element's core) or white (tool icons). Measured on
/// the owner's crops: bullets olive 0.101–0.126, copper 0.034–0.037, lavender 0; shards grey
/// 0.165–0.167, white 0; Element olive 0.050–0.058, copper 0.040–0.060, lavender 0.003–0.005;
/// the repair-tool icon grey 0.132 but white 0.355; empty cells 0 across the board.
///
/// ponytail: the turret signature is inferred, not measured — the owner's "T01" capture turned
/// out to be a Tek Generator, so no real turret inventory has been seen by this code yet. The
/// structure-slot model matches every inventory in the screenshots; if a turret draws its grid
/// differently, <see cref="MaxTurretSlots"/> and <see cref="BorderedCells"/> are where to look.
/// </summary>
public static class TurretInventoryLook
{
    /// <summary>
    /// Most slots a structure may have and still count as a turret. The Tek turret is reported
    /// at 5; Auto and Heavy turrets are not measured — 10 covers "1000 bullets = 10 stacks".
    /// Anything with more (storage, generators, dinos, the transmitter) never triggers.
    /// </summary>
    public const int MaxTurretSlots = 10;

    /// <summary>Frame samples (of 9) that must show the bright panel frame.</summary>
    public const int FrameHitsNeeded = 7;

    // Grid line: bright enough in G+B, and standing out from the pixels either side of it by this much.
    private const int LineMin = 200;
    private const int LineRidge = 90;

    // Panel frame: pale cyan, and a 1 px ridge against both sides.
    private const int FrameGreenMin = 170;
    private const int FrameBlueMin = 190;
    private const int FrameRidge = 80;

    /// <summary>Line samples (of 5) along one cell edge that must show a bordered line.</summary>
    private const int EdgeHitsNeeded = 3;

    // Cell interior around the centre, in 1080p pixels — as ArmorSlotLook.
    private const int InteriorLeft = -40, InteriorRight = 40, InteriorTop = -26, InteriorBottom = 28;

    public const double BulletOliveMin = 0.080;
    public const double BulletCopperMin = 0.015;
    public const double LavenderMax = 0.002;
    public const double ShardGreyMin = 0.080;
    public const double WhiteMax = 0.020;
    public const double OccupiedMin = 0.010;

    /// <summary>What the watcher saw: frame samples that hit, and bordered cells in each of the three rows.</summary>
    public readonly record struct Signature(int FrameHits, int Row0, int Row1, int Row2)
    {
        public int Slots => Row0 + Row1 + Row2;

        /// <summary>A panel frame, and a structure grid with a handful of slots and nothing on row three.</summary>
        public bool IsTurret =>
            FrameHits >= FrameHitsNeeded
            && Row0 > 0 && Row2 == 0 && Slots <= MaxTurretSlots
            && (Row1 == 0 || Row0 == 6);

        public override string ToString()
            => string.Create(CultureInfo.InvariantCulture, $"frame {FrameHits}/9, rows {Row0}/{Row1}/{Row2}");
    }

    /// <summary>Reads the turret signature out of a capture of <see cref="TurretInventoryLayout.WatchRegion"/>.</summary>
    public static Signature Read(ScreenCapture capture, Point origin, TurretInventoryLayout layout)
    {
        var hits = 0;
        foreach (var x in TurretInventoryLayout.FrameXOffsets)
            foreach (var y in TurretInventoryLayout.FrameYOffsets)
                if (IsFrameAt(capture, origin, layout.FramePoint(x, y), layout.Scale)) hits++;

        return new Signature(
            hits,
            BorderedInRow(capture, origin, layout, layout.TurretGrid, 0),
            BorderedInRow(capture, origin, layout, layout.TurretGrid, 1),
            BorderedInRow(capture, origin, layout, layout.TurretGrid, 2));
    }

    /// <summary>Bordered cells of a grid, row-major over <paramref name="rows"/> rows — the item count's upper bound in the player panel, the slot count in a structure's.</summary>
    public static int BorderedCells(ScreenCapture capture, Point origin, TurretInventoryLayout layout, Point grid, int rows)
    {
        var total = 0;
        for (var row = 0; row < rows; row++)
        {
            var inRow = BorderedInRow(capture, origin, layout, grid, row);
            total += inRow;
            if (inRow < 6) break;
        }
        return total;
    }

    /// <summary>
    /// Bordered cells in one row, from the left: a row of k bordered cells draws k+1 bright
    /// vertical lines, and the lines stop where the cells do.
    /// </summary>
    private static int BorderedInRow(ScreenCapture capture, Point origin, TurretInventoryLayout layout, Point grid, int row)
    {
        var top = layout.RowTop(grid, row);
        var lines = 0;
        for (var column = 0; column <= 6; column++)
        {
            var x = layout.LineX(grid, column);
            var hits = 0;
            for (var s = 1; s <= 5; s++)
            {
                var y = top + (int)Math.Round(layout.Pitch * (0.05 + 0.15 * s));
                if (IsLineAt(capture, origin, x, y, layout.Scale)) hits++;
            }
            if (hits < EdgeHitsNeeded) break;
            lines++;
        }
        return lines >= 2 ? lines - 1 : 0;
    }

    private static bool IsLineAt(ScreenCapture capture, Point origin, int x, int y, double scale)
    {
        var reach = Math.Max(1, (int)Math.Round(1.5 * scale));
        var side = Math.Max(3, (int)Math.Round(2 * scale) + 1);
        for (var dx = -reach; dx <= reach; dx++)
        {
            var c = GreenBlue(capture, origin, x + dx, y);
            if (c < LineMin) continue;
            if (c - GreenBlue(capture, origin, x + dx - side, y) >= LineRidge
                && c - GreenBlue(capture, origin, x + dx + side, y) >= LineRidge)
                return true;
        }
        return false;
    }

    private static bool IsFrameAt(ScreenCapture capture, Point origin, Point at, double scale)
    {
        var reach = Math.Max(2, (int)Math.Round(3 * scale));
        var side = Math.Max(4, (int)Math.Round(3 * scale) + 1);
        for (var dx = -reach; dx <= reach; dx++)
        {
            if (!TryPixel(capture, origin, at.X + dx, at.Y, out var r, out var g, out var b)) continue;
            if (g < FrameGreenMin || b < FrameBlueMin || r > g) continue;
            var c = g + b;
            if (c - GreenBlue(capture, origin, at.X + dx - side, at.Y) >= FrameRidge
                && c - GreenBlue(capture, origin, at.X + dx + side, at.Y) >= FrameRidge)
                return true;
        }
        return false;
    }

    /// <summary>What the cell centred at <paramref name="centre"/> holds.</summary>
    public static AmmoKind Classify(ScreenCapture capture, Point origin, Point centre, double scale)
    {
        var s = Shares(capture, origin, centre, scale);
        if (s.Grey >= ShardGreyMin && s.White < WhiteMax) return AmmoKind.Shard;
        if (s.Olive >= BulletOliveMin && s.Copper >= BulletCopperMin && s.Lavender < LavenderMax) return AmmoKind.Bullet;
        return s.Occupied >= OccupiedMin ? AmmoKind.Other : AmmoKind.Empty;
    }

    /// <summary>The colour shares <see cref="Classify"/> decides on, 0–1 each.</summary>
    public readonly record struct CellShares(double Olive, double Copper, double Grey, double Lavender, double White, double Occupied);

    public static CellShares Shares(ScreenCapture capture, Point origin, Point centre, double scale)
    {
        int olive = 0, copper = 0, grey = 0, lavender = 0, white = 0, occupied = 0, total = 0;
        var left = centre.X + (int)Math.Round(InteriorLeft * scale);
        var right = centre.X + (int)Math.Round(InteriorRight * scale);
        var top = centre.Y + (int)Math.Round(InteriorTop * scale);
        var bottom = centre.Y + (int)Math.Round(InteriorBottom * scale);
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (!TryPixel(capture, origin, x, y, out var r, out var g, out var b)) continue;
                total++;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (r >= g - 6 && g >= b + 6 && max >= 20) olive++;
                if (r >= 90 && r >= g + 12 && g >= b && r - b >= 25) copper++;
                if (r >= 75 && g >= r + 5 && g - r <= 45 && Math.Abs(g - b) <= 14) grey++;
                if (b >= r - 10 && r >= g + 20) lavender++;
                if (min >= 235) white++;
                if (max >= 100) occupied++;
            }
        }
        if (total == 0) return default;
        double d = total;
        return new CellShares(olive / d, copper / d, grey / d, lavender / d, white / d, occupied / d);
    }

    /// <summary>
    /// The player's cells, row-major, from a capture of <see cref="TurretInventoryLayout.PlayerRegion"/>.
    /// Only the bordered cells are read — ARK packs items from the first cell and borders them —
    /// so the world showing through the empty part of the panel is never mistaken for an item.
    /// </summary>
    public static AmmoKind[] ScanPlayer(ScreenCapture capture, Point origin, TurretInventoryLayout layout)
    {
        var bordered = BorderedCells(capture, origin, layout, layout.PlayerGrid, TurretInventoryLayout.PlayerRows);
        var cells = new AmmoKind[bordered];
        for (var i = 0; i < bordered; i++)
            cells[i] = Classify(capture, origin, layout.PlayerCell(i), layout.Scale);
        return cells;
    }

    /// <summary>The ammo already in the turret's <paramref name="slots"/> cells, if any — the type that fits.</summary>
    public static AmmoKind? TurretAmmo(ScreenCapture capture, Point origin, TurretInventoryLayout layout, int slots)
    {
        for (var i = 0; i < slots; i++)
        {
            var kind = Classify(capture, origin, layout.TurretCell(i), layout.Scale);
            if (kind is AmmoKind.Bullet or AmmoKind.Shard) return kind;
        }
        return null;
    }

    /// <summary>A channel has to move by more than this for its pixel to count as changed.</summary>
    private const int ChangeTolerance = 24;

    /// <summary>Pixels that moved between two captures of the same rectangle; 0 when either failed or they differ in size.</summary>
    public static int ChangedPixels(ScreenCapture a, ScreenCapture b)
    {
        if (a.IsEmpty || b.IsEmpty || a.Width != b.Width || a.Height != b.Height || a.Bgra.Length != b.Bgra.Length) return 0;
        var changed = 0;
        for (var p = 0; p + 2 < a.Bgra.Length; p += 4)
        {
            if (Math.Abs(a.Bgra[p] - b.Bgra[p]) > ChangeTolerance
                || Math.Abs(a.Bgra[p + 1] - b.Bgra[p + 1]) > ChangeTolerance
                || Math.Abs(a.Bgra[p + 2] - b.Bgra[p + 2]) > ChangeTolerance)
                changed++;
        }
        return changed;
    }

    /// <summary>
    /// Whether ARK's inventory item tooltips are on, from <c>bEnableInventoryItemTooltips</c> in
    /// the same GameUserSettings.ini <see cref="ArkInventoryLayout.ReadUiScaling"/> reads (the
    /// owner's is UTF-16; the reader follows the byte-order mark). False when unknown.
    /// </summary>
    public static bool ReadTooltipsEnabled(string? arkInstallPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(arkInstallPath)) return false;
            var path = Path.Combine(
                arkInstallPath, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
            if (!File.Exists(path)) return false;

            const string key = "bEnableInventoryItemTooltips=";
            foreach (var line in File.ReadLines(path))
            {
                var text = line.AsSpan().Trim();
                if (text.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    return text[key.Length..].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // The game rewriting the file mid-read is not worth an error; the hint just stays hidden.
        }
        return false;
    }

    private static int GreenBlue(ScreenCapture capture, Point origin, int x, int y)
        => TryPixel(capture, origin, x, y, out _, out var g, out var b) ? g + b : 0;

    private static bool TryPixel(ScreenCapture capture, Point origin, int x, int y, out int r, out int g, out int b)
    {
        var column = x - origin.X;
        var row = y - origin.Y;
        if (column < 0 || row < 0 || column >= capture.Width || row >= capture.Height)
        {
            r = g = b = 0;
            return false;
        }
        var i = (row * capture.Width + column) * 4;
        b = capture.Bgra[i];
        g = capture.Bgra[i + 1];
        r = capture.Bgra[i + 2];
        return true;
    }
}
