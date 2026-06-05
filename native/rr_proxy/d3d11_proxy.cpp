// rr d3d11.dll proxy for ARK — hooks ID3D11Device::CreateTexture2D so we can SEE and REPLACE
// textures as the game creates them (the ReShade/texture-mod technique). Loaded by the game
// itself (proxy in Win64) — no injection. All real d3d11 exports forward to d3d11orig.dll via
// exports_gen_d3d11.h; we implement only D3D11CreateDevice(+AndSwapChain) to install the hook.
//
// First visible proof: BC3 (DXT5) textures shaped like a sky (square, 256–2048) get their
// pixels replaced with solid red. If the sky / UI turns red, the hook works and we can swap
// in a real custom image next.

#include <windows.h>
#include <d3d11.h>
#include <cstdio>
#include <cstdint>
#include "exports_gen_d3d11.h"

// Export our implementations under the real names (renamed to avoid clashing with the
// dllimport declarations in d3d11.h).
#pragma comment(linker, "/EXPORT:D3D11CreateDevice=rrD3D11CreateDevice")
#pragma comment(linker, "/EXPORT:D3D11CreateDeviceAndSwapChain=rrD3D11CreateDeviceAndSwapChain")

static void Log(const char* msg)
{
    char tmp[MAX_PATH]; DWORD n = GetTempPathA(MAX_PATH, tmp);
    if (n == 0 || n > MAX_PATH) return;
    char path[MAX_PATH]; sprintf_s(path, "%srr_live.log", tmp);
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, nullptr, FILE_END);
    char line[1024]; int len = sprintf_s(line, "[t=%lu][d3d11] %s\r\n", GetTickCount(), msg);
    DWORD wr = 0; if (len > 0) WriteFile(h, line, (DWORD)len, &wr, nullptr);
    CloseHandle(h);
}

// ── real d3d11 ──────────────────────────────────────────────────────────────
typedef HRESULT(WINAPI* PFN_D3D11CreateDevice)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
    const D3D_FEATURE_LEVEL*, UINT, UINT, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
typedef HRESULT(WINAPI* PFN_D3D11CreateDeviceAndSwapChain)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
    const D3D_FEATURE_LEVEL*, UINT, UINT, const DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**, ID3D11Device**,
    D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);

static HMODULE g_real = nullptr;
static HMODULE RealD3D11()
{
    if (!g_real)
    {
        char tmp[MAX_PATH]; GetModuleFileNameA(GetModuleHandleA("d3d11.dll"), tmp, MAX_PATH);
        char* slash = strrchr(tmp, '\\'); if (slash) { strcpy_s(slash + 1, MAX_PATH - (slash + 1 - tmp), "d3d11orig.dll"); }
        g_real = LoadLibraryA(tmp);
        if (!g_real) g_real = LoadLibraryA("d3d11orig.dll");
    }
    return g_real;
}

// ── CreateTexture2D hook ────────────────────────────────────────────────────
typedef HRESULT(STDMETHODCALLTYPE* PFN_CreateTexture2D)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
static PFN_CreateTexture2D g_origCreateTex = nullptr;
static volatile LONG g_hooked = 0;
static int g_texCount = 0, g_redCount = 0, g_bigLogged = 0;
static bool g_redden = false; // master switch — false = NORMAL world (no texture changes)

struct FmtInfo { UINT blkW, blkH, blkBytes; BYTE red[16]; };
static bool GetFmt(DXGI_FORMAT f, FmtInfo* fi)
{
    switch (f)
    {
    case DXGI_FORMAT_BC1_TYPELESS: case DXGI_FORMAT_BC1_UNORM: case DXGI_FORMAT_BC1_UNORM_SRGB:
        *fi = { 4,4,8,{0x00,0xF8,0x00,0xF8,0,0,0,0} }; return true;            // DXT1 red
    case DXGI_FORMAT_BC3_TYPELESS: case DXGI_FORMAT_BC3_UNORM: case DXGI_FORMAT_BC3_UNORM_SRGB:
        *fi = { 4,4,16,{0xFF,0xFF,0,0,0,0,0,0,0x00,0xF8,0x00,0xF8,0,0,0,0} }; return true; // DXT5 red
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        *fi = { 1,1,4,{0x00,0x00,0xFF,0xFF} }; return true;                    // BGRA red
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        *fi = { 1,1,4,{0xFF,0x00,0x00,0xFF} }; return true;                    // RGBA red
    default: return false;
    }
}

