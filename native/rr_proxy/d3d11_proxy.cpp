// rr d3d11.dll proxy for ARK — LIVE SKY SWAP engine.
//
// Loaded by the game itself (proxy in Win64, ReShade-style — no injection). All real d3d11
// exports forward to d3d11orig.dll via exports_gen_d3d11.h; we implement only
// D3D11CreateDevice(+AndSwapChain) to install hooks on the device + immediate context.
//
// Reads the control dir written by C# LiveSkyService:
//   %LOCALAPPDATA%\RazorReaper\LiveSky\
//     enabled         — presence = armed
//     gen.txt         — integer bumped each apply
//     targets.txt     — "<fnv1a64-hex> <W> <H>" per SimpleSky original
//     sky_<W>x<H>.bin — the user's image as a BC3 full mip chain for that dimension
//
// Two paths, both thread-safe:
//   1) CREATE-TIME SPLICE (worker thread): at CreateTexture2D, a BC3 sky texture whose base-mip
//      FNV-1a-64 hash matches a target gets the user's BC3 spliced in as the game creates it.
//      Matched textures are tracked (AddRef'd).
//   2) LIVE RE-SKIN (render/RHI thread): a background poller watches gen.txt; on a bump it flags a
//      re-skin, which the Map hook performs via UpdateSubresource on already-loaded tracked textures
//      — so pressing "swap" repaints the sky with no relaunch. The Map hook runs on the same thread
//      that owns the immediate context, which is why this is safe (the old crash re-skinned from the
//      CreateTexture2D worker thread).

#include <windows.h>
#include <d3d11.h>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include "exports_gen_d3d11.h"

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

// ── live-sky control dir ─────────────────────────────────────────────────────
static char g_dir[MAX_PATH] = "";
static volatile LONG g_armed = 0;
static int g_gen = -1;
static CRITICAL_SECTION g_cs;

struct Target { uint64_t hash; uint32_t w, h; };
static Target g_targets[512]; static int g_targetN = 0;
struct Blob { uint32_t w, h; BYTE* data; size_t size; };
static Blob g_blobs[16]; static int g_blobN = 0;

// Already-loaded sky textures we replaced at create-time, kept alive (AddRef'd) so the live
// re-skin can rewrite them in place when the user swaps to a new image.
struct Tracked { ID3D11Texture2D* tex; uint32_t w, h; UINT mips; };
static Tracked g_tracked[16]; static int g_trackedN = 0;
static volatile LONG g_reskinPending = 0;

static uint64_t Fnv1a64(const BYTE* d, size_t n)
{
    uint64_t h = 14695981039346656037ULL;
    for (size_t i = 0; i < n; i++) { h ^= d[i]; h *= 1099511628211ULL; }
    return h;
}

static bool IsBc3(DXGI_FORMAT f)
{
    return f == DXGI_FORMAT_BC3_UNORM || f == DXGI_FORMAT_BC3_UNORM_SRGB || f == DXGI_FORMAT_BC3_TYPELESS;
}
static bool IsSkyDim(UINT w, UINT h)
{
    if (w != h) return false;
    return w == 256 || w == 512 || w == 1024 || w == 2048 || w == 4096;
}

static BYTE* SlurpFile(const char* path, size_t* outSize)
{
    *outSize = 0;
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return nullptr;
    DWORD sz = GetFileSize(h, nullptr);
    BYTE* b = (BYTE*)malloc((size_t)sz + 1);   // +1 so the content is always null-terminated
    DWORD rd = 0; if (b) ReadFile(h, b, sz, &rd, nullptr);
    if (b) b[rd] = 0;                          // terminate for atoi / strtok_s (fixes gen misread)
    CloseHandle(h);
    *outSize = rd; return b;
}

static void EnsureDir()
{
    if (g_dir[0]) return;
    char* base = nullptr; size_t n = 0;
    if (_dupenv_s(&base, &n, "LOCALAPPDATA") == 0 && base) { sprintf_s(g_dir, sizeof(g_dir), "%s\\RazorReaper\\LiveSky\\", base); free(base); }
}

