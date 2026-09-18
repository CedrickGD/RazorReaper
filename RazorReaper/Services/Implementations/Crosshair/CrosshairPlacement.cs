using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// The pure geometry of "put the crosshair exactly on the middle of the screen". Everything in
/// here is integer/float arithmetic with no GDI+ or Win32 dependency, so it is directly unit
/// testable — which is the point: the centring chain used to be three lines of inline arithmetic
/// spread over the renderer and the overlay, and a half-pixel error in any of them is invisible
/// in code review but obvious on screen.
///
/// The chain has three links:
///  1. <see cref="CanvasOrigin"/> — where inside the render bitmap world-coordinate (0,0) sits.
///  2. <see cref="StrokeStart"/>  — where a stroke of a given thickness starts, so that it lands
///     on whole pixels instead of straddling a pixel boundary at 50 % alpha.
///  3. <see cref="AxisTopLeft"/>  — where the finished bitmap is placed on the monitor.
///
/// Coordinate convention throughout: a pixel with index <c>i</c> covers the continuous interval
/// <c>[i, i+1)</c>, so its centre is at <c>i + 0.5</c> and the boundary between pixel <c>i-1</c>
/// and <c>i</c> is the integer <c>i</c>. This is the convention GDI+ uses for fills with
/// <c>PixelOffsetMode.Half</c>, which is what the renderer sets.
///
/// DPI: every coordinate here is a PHYSICAL pixel. The overlay's UI thread runs with
/// <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c> (and the app manifest declares PerMonitorV2
/// process-wide), so the monitor rectangles returned by <c>GetMonitorInfo</c> and the coordinates
/// consumed by <c>UpdateLayeredWindow</c> are both physical and the scale factor cancels out —
/// there is no per-DPI correction to apply, and applying one would introduce the very error it
/// looks like it is preventing. <see cref="AxisTopLeft"/> is therefore scale-independent by
/// construction; the unit tests pin that down for 100 / 125 / 150 % displays.
/// </summary>
internal static class CrosshairPlacement
{
    /// <summary>
    /// World origin inside a square render canvas: the canvas's geometric centre.
    ///
    /// <para>The canvas size is always even (<c>CrosshairRenderer.ComputeCanvasSize</c> guarantees
    /// it), so the geometric centre is an integer — i.e. it lands on a pixel BOUNDARY, not on a
    /// pixel centre. That is deliberate and it is what makes the placement exact: the bitmap is
    /// positioned so this point coincides with the monitor's own geometric centre, which on an
    /// even-width screen is also a pixel boundary.</para>
    ///
    /// <para>The previous value was <c>(canvasSize - 1) / 2f</c>, half a pixel up and to the left
    /// of the geometric centre. That put every shape half a pixel off the aim point AND smeared
    /// even-thickness strokes across an extra column at 50 % alpha, because an even-width stroke
    /// centred on a pixel centre covers two half pixels at its edges.</para>
    /// </summary>
    public static float CanvasOrigin(int canvasSize) => canvasSize / 2f;

    /// <summary>
    /// Leading edge (relative to the world origin) of a stroke <paramref name="thickness"/> pixels
    /// wide that should be centred on the origin, snapped so the stroke covers whole pixels.
    ///
    /// <para>Even thickness snaps exactly: a 2-px stroke starts at -1 and covers the two pixels
    /// either side of the boundary. Odd thickness cannot be symmetric about a boundary at all, so
    /// it is biased half a pixel towards the top-left (see <see cref="StrokeResidual"/>) — that is
    /// the smallest possible error and it keeps the stroke crisp. Rendering it symmetrically
    /// instead would cost a 50 %-alpha fringe on both sides, which reads as a blurry, thicker
    /// crosshair and was the more visible of the two defects.</para>
    /// </summary>
    public static int StrokeStart(int thickness) => -((Math.Max(1, thickness) + 1) / 2);

    /// <summary>
    /// How far the centre of a <see cref="StrokeStart"/>-snapped stroke ends up from the world
    /// origin: 0 for even thickness, -0.5 px (up/left) for odd thickness. Exposed so tests can
    /// assert the bound rather than re-deriving it.
    /// </summary>
    public static float StrokeResidual(int thickness) => Math.Max(1, thickness) % 2 == 0 ? 0f : -0.5f;

    /// <summary>
    /// Top-left screen coordinate, on one axis, at which a bitmap of <paramref name="bitmapExtent"/>
    /// pixels must be placed so that its <see cref="CanvasOrigin"/> lands on the monitor's centre,
    /// plus the user's manual <paramref name="offset"/>.
    ///
    /// <para>Uses floating-point for the centre so an odd monitor extent (or, defensively, an odd
    /// bitmap) resolves to the nearest whole pixel instead of silently truncating: the old
    /// <c>monitor.Width / 2 - bmp.Width / 2</c> did two integer divisions, each of which loses half
    /// a pixel downwards, and on an odd-width display they do not cancel.</para>
    /// </summary>
    public static int AxisTopLeft(int monitorOrigin, int monitorExtent, int bitmapExtent, int offset)
    {
        var centre = monitorOrigin + monitorExtent / 2.0;
        var topLeft = centre - bitmapExtent / 2.0;
        // Floor(x + 0.5) rather than Math.Round: deterministic on .5 (always towards +infinity)
        // and monotone across the negative coordinates of a left-hand monitor, where
        // MidpointRounding.AwayFromZero would round the two halves of the desktop in opposite
        // directions and make a left monitor land one pixel differently from a right one.
        return (int)Math.Floor(topLeft + 0.5) + offset;
    }

    /// <summary>Both axes at once — what the overlay actually calls.</summary>
    public static (int X, int Y) ScreenTopLeft(MonitorInfo monitor, int bitmapWidth, int bitmapHeight, int offsetX, int offsetY)
        => (AxisTopLeft(monitor.X, monitor.Width, bitmapWidth, offsetX),
            AxisTopLeft(monitor.Y, monitor.Height, bitmapHeight, offsetY));
}
