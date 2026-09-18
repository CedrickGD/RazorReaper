using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Screen capture through DXGI Desktop Duplication.
///
/// Why this exists: GDI's BitBlt — what <see cref="ScreenSampler"/> used exclusively — cannot see
/// a game that presents in exclusive/independent-flip fullscreen. Measured against ARK: while the
/// game had focus in fullscreen, every grab returned the desktop wallpaper instead of the game
/// (constant maxGreen=178 over a region full of bright green HUD numbers), and an independent
/// capture tool using the same Windows path returned the same thing. Desktop Duplication reads the
/// composed output the display controller is actually scanning out, so it sees the game.
///
/// Written against the raw COM vtables rather than through an interop package: the project has no
/// D3D dependency and this needs exactly eight calls. Every failure path returns false so the caller
/// can fall back to GDI — a wrong picture is worse than a missing one, but no picture at all must
/// never take the app down.
///
/// It duplicates the output ARK is on, not output 0. A duplication covers exactly one monitor, and
/// for its whole life this one was pinned to the first output of the first adapter: with the game
/// on a second screen every capture asked the primary monitor what the game's HUD looked like,
/// found the region outside its frame, refused, and handed the job to a GDI fallback that cannot
/// see a fullscreen game at all. Which output that is gets re-decided whenever the window moves.
/// </summary>
internal sealed unsafe class DesktopDuplicator : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<Rectangle> _gameWindowBounds;
    private readonly IReadOnlyList<AttachedDisplay> _noMonitors = Array.Empty<AttachedDisplay>();
    private readonly object _gate = new();

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _duplication;
    private IntPtr _staging;

    private int _width;
    private int _height;
    private byte[]? _frame;      // last full duplicated output frame, BGRA

    /// <summary>
    /// Where the duplicated output sits on the virtual desktop. Capture regions are virtual-desktop
    /// coordinates and the frame is output-local, so every grab is offset by this. It used to be
    /// assumed to be (0,0) — true only for output 0 of a single-monitor machine, and silently
    /// wrong by the whole width of the primary screen for anyone else.
    /// </summary>
    private Rectangle _outputBounds = Rectangle.Empty;

    /// <summary>Device path of the duplicated output, so a move to another monitor is noticed.</summary>
    private string _outputDevice = string.Empty;

    /// <summary>What the last capture asked for, so a mid-pump rebuild targets the same output.</summary>
    private Rectangle _lastTarget = Rectangle.Empty;

    /// <summary>When the cached frame was last confirmed current (new frame or an explicit timeout).</summary>
    private DateTime _confirmedUtc = DateTime.MinValue;

    /// <summary>Longer than any caller's scan interval, short enough that a freeze is never acted on.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Set only when duplication cannot work on this machine at all (no D3D11, no DXGI 1.2).
    /// Transient failures — secure desktop, mode switch — must NOT land here, or one UAC prompt
    /// would demote the app to the GDI path that cannot see a fullscreen game for the rest of
    /// the run.
    /// </summary>
    private bool _unavailable;

    /// <summary>Backs off after a transient setup failure instead of retrying every single tick.</summary>
    private DateTime _retryAfterUtc = DateTime.MinValue;

    /// <param name="gameWindowBounds">
    /// Where ARK's window is, in virtual-desktop pixels, or an empty rectangle when the game is not
    /// running. A function rather than a value because the window moves and this object does not:
    /// it is asked again on every capture, which is what makes dragging the game to the other
    /// screen mid-run follow rather than break.
    /// </param>
    public DesktopDuplicator(ILogger logger, Func<Rectangle>? gameWindowBounds = null)
    {
        _logger = logger;
        _gameWindowBounds = gameWindowBounds ?? (static () => Rectangle.Empty);
    }

    /// <summary>The output being duplicated right now, or null when none is.</summary>
    public string? ActiveOutputDevice
    {
        get { lock (_gate) return _duplication == IntPtr.Zero ? null : _outputDevice; }
    }

    /// <summary>
    /// Copies <paramref name="region"/> (virtual-desktop pixels) out of the latest frame of the
    /// output ARK is on. False when duplication is unavailable, no frame has ever arrived, or the
    /// region is not on that output — the caller then uses its GDI path.
    /// </summary>
    public bool TryCapture(Rectangle region, out byte[] bgra)
    {
        bgra = Array.Empty<byte>();
        if (_unavailable) return false;

        lock (_gate)
        {
            try
            {
                // The game's window decides the output; the region only decides it when ARK is
                // not running, which is every calibration taken from the desktop.
                var gameBounds = SafeGameBounds();
                var target = gameBounds.Width > 0 && gameBounds.Height > 0 ? gameBounds : region;
                _lastTarget = target;

                if (_duplication != IntPtr.Zero && !StillOnTheRightOutput(target))
                {
                    _logger.LogInformation(
                        "ARK moved off {Old} — rebuilding the duplication for its new display", _outputDevice);
                    Teardown();
                }

                if (_duplication == IntPtr.Zero && !Initialize(target)) return false;

                PumpFrame();
                if (_frame is null) return false;

                // A frame nobody has confirmed is current cannot be handed out as a live capture:
                // after a driver reset the duplication can keep failing quietly, and a script
                // acting on a minutes-old screenshot is worse than one with no picture at all.
                if (DateTime.UtcNow - _confirmedUtc > StaleAfter)
                {
                    _logger.LogWarning("Duplication frame is stale — falling back to GDI");
                    Teardown();
                    return false;
                }

                // Virtual-desktop coordinates in, output-local coordinates out. On a single
                // monitor the offset is zero and this reads like the old code; on a second
                // monitor it is the whole difference between the HUD and the wrong screen.
                var w = region.Width;
                var h = region.Height;
                var left = region.Left - _outputBounds.Left;
                var top = region.Top - _outputBounds.Top;

                // No clamping: only this one output is duplicated, so a region reaching past it
                // belongs to another monitor or to a stale calibration. Returning the nearest
                // edge would be a confident lie about both the size and the content — the caller
                // gets false and uses GDI, which at least covers the windowed case.
                if (left < 0 || top < 0 || left + w > _width || top + h > _height || w <= 0 || h <= 0)
                {
                    return false;
                }

                var outBuf = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(_frame, ((top + y) * _width + left) * 4, outBuf, y * w * 4, w * 4);
                }
                bgra = outBuf;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Desktop duplication capture failed");
                Teardown();
                return false;
            }
        }
    }

    // ── Frame pump ──────────────────────────────────────────────────────────────────────────

    private void PumpFrame()
    {
        // Drain whatever is queued and keep the newest. A timeout is normal (nothing changed on
        // screen), and then the cached frame is still the truth.
        for (var i = 0; i < 4; i++)
        {
            var hr = AcquireNextFrame(_duplication, 12, out var frameInfo, out var resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // Not a failure: DWM is confirming nothing changed, so the cache IS current.
                _confirmedUtc = DateTime.UtcNow;
                return;
            }

            if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL)
            {
                // Routine and transient: mode switch, UAC desktop, or another duplicating
                // process took over. Rebuild rather than giving up on duplication for good.
                Teardown();
                Initialize(_lastTarget);
                return;
            }

            if (hr < 0)
            {
                // DEVICE_REMOVED/DEVICE_RESET after a driver TDR are permanent for this device;
                // every later call returns the same. Drop the state (Teardown clears the cached
                // frame) so the next capture rebuilds or falls back instead of serving a freeze.
                _logger.LogWarning("AcquireNextFrame failed (0x{Hr:X8}) — dropping duplication state", hr);
                Teardown();
                return;
            }

            if (resource == IntPtr.Zero)
            {
                // Frame is held even without a resource — release it or the next acquire fails.
                ReleaseFrame(_duplication);
                return;
            }

            try
            {
                if (frameInfo.LastPresentTime == 0 && _frame is not null)
                {
                    // Cursor-only update; the pixels did not change, but they were confirmed.
                    _confirmedUtc = DateTime.UtcNow;
                    continue;
                }
                CopyToCache(resource);
                _confirmedUtc = DateTime.UtcNow;
            }
            finally
            {
                Marshal.Release(resource);
                ReleaseFrame(_duplication);
            }
        }
    }

    private void CopyToCache(IntPtr resource)
    {
        var iid = IID_ID3D11Texture2D;
        var hr = Marshal.QueryInterface(resource, in iid, out var texture);
        if (hr < 0 || texture == IntPtr.Zero) return;

        try
        {
            CopyResource(_context, _staging, texture);

            var mapped = MapStaging();
            if (mapped.Data == IntPtr.Zero) return;

            try
            {
                _frame ??= new byte[_width * _height * 4];
                for (var y = 0; y < _height; y++)
                {
                    Marshal.Copy(mapped.Data + y * (int)mapped.RowPitch, _frame, y * _width * 4, _width * 4);
                }
            }
            finally
            {
                Unmap(_context, _staging, 0);
            }
        }
        finally
        {
            Marshal.Release(texture);
        }
    }

    // ── Setup ───────────────────────────────────────────────────────────────────────────────

    /// <summary>ARK's window, never throwing into a capture: a bad bounds read must cost a frame, not the app.</summary>
    private Rectangle SafeGameBounds()
    {
        try { return _gameWindowBounds(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Game window bounds lookup threw — choosing the output by region instead");
            return Rectangle.Empty;
        }
    }

    /// <summary>
    /// True when the duplication already covers the output <paramref name="target"/> belongs to.
    /// Enumerating outputs costs a DXGI factory, so this compares against the cheap GDI monitor
    /// list: both report the same <c>\\.\DISPLAYn</c> device names.
    /// </summary>
    private bool StillOnTheRightOutput(Rectangle target)
    {
        var monitors = SafeMonitorList();
        if (monitors.Count == 0) return true;   // nothing to compare against — leave it alone

        var wanted = MonitorSelection.Choose(monitors, target);
        return wanted is null || string.Equals(wanted.DeviceName, _outputDevice, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<AttachedDisplay> SafeMonitorList()
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iidFactory = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iidFactory, out factory) < 0) return _noMonitors;
            return EnumerateOutputs(factory).Select(o => o.Monitor).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Output enumeration threw — keeping the current duplication");
            return _noMonitors;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    /// <summary>An output of an adapter, and where it sits on the virtual desktop.</summary>
    private sealed record OutputSlot(int AdapterIndex, int OutputIndex, AttachedDisplay Monitor);

    /// <summary>
    /// Every attached output of every adapter. Both indices are kept because duplication needs
    /// them: the D3D11 device has to be created on the adapter that owns the output, or
    /// DuplicateOutput refuses it — which is the trap behind "just pass a different index".
    /// </summary>
    private List<OutputSlot> EnumerateOutputs(IntPtr factory)
    {
        var slots = new List<OutputSlot>();
        for (var a = 0u; a < 8; a++)
        {
            if (EnumAdapters1(factory, a, out var adapter) < 0 || adapter == IntPtr.Zero) break;
            try
            {
                for (var o = 0u; o < 8; o++)
                {
                    if (EnumOutputs(adapter, o, out var output) < 0 || output == IntPtr.Zero) break;
                    try
                    {
                        if (TryGetOutputInfo(output, out var device, out var bounds, out var attached) && attached)
                            slots.Add(new OutputSlot((int)a, (int)o, new AttachedDisplay(device, bounds, bounds.Left == 0 && bounds.Top == 0)));
                    }
                    finally { Marshal.Release(output); }
                }
            }
            finally { Marshal.Release(adapter); }
        }
        return slots;
    }

    /// <summary>
    /// Builds device + duplication for the output <paramref name="target"/> is on. Distinguishes
    /// "this machine cannot do it" (latched in <see cref="_unavailable"/>) from "not right now"
    /// (backed off via <see cref="_retryAfterUtc"/>), because DuplicateOutput legitimately fails
    /// while the secure desktop is up or during a mode switch, and those must not cost the app
    /// its only fullscreen-capable capture path.
    /// </summary>
    private bool Initialize(Rectangle target)
    {
        if (DateTime.UtcNow < _retryAfterUtc) return false;

        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
        var transient = false;
        try
        {
            var iidFactory = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iidFactory, out factory) < 0) { _unavailable = true; return false; }

            var slots = EnumerateOutputs(factory);
            if (slots.Count == 0)
            {
                // No attached output at all: a remote session or a laptop with the lid shut.
                // Transient, not a verdict on the machine.
                _logger.LogDebug("No attached DXGI output — retrying shortly, GDI meanwhile");
                transient = true;
                return false;
            }

            var wanted = MonitorSelection.Choose(slots.Select(s => s.Monitor).ToList(), target);
            var slot = slots.FirstOrDefault(s => s.Monitor == wanted) ?? slots[0];

            if (EnumAdapters1(factory, (uint)slot.AdapterIndex, out adapter) < 0 || adapter == IntPtr.Zero)
            {
                _unavailable = true;
                return false;
            }

            // The device must live on the adapter that owns the output, and a non-null adapter
            // means the driver type has to be UNKNOWN — passing HARDWARE alongside one is an
            // outright E_INVALIDARG.
            var levels = stackalloc uint[] { 0xb000 /* 11_0 */, 0xa100 /* 10_1 */, 0xa000 /* 10_0 */ };
            var hr = D3D11CreateDevice(
                adapter, 0 /* UNKNOWN */, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */,
                levels, 3, 7 /* SDK_VERSION */, out _device, out _, out _context);
            if (hr < 0 || _device == IntPtr.Zero)
            {
                _logger.LogWarning("D3D11CreateDevice failed (0x{Hr:X}) — falling back to GDI capture", hr);
                _unavailable = true;
                return false;
            }

            if (EnumOutputs(adapter, (uint)slot.OutputIndex, out output) < 0 || output == IntPtr.Zero)
            {
                _unavailable = true;
                return false;
            }

            var iidOutput1 = IID_IDXGIOutput1;
            if (Marshal.QueryInterface(output, in iidOutput1, out output1) < 0) { _unavailable = true; return false; }

            if (DuplicateOutput(output1, _device, out _duplication) < 0 || _duplication == IntPtr.Zero)
            {
                // Transient in practice: secure desktop, mode switch, or another process holding
                // the duplication. Back off and try again instead of latching.
                _logger.LogDebug("DuplicateOutput unavailable right now — retrying shortly, GDI meanwhile");
                transient = true;
                return false;
            }

            GetDuplDesc(_duplication, out var desc);
            _width = (int)desc.ModeDesc.Width;
            _height = (int)desc.ModeDesc.Height;
            if (_width <= 0 || _height <= 0) { transient = true; return false; }

            _staging = CreateStagingTexture(_width, _height);
            if (_staging == IntPtr.Zero) { _unavailable = true; return false; }

            _outputBounds = slot.Monitor.Bounds;
            _outputDevice = slot.Monitor.DeviceName;

            _logger.LogInformation(
                "Desktop duplication ready on {Device} (Monitor {Index}, {W}x{H} at {X},{Y})",
                _outputDevice, slot.Monitor.Index, _width, _height, _outputBounds.Left, _outputBounds.Top);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Desktop duplication unavailable — falling back to GDI capture");
            _unavailable = true;
            return false;
        }
        finally
        {
            if (output1 != IntPtr.Zero) Marshal.Release(output1);
            if (output != IntPtr.Zero) Marshal.Release(output);
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
            if (factory != IntPtr.Zero) Marshal.Release(factory);

            // A half-built attempt must not leave the device and context behind — this runs on
            // every failed tick otherwise and leaks a D3D11 device each time.
            if (_duplication == IntPtr.Zero)
            {
                if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
                if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
                if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
                if (transient) _retryAfterUtc = DateTime.UtcNow.AddSeconds(3);
            }
        }
    }

    private IntPtr CreateStagingTexture(int w, int h)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
            SampleDescCount = 1,
            SampleDescQuality = 0,
            Usage = 3,   // D3D11_USAGE_STAGING
            BindFlags = 0,
            CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
            MiscFlags = 0,
        };
        return CreateTexture2D(_device, ref desc, IntPtr.Zero, out var tex) < 0 ? IntPtr.Zero : tex;
    }

    private void Teardown()
    {
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
        if (_duplication != IntPtr.Zero) { Marshal.Release(_duplication); _duplication = IntPtr.Zero; }
        if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        _frame = null;
        _confirmedUtc = DateTime.MinValue;

        // The offset belongs to the torn-down duplication. Leaving it behind would translate the
        // next output's regions by the last output's origin — pixels from the right monitor at
        // the wrong place, which is harder to spot than no pixels at all.
        _outputBounds = Rectangle.Empty;
        _outputDevice = string.Empty;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        lock (_gate) Teardown();
    }

    // ── COM plumbing ────────────────────────────────────────────────────────────────────────
    // Called through the vtable by index instead of declaring the full interfaces: this needs
    // six methods out of six interfaces, and a mis-declared interface is a silent crash.

    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);

    private static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static void* Vtbl(IntPtr obj, int index) =>
        (void*)Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size);

    // IDXGIFactory1::EnumAdapters1 — vtable slot 12.
    private static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
    {
        fixed (IntPtr* p = &adapter)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(factory, 12))(factory, index, p);
    }

    // IDXGIAdapter::EnumOutputs — slot 7.
    private static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        fixed (IntPtr* p = &output)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(adapter, 7))(adapter, index, p);
    }

    // IDXGIOutput::GetDesc — slot 7 (IUnknown 0-2, IDXGIObject 3-6). Same index as
    // IDXGIAdapter::EnumOutputs above, and for the same reason: both derive from IDXGIObject.
    private static bool TryGetOutputInfo(IntPtr output, out string deviceName, out Rectangle bounds, out bool attached)
    {
        DXGI_OUTPUT_DESC desc;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)Vtbl(output, 7))(output, &desc);
        if (hr < 0)
        {
            deviceName = string.Empty;
            bounds = Rectangle.Empty;
            attached = false;
            return false;
        }

        // desc is a stack local, so its fixed buffer is already pinned and converts straight to
        // a char*; the string runs to the first NUL, which is how DXGI writes it.
        deviceName = new string(desc.DeviceName);
        bounds = Rectangle.FromLTRB(
            desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
            desc.DesktopCoordinates.Right, desc.DesktopCoordinates.Bottom);
        attached = desc.AttachedToDesktop != 0 && bounds.Width > 0 && bounds.Height > 0;
        return true;
    }

    // IDXGIOutput1::DuplicateOutput — slot 22.
    private static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        fixed (IntPtr* p = &duplication)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(output1, 22))(output1, device, p);
    }

    // IDXGIOutputDuplication::GetDesc — slot 7 (void return).
    private static void GetDuplDesc(IntPtr dupl, out DXGI_OUTDUPL_DESC desc)
    {
        DXGI_OUTDUPL_DESC local;
        ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTDUPL_DESC*, void>)Vtbl(dupl, 7))(dupl, &local);
        desc = local;
    }

    // IDXGIOutputDuplication::AcquireNextFrame — slot 8.
    private static int AcquireNextFrame(IntPtr dupl, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
    {
        DXGI_OUTDUPL_FRAME_INFO local;
        IntPtr res;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)Vtbl(dupl, 8))
            (dupl, timeoutMs, &local, &res);
        info = local;
        resource = res;
        return hr;
    }

    // IDXGIOutputDuplication::ReleaseFrame — slot 14.
    private static int ReleaseFrame(IntPtr dupl) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(dupl, 14))(dupl);

    // ID3D11Device::CreateTexture2D — slot 5.
    private static int CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initial, out IntPtr texture)
    {
        fixed (D3D11_TEXTURE2D_DESC* d = &desc)
        fixed (IntPtr* p = &texture)
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Vtbl(device, 5))
                (device, d, initial, p);
    }

    // ID3D11DeviceContext::CopyResource — slot 47.
    private static void CopyResource(IntPtr context, IntPtr dst, IntPtr src) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(context, 47))(context, dst, src);

    // ID3D11DeviceContext::Map — slot 14.
    private (IntPtr Data, uint RowPitch) MapStaging()
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Vtbl(_context, 14))
            (_context, _staging, 0, 1 /* MAP_READ */, 0, &mapped);
        return hr < 0 ? (IntPtr.Zero, 0) : (mapped.pData, mapped.RowPitch);
    }

    // ID3D11DeviceContext::Unmap — slot 15.
    private static void Unmap(IntPtr context, IntPtr resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(context, 15))(context, resource, subresource);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        uint* featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr context);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_DESC
    {
        public DXGI_MODE_DESC ModeDesc;
        public uint Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        // WCHAR[32] inline, not a marshalled string: this struct is written through a raw
        // function pointer, where no marshaller runs.
        public fixed char DeviceName[32];
        public NATIVERECT DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVERECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MODE_DESC
    {
        public uint Width, Height;
        public uint RefreshNumerator, RefreshDenominator;
        public uint Format;
        public uint ScanlineOrdering, Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_POINTER_POSITION
    {
        public int X, Y;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleDescCount, SampleDescQuality;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