// Re-read the control dir when armed-state or gen changes. Runs on the background poller thread
// (file I/O only) so neither the render nor the worker threads ever block on disk.
static void LoadControl()
{
    EnsureDir(); if (!g_dir[0]) return;
    char p[MAX_PATH];
    sprintf_s(p, sizeof(p), "%senabled", g_dir);
    bool armed = GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
    int gen = -1;
    sprintf_s(p, sizeof(p), "%sgen.txt", g_dir);
    { size_t s; BYTE* b = SlurpFile(p, &s); if (b) { gen = atoi((char*)b); free(b); } }

    if ((armed ? 1 : 0) == g_armed && gen == g_gen) return;   // nothing changed

    EnterCriticalSection(&g_cs);
    g_armed = armed ? 1 : 0; g_gen = gen;
    g_targetN = 0;
    for (int i = 0; i < g_blobN; i++) free(g_blobs[i].data);
    g_blobN = 0;

    if (armed)
    {
        sprintf_s(p, sizeof(p), "%stargets.txt", g_dir);
        size_t s; BYTE* b = SlurpFile(p, &s);
        if (b)
        {
            char* ctx = nullptr;
            char* line = strtok_s((char*)b, "\r\n", &ctx);
            while (line && g_targetN < 512)
            {
                unsigned long long hash; unsigned int w, h;
                if (sscanf_s(line, "%llx %u %u", &hash, &w, &h) == 3)
                {
                    g_targets[g_targetN++] = { (uint64_t)hash, (uint32_t)w, (uint32_t)h };
                    bool have = false;
                    for (int i = 0; i < g_blobN; i++) if (g_blobs[i].w == w && g_blobs[i].h == h) { have = true; break; }
                    if (!have && g_blobN < 16)
                    {
                        char bp[MAX_PATH]; sprintf_s(bp, sizeof(bp), "%ssky_%ux%u.bin", g_dir, w, h);
                        size_t bs; BYTE* bd = SlurpFile(bp, &bs);
                        if (bd && bs > 0)
                        {
                            g_blobs[g_blobN++] = { (uint32_t)w, (uint32_t)h, bd, bs };
                            // log the injected image's per-mip hashes so the next pass can find which
                            // runtime mip the file-inject lands on (compare BLOB m? vs BC3 sky m?).
                            char bm[220]; int bpos = sprintf_s(bm, sizeof(bm), "BLOB %ux%u", w, h);
                            size_t boff = 0;
                            for (int m = 0; m < 3; m++)
                            {
                                UINT mw = (UINT)w >> m, mh = (UINT)h >> m; if (!mw) mw = 1; if (!mh) mh = 1;
                                size_t msz = (size_t)((mw + 3) / 4) * ((mh + 3) / 4) * 16;
                                if (boff + msz > bs) break;
                                bpos += sprintf_s(bm + bpos, sizeof(bm) - bpos, " m%d=%016llx", m, (unsigned long long)Fnv1a64(bd + boff, msz));
                                boff += msz;
                            }
                            Log(bm);
                        }
                        else if (bd) free(bd);
                    }
                }
                line = strtok_s(nullptr, "\r\n", &ctx);
            }
            free(b);
        }
        InterlockedExchange(&g_reskinPending, 1);   // repaint already-loaded sky on the next frame
        char m[128]; sprintf_s(m, sizeof(m), "armed: %d targets, %d blobs, gen=%d", g_targetN, g_blobN, gen); Log(m);
    }
    else
    {
        // Disarm: drop our refs on tracked textures. They keep the last image until the game
        // reloads them (map change / Restore's file revert), per the documented behavior.
        for (int i = 0; i < g_trackedN; i++) if (g_tracked[i].tex) g_tracked[i].tex->Release();
        g_trackedN = 0;
        Log("disarmed");
    }
    LeaveCriticalSection(&g_cs);
}

static DWORD WINAPI Poller(LPVOID)
{
    for (;;) { LoadControl(); Sleep(500); }
}

