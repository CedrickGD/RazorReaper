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
/// D3D dependency and this needs exactly six calls. Every failure path returns false so the caller
/// can fall back to GDI — a wrong picture is worse than a missing one, but no picture at all must
/// never take the app down.
/// </summary>
internal sealed unsafe class DesktopDuplicator : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _duplication;
    private IntPtr _staging;

    private int _width;
    private int _height;
    private byte[]? _frame;      // last full desktop frame, BGRA

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

    public DesktopDuplicator(ILogger logger) => _logger = logger;

    /// <summary>
    /// Copies <paramref name="region"/> out of the latest desktop frame. False when duplication is
    /// unavailable or no frame has ever arrived — the caller then uses its GDI path.
    /// </summary>
    public bool TryCapture(Rectangle region, out byte[] bgra)
    {
        bgra = Array.Empty<byte>();
        if (_unavailable) return false;

        lock (_gate)
        {
            try
            {
                if (_duplication == IntPtr.Zero && !Initialize()) return false;

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

                // No clamping: only this one output is duplicated, so a region reaching past it
                // belongs to another monitor or to a stale calibration. Returning the nearest
                // edge would be a confident lie about both the size and the content — the caller
                // gets false and uses GDI, which at least covers the windowed case.
                var w = region.Width;
                var h = region.Height;
                if (region.Left < 0 || region.Top < 0 || region.Right > _width || region.Bottom > _height
                    || w <= 0 || h <= 0)
                {
                    return false;
                }
                var left = region.Left;
                var top = region.Top;

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
                Initialize();
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

    /// <summary>
    /// Builds device + duplication. Distinguishes "this machine cannot do it" (latched in
    /// <see cref="_unavailable"/>) from "not right now" (backed off via <see cref="_retryAfterUtc"/>),
    /// because DuplicateOutput legitimately fails while the secure desktop is up or during a
    /// mode switch, and those must not cost the app its only fullscreen-capable capture path.
    /// </summary>
    private bool Initialize()
    {
        if (DateTime.UtcNow < _retryAfterUtc) return false;

        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
        var transient = false;
        try
        {
            var levels = stackalloc uint[] { 0xb000 /* 11_0 */, 0xa100 /* 10_1 */, 0xa000 /* 10_0 */ };
            var hr = D3D11CreateDevice(
                IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */,
                levels, 3, 7 /* SDK_VERSION */, out _device, out _, out _context);
            if (hr < 0 || _device == IntPtr.Zero)
            {
                _logger.LogWarning("D3D11CreateDevice failed (0x{Hr:X}) — falling back to GDI capture", hr);
                _unavailable = true;
                return false;
            }

            var iidFactory = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iidFactory, out factory) < 0) { _unavailable = true; return false; }

            // Adapter 0 / output 0. TryCapture refuses any region outside this output's frame,
            // so a HUD on a second monitor falls back to GDI rather than reading the wrong
            // screen — picking the output by DesktopCoordinates is the next step if that comes up.
            if (EnumAdapters1(factory, 0, out adapter) < 0) { _unavailable = true; return false; }
            if (EnumOutputs(adapter, 0, out output) < 0) { _unavailable = true; return false; }

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

            _logger.LogInformation("Desktop duplication ready ({W}x{H})", _width, _height);
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