static HRESULT STDMETHODCALLTYPE Hook_CreateTexture2D(ID3D11Device* self,
    const D3D11_TEXTURE2D_DESC* desc, const D3D11_SUBRESOURCE_DATA* init, ID3D11Texture2D** out)
{
    g_texCount++;
    // Log every large texture once (find the sky among them).
    if (desc && desc->Width >= 256 && g_bigLogged < 220)
    {
        g_bigLogged++;
        char dm[192];
        sprintf_s(dm, "TEX fmt=%d %ux%u mips=%u arr=%u misc=0x%X init=%d",
            (int)desc->Format, desc->Width, desc->Height, desc->MipLevels,
            desc->ArraySize, desc->MiscFlags, init ? 1 : 0);
        Log(dm);
    }
    FmtInfo fi;
    if (g_redden && desc && init && desc->ArraySize == 1 && desc->Width >= 64 && desc->Height >= 64
        && desc->MipLevels >= 1 && desc->MipLevels <= 16 && GetFmt(desc->Format, &fi))
    {
        UINT mips = desc->MipLevels;
        BYTE* bufs[16] = {};
        D3D11_SUBRESOURCE_DATA mod[16];
        bool ok = true;
        for (UINT i = 0; i < mips && i < 16; ++i)
        {
            UINT w = desc->Width >> i; if (w < 1) w = 1;
            UINT h = desc->Height >> i; if (h < 1) h = 1;
            UINT bw = (w + fi.blkW - 1) / fi.blkW, bh = (h + fi.blkH - 1) / fi.blkH;
            size_t sz = (size_t)bw * bh * fi.blkBytes;
            BYTE* b = (BYTE*)malloc(sz);
            if (!b) { ok = false; break; }
            for (size_t o = 0; o + fi.blkBytes <= sz; o += fi.blkBytes) memcpy(b + o, fi.red, fi.blkBytes);
            bufs[i] = b;
            mod[i].pSysMem = b; mod[i].SysMemPitch = bw * fi.blkBytes; mod[i].SysMemSlicePitch = 0;
        }
        HRESULT hr;
        if (ok) { hr = g_origCreateTex(self, desc, mod, out); g_redCount++; }
        else    { hr = g_origCreateTex(self, desc, init, out); }
        for (UINT i = 0; i < 16; ++i) if (bufs[i]) free(bufs[i]);
        if (g_redCount <= 15) { char m[160]; sprintf_s(m, "reddened fmt=%d %ux%u mips=%u (red=%d seen=%d)", desc->Format, desc->Width, desc->Height, mips, g_redCount, g_texCount); Log(m); }
        return hr;
    }
    return g_origCreateTex(self, desc, init, out);
}

// UpdateSubresource — ARK uploads texture pixels here (textures are created empty).
typedef void (STDMETHODCALLTYPE* PFN_UpdateSub)(ID3D11DeviceContext*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT);
static PFN_UpdateSub g_origUpdateSub = nullptr;
static int g_updRed = 0;

static void STDMETHODCALLTYPE Hook_UpdateSubresource(ID3D11DeviceContext* ctx, ID3D11Resource* dst,
    UINT sub, const D3D11_BOX* box, const void* src, UINT rowPitch, UINT depthPitch)
{
    if (dst && src && rowPitch && box == nullptr)
    {
        ID3D11Texture2D* tex = nullptr;
        if (SUCCEEDED(dst->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex)) && tex)
        {
            D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d); tex->Release();
            FmtInfo fi;
            if (d.Width >= 64 && d.Height >= 64 && GetFmt(d.Format, &fi))
            {
                UINT mipLevels = d.MipLevels ? d.MipLevels : 1;
                UINT mip = sub % mipLevels;
                UINT h = d.Height >> mip; if (h < 1) h = 1;
                UINT bh = (h + fi.blkH - 1) / fi.blkH;
                size_t sz = (size_t)rowPitch * bh;
                BYTE* b = (BYTE*)malloc(sz);
                if (b)
                {
                    for (size_t o = 0; o + fi.blkBytes <= sz; o += fi.blkBytes) memcpy(b + o, fi.red, fi.blkBytes);
                    g_origUpdateSub(ctx, dst, sub, box, b, rowPitch, depthPitch);
                    free(b);
                    if (++g_updRed <= 15) { char m[160]; sprintf_s(m, "UpdateSub reddened fmt=%d %ux%u mip=%u (n=%d)", (int)d.Format, d.Width, d.Height, mip, g_updRed); Log(m); }
                    return;
                }
            }
        }
    }
    g_origUpdateSub(ctx, dst, sub, box, src, rowPitch, depthPitch);
}