static Blob* FindBlob(uint32_t w, uint32_t h) { for (int i = 0; i < g_blobN; i++) if (g_blobs[i].w == w && g_blobs[i].h == h) return &g_blobs[i]; return nullptr; }
static bool MatchTarget(uint64_t hash, uint32_t w, uint32_t h) { for (int i = 0; i < g_targetN; i++) if (g_targets[i].hash == hash && g_targets[i].w == w && g_targets[i].h == h) return true; return false; }

// Keep a matched sky texture alive so the live re-skin can rewrite it. Caller holds g_cs.
static void TrackSky(ID3D11Texture2D* tex, uint32_t w, uint32_t h, UINT mips)
{
    if (!tex) return;
    for (int i = 0; i < g_trackedN; i++) if (g_tracked[i].tex == tex) return;   // already tracked
    if (g_trackedN >= 16)
    {
        if (g_tracked[0].tex) g_tracked[0].tex->Release();
        for (int i = 1; i < g_trackedN; i++) g_tracked[i - 1] = g_tracked[i];
        g_trackedN--;
    }
    tex->AddRef();
    g_tracked[g_trackedN++] = { tex, w, h, mips };
}

// Rewrite every tracked sky texture from the current blobs. MUST run on the immediate-context
// thread — it is only ever called from the Map hook.
static void ReskinTracked(ID3D11DeviceContext* ctx)
{
    int n = 0;
    EnterCriticalSection(&g_cs);
    for (int t = 0; t < g_trackedN; t++)
    {
        Blob* blob = FindBlob(g_tracked[t].w, g_tracked[t].h);
        if (!blob) continue;
        uint32_t W = g_tracked[t].w, H = g_tracked[t].h; UINT mips = g_tracked[t].mips;
        size_t off = 0;
        for (UINT i = 0; i < mips && i < 16; i++)
        {
            UINT w = W >> i; if (!w) w = 1; UINT h = H >> i; if (!h) h = 1;
            UINT bw = (w + 3) / 4, bh = (h + 3) / 4; size_t sz = (size_t)bw * bh * 16;
            if (off + sz > blob->size) break;
            ctx->UpdateSubresource(g_tracked[t].tex, i, nullptr, blob->data + off, bw * 16, 0);
            off += sz;
        }
        n++;
    }
    LeaveCriticalSection(&g_cs);
    if (n) { char m[64]; sprintf_s(m, sizeof(m), "re-skinned %d sky tex", n); Log(m); }
}

// ── CreateTexture2D hook (create-time splice + track) ────────────────────────
typedef HRESULT(STDMETHODCALLTYPE* PFN_CreateTexture2D)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
static PFN_CreateTexture2D g_origCreateTex = nullptr;
static volatile LONG g_hooked = 0;
static int g_swapCount = 0;
static int g_diagN = 0;   // diagnostic: log the first N BC3 sky-dim textures + match result