// Map/Unmap — ARK's texture streaming maps the texture, writes mips, then unmaps. We let UE4
// write, then overwrite the mapped bytes with red just before Unmap commits them.
struct MapEntry { ID3D11Resource* res; UINT sub; void* p; size_t bytes; UINT blkBytes; BYTE red[16]; };
static MapEntry g_maps[64];
static CRITICAL_SECTION g_mapCs;
static int g_mapRed = 0;
typedef HRESULT(STDMETHODCALLTYPE* PFN_Map)(ID3D11DeviceContext*, ID3D11Resource*, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
typedef void (STDMETHODCALLTYPE* PFN_Unmap)(ID3D11DeviceContext*, ID3D11Resource*, UINT);
static PFN_Map g_origMap = nullptr;
static PFN_Unmap g_origUnmap = nullptr;
static int g_copyLogged = 0, g_mapLogged = 0;

// CopyResource / CopySubresourceRegion — the likely streamed-texture upload path
// (staging texture filled then copied into the real texture). Log the destinations.
typedef void (STDMETHODCALLTYPE* PFN_CopyRes)(ID3D11DeviceContext*, ID3D11Resource*, ID3D11Resource*);
typedef void (STDMETHODCALLTYPE* PFN_CopySub)(ID3D11DeviceContext*, ID3D11Resource*, UINT, UINT, UINT, UINT, ID3D11Resource*, UINT, const D3D11_BOX*);
static PFN_CopyRes g_origCopyRes = nullptr;
static PFN_CopySub g_origCopySub = nullptr;

static void LogCopyDst(ID3D11Resource* dst, const char* which)
{
    if (g_copyLogged >= 60 || !dst) return;
    ID3D11Texture2D* tex = nullptr;
    if (SUCCEEDED(dst->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex)) && tex)
    {
        D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d); tex->Release();
        if (d.Width >= 128) { g_copyLogged++; char m[160]; sprintf_s(m, "%s dst fmt=%d %ux%u mips=%u arr=%u", which, (int)d.Format, d.Width, d.Height, d.MipLevels, d.ArraySize); Log(m); }
    }
}
static void STDMETHODCALLTYPE Hook_CopyResource(ID3D11DeviceContext* ctx, ID3D11Resource* dst, ID3D11Resource* src) { LogCopyDst(dst, "COPYRES"); g_origCopyRes(ctx, dst, src); }
static void STDMETHODCALLTYPE Hook_CopySub(ID3D11DeviceContext* ctx, ID3D11Resource* dst, UINT dsub, UINT dx, UINT dy, UINT dz, ID3D11Resource* src, UINT ssub, const D3D11_BOX* box) { LogCopyDst(dst, "COPYSUB"); g_origCopySub(ctx, dst, dsub, dx, dy, dz, src, ssub, box); }

static HRESULT STDMETHODCALLTYPE Hook_Map(ID3D11DeviceContext* ctx, ID3D11Resource* res, UINT sub,
    D3D11_MAP mapType, UINT flags, D3D11_MAPPED_SUBRESOURCE* mapped)
{
    HRESULT hr = g_origMap(ctx, res, sub, mapType, flags, mapped);
    if (SUCCEEDED(hr) && mapped && mapped->pData && res &&
        (mapType == D3D11_MAP_WRITE || mapType == D3D11_MAP_WRITE_DISCARD || mapType == D3D11_MAP_READ_WRITE))
    {
        ID3D11Texture2D* tex = nullptr;
        if (SUCCEEDED(res->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex)) && tex)
        {
            D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d); tex->Release();
            if (g_mapLogged < 50 && d.Width >= 128) { g_mapLogged++; char lm[160]; sprintf_s(lm, "MAP tex fmt=%d %ux%u sub=%u type=%d", (int)d.Format, d.Width, d.Height, sub, (int)mapType); Log(lm); }
            FmtInfo fi;
            if (d.Width >= 64 && d.Height >= 64 && GetFmt(d.Format, &fi))
            {
                UINT mipLevels = d.MipLevels ? d.MipLevels : 1; UINT mip = sub % mipLevels;
                UINT h = d.Height >> mip; if (h < 1) h = 1;
                UINT bh = (h + fi.blkH - 1) / fi.blkH;
                size_t bytes = (size_t)mapped->RowPitch * bh;
                EnterCriticalSection(&g_mapCs);
                for (int i = 0; i < 64; i++)
                    if (!g_maps[i].res) { g_maps[i] = { res, sub, mapped->pData, bytes, fi.blkBytes, {} }; memcpy(g_maps[i].red, fi.red, 16); break; }
                LeaveCriticalSection(&g_mapCs);
            }
        }
    }
    return hr;
}

static void STDMETHODCALLTYPE Hook_Unmap(ID3D11DeviceContext* ctx, ID3D11Resource* res, UINT sub)
{
    EnterCriticalSection(&g_mapCs);
    for (int i = 0; i < 64; i++)
    {
        if (g_maps[i].res == res && g_maps[i].sub == sub)
        {
            BYTE* p = (BYTE*)g_maps[i].p; size_t bytes = g_maps[i].bytes; UINT bb = g_maps[i].blkBytes;
            for (size_t o = 0; o + bb <= bytes; o += bb) memcpy(p + o, g_maps[i].red, bb);
            g_maps[i].res = nullptr;
            if (++g_mapRed <= 15) { char m[96]; sprintf_s(m, "Map reddened (n=%d bytes=%zu)", g_mapRed, bytes); Log(m); }
            break;
        }
    }
    LeaveCriticalSection(&g_mapCs);
    g_origUnmap(ctx, res, sub);
}

static void HookCtxMethod(void** cvt, int idx, void* hook, void** orig, const char* name)
{
    *orig = cvt[idx];
    DWORD o;
    if (VirtualProtect(&cvt[idx], sizeof(void*), PAGE_EXECUTE_READWRITE, &o))
    {
        cvt[idx] = hook;
        VirtualProtect(&cvt[idx], sizeof(void*), o, &o);
        char m[64]; sprintf_s(m, "%s hooked", name); Log(m);
    }
}

static void HookDevice(ID3D11Device* dev)
{
    if (InterlockedCompareExchange(&g_hooked, 1, 0) != 0) return;
    void** vtbl = *(void***)dev;     // ID3D11Device vtable; index 5 = CreateTexture2D
    g_origCreateTex = (PFN_CreateTexture2D)vtbl[5];
    DWORD old;
    if (VirtualProtect(&vtbl[5], sizeof(void*), PAGE_EXECUTE_READWRITE, &old))
    {
        vtbl[5] = (void*)Hook_CreateTexture2D;
        VirtualProtect(&vtbl[5], sizeof(void*), old, &old);
        Log("CreateTexture2D hooked");
    }
    // Hook the immediate context's UpdateSubresource (vtable index 48) — the texture-upload path.
    ID3D11DeviceContext* ctx = nullptr;
    dev->GetImmediateContext(&ctx);
    if (ctx)
    {
        void** cvt = *(void***)ctx;
        HookCtxMethod(cvt, 14, (void*)Hook_Map, (void**)&g_origMap, "Map");
        HookCtxMethod(cvt, 15, (void*)Hook_Unmap, (void**)&g_origUnmap, "Unmap");
        HookCtxMethod(cvt, 46, (void*)Hook_CopySub, (void**)&g_origCopySub, "CopySubresourceRegion");
        HookCtxMethod(cvt, 47, (void*)Hook_CopyResource, (void**)&g_origCopyRes, "CopyResource");
        HookCtxMethod(cvt, 48, (void*)Hook_UpdateSubresource, (void**)&g_origUpdateSub, "UpdateSubresource");
        ctx->Release();
    }
}

// ── exported entry points we implement (everything else forwards) ───────────
extern "C" HRESULT WINAPI rrD3D11CreateDevice(
    IDXGIAdapter* a, D3D_DRIVER_TYPE dt, HMODULE sw, UINT flags,
    const D3D_FEATURE_LEVEL* fl, UINT nfl, UINT sdk,
    ID3D11Device** dev, D3D_FEATURE_LEVEL* ofl, ID3D11DeviceContext** ctx)
{
    auto real = (PFN_D3D11CreateDevice)GetProcAddress(RealD3D11(), "D3D11CreateDevice");
    if (!real) return E_FAIL;
    HRESULT hr = real(a, dt, sw, flags, fl, nfl, sdk, dev, ofl, ctx);
    if (SUCCEEDED(hr) && dev && *dev) HookDevice(*dev);
    return hr;
}

extern "C" HRESULT WINAPI rrD3D11CreateDeviceAndSwapChain(
    IDXGIAdapter* a, D3D_DRIVER_TYPE dt, HMODULE sw, UINT flags,
    const D3D_FEATURE_LEVEL* fl, UINT nfl, UINT sdk,
    const DXGI_SWAP_CHAIN_DESC* scd, IDXGISwapChain** sc,
    ID3D11Device** dev, D3D_FEATURE_LEVEL* ofl, ID3D11DeviceContext** ctx)
{
    auto real = (PFN_D3D11CreateDeviceAndSwapChain)GetProcAddress(RealD3D11(), "D3D11CreateDeviceAndSwapChain");
    if (!real) return E_FAIL;
    HRESULT hr = real(a, dt, sw, flags, fl, nfl, sdk, scd, sc, dev, ofl, ctx);
    if (SUCCEEDED(hr) && dev && *dev) HookDevice(*dev);
    return hr;
}

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(h); InitializeCriticalSection(&g_mapCs); Log("d3d11 proxy loaded"); }
    return TRUE;
}