static HRESULT STDMETHODCALLTYPE Hook_CreateTexture2D(ID3D11Device* self,
    const D3D11_TEXTURE2D_DESC* desc, const D3D11_SUBRESOURCE_DATA* init, ID3D11Texture2D** out)
{
    if (g_armed && desc && init && init[0].pSysMem && desc->ArraySize == 1
        && IsBc3(desc->Format) && IsSkyDim(desc->Width, desc->Height))
    {
        UINT W = desc->Width, H = desc->Height;
        size_t baseMip = (size_t)W * H;   // BC3 base-mip bytes = (W/4)*(H/4)*16 = W*H
        EnterCriticalSection(&g_cs);
        uint64_t hash = Fnv1a64((const BYTE*)init[0].pSysMem, baseMip);
        bool matched = MatchTarget(hash, W, H);
        if (g_diagN < 50)
        {
            g_diagN++;
            char dm[220]; int dp = sprintf_s(dm, sizeof(dm), "BC3 sky %ux%u m0=%016llx", W, H, (unsigned long long)hash);
            UINT mc = desc->MipLevels ? desc->MipLevels : 1;
            for (UINT m = 1; m < 3 && m < mc; m++)   // also fingerprint mips 1-2: the file-inject may land on a lower mip
            {
                if (!init[m].pSysMem) continue;
                UINT mw = W >> m, mh = H >> m; if (!mw) mw = 1; if (!mh) mh = 1;
                size_t msz = (size_t)((mw + 3) / 4) * ((mh + 3) / 4) * 16;
                dp += sprintf_s(dm + dp, sizeof(dm) - dp, " m%u=%016llx", m, (unsigned long long)Fnv1a64((const BYTE*)init[m].pSysMem, msz));
            }
            sprintf_s(dm + dp, sizeof(dm) - dp, " match=%d targets=%d", matched ? 1 : 0, g_targetN);
            Log(dm);
        }
        Blob* blob = matched ? FindBlob(W, H) : nullptr;
        if (blob)
        {
            UINT mips = desc->MipLevels ? desc->MipLevels : 1;
            D3D11_SUBRESOURCE_DATA mod[16]; size_t off = 0; bool ok = true;
            for (UINT i = 0; i < mips && i < 16; i++)
            {
                UINT w = W >> i; if (!w) w = 1; UINT h = H >> i; if (!h) h = 1;
                UINT bw = (w + 3) / 4, bh = (h + 3) / 4; size_t sz = (size_t)bw * bh * 16;
                if (off + sz > blob->size) { ok = false; break; }
                mod[i].pSysMem = blob->data + off; mod[i].SysMemPitch = bw * 16; mod[i].SysMemSlicePitch = 0;
                off += sz;
            }
            if (ok)
            {
                HRESULT hr = g_origCreateTex(self, desc, mod, out);
                if (SUCCEEDED(hr) && out && *out) TrackSky(*out, W, H, mips);
                if (++g_swapCount <= 20) { char m[96]; sprintf_s(m, sizeof(m), "SKY spliced %ux%u (n=%d)", W, H, g_swapCount); Log(m); }
                LeaveCriticalSection(&g_cs);
                return hr;
            }
        }
        LeaveCriticalSection(&g_cs);
    }
    return g_origCreateTex(self, desc, init, out);
}

// ── Map hook (render-thread tick that performs pending re-skins) ─────────────
typedef HRESULT(STDMETHODCALLTYPE* PFN_Map)(ID3D11DeviceContext*, ID3D11Resource*, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
static PFN_Map g_origMap = nullptr;

static HRESULT STDMETHODCALLTYPE Hook_Map(ID3D11DeviceContext* ctx, ID3D11Resource* res, UINT sub,
    D3D11_MAP mapType, UINT flags, D3D11_MAPPED_SUBRESOURCE* mapped)
{
    if (InterlockedExchange(&g_reskinPending, 0) != 0) ReskinTracked(ctx);   // immediate-ctx thread — safe
    return g_origMap(ctx, res, sub, mapType, flags, mapped);
}

static volatile LONG g_pollerStarted = 0;

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
    // Immediate context vtable; index 14 = Map. We use it purely as a render-thread heartbeat.
    ID3D11DeviceContext* ctx = nullptr;
    dev->GetImmediateContext(&ctx);
    if (ctx)
    {
        void** cvt = *(void***)ctx;
        g_origMap = (PFN_Map)cvt[14];
        DWORD o;
        if (VirtualProtect(&cvt[14], sizeof(void*), PAGE_EXECUTE_READWRITE, &o))
        {
            cvt[14] = (void*)Hook_Map;
            VirtualProtect(&cvt[14], sizeof(void*), o, &o);
            Log("Map hooked (re-skin tick)");
        }
        ctx->Release();
    }
    if (InterlockedCompareExchange(&g_pollerStarted, 1, 0) == 0)
    {
        HANDLE th = CreateThread(nullptr, 0, Poller, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
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
    if (reason == DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(h); InitializeCriticalSection(&g_cs); Log("d3d11 proxy loaded (live sky)"); }
    return TRUE;
}
