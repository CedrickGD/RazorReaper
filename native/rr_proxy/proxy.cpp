// RazorReaper proxy DLL (dxgi.dll) for ARK (ShooterGame.exe).
//
// Loaded BY the game (proxy in the Win64 folder), so no injection — nothing for AV to flag.
// All real dxgi exports are forwarded to dxgiorig.dll via the generated exports_gen.h.
//
// This build is a live reverse-engineering tool: it opens \\.\pipe\rr_live and answers
// memory commands so we can locate UE4's GNames/GObjects/sky-texture WITHOUT restarting ARK.
// Every memory touch is SEH-guarded so a bad address can't crash the game.
//   modinfo                  -> log ShooterGame.exe base + size
//   ascan <text>             -> scan committed memory for an ASCII string
//   wscan <text>             -> scan for a UTF-16 string
//   read <hexaddr> <len>     -> hex-dump memory at an address
//   regions                  -> summarize committed regions

#include <windows.h>
#include <string>
#include <vector>
#include <cstdio>
#include <cstdint>
#include "exports_gen.h"

static void Log(const char* msg)
{
    char tmp[MAX_PATH];
    DWORD n = GetTempPathA(MAX_PATH, tmp);
    if (n == 0 || n > MAX_PATH) return;
    std::string path = std::string(tmp) + "rr_live.log";
    HANDLE h = CreateFileA(path.c_str(), FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, nullptr, FILE_END);
    char line[2048];
    int len = sprintf_s(line, "[t=%lu] %s\r\n", GetTickCount(), msg);
    DWORD wr = 0;
    if (len > 0) WriteFile(h, line, (DWORD)len, &wr, nullptr);
    CloseHandle(h);
}

// ── SEH-guarded primitives (no C++ objects inside __try) ────────────────────
static bool RegionReadable(const MEMORY_BASIC_INFORMATION& mbi)
{
    if (mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) return false;
    DWORD p = mbi.Protect & 0xFF;
    return p == PAGE_READONLY || p == PAGE_READWRITE || p == PAGE_WRITECOPY
        || p == PAGE_EXECUTE_READ || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
}

static void ScanRegionSEH(const BYTE* base, size_t size, const BYTE* needle, size_t nlen,
                          uintptr_t* out, int maxOut, int* count)
{
    __try
    {
        if (size < nlen) return;
        const BYTE* end = base + size - nlen;
        for (const BYTE* p = base; p <= end; ++p)
        {
            if (p[0] == needle[0] && memcmp(p, needle, nlen) == 0)
            {
                out[(*count)++] = (uintptr_t)p;
                if (*count >= maxOut) return;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static bool SafeRead(const void* addr, void* dst, int len)
{
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(addr, &mbi, sizeof(mbi)) == 0) return false;
    if (!RegionReadable(mbi)) return false;
    __try { memcpy(dst, addr, len); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// ── Commands ────────────────────────────────────────────────────────────────
static int Scan(const BYTE* needle, size_t nlen, uintptr_t* out, int maxOut)
{
    int count = 0;
    SYSTEM_INFO si; GetSystemInfo(&si);
    BYTE* addr = (BYTE*)si.lpMinimumApplicationAddress;
    BYTE* maxAddr = (BYTE*)si.lpMaximumApplicationAddress;
    MEMORY_BASIC_INFORMATION mbi;
    while (addr < maxAddr && count < maxOut)
    {
        if (VirtualQuery(addr, &mbi, sizeof(mbi)) == 0) break;
        // Runtime FNames/textures live in private/mapped memory, not the exe image. Skipping
        // MEM_IMAGE is faster and avoids scanning the module's static data.
        if (RegionReadable(mbi) && mbi.Type != MEM_IMAGE)
            ScanRegionSEH((BYTE*)mbi.BaseAddress, mbi.RegionSize, needle, nlen, out, maxOut, &count);
        BYTE* next = (BYTE*)mbi.BaseAddress + mbi.RegionSize;
        if (next <= addr) break;
        addr = next;
    }
    return count;
}

static void CmdModinfo()
{
    HMODULE base = GetModuleHandleA("ShooterGame.exe");
    if (!base) base = GetModuleHandleA(nullptr);
    auto dos = (IMAGE_DOS_HEADER*)base;
    auto nt = (IMAGE_NT_HEADERS*)((BYTE*)base + dos->e_lfanew);
    char m[256];
    sprintf_s(m, "modinfo: ShooterGame base=%p size=0x%X", (void*)base, nt->OptionalHeader.SizeOfImage);
    Log(m);
}

static void CmdScanAscii(const char* text)
{
    uintptr_t hits[12];
    int n = Scan((const BYTE*)text, strlen(text), hits, 12);
    std::string s = "ascan '" + std::string(text) + "' -> " + std::to_string(n) + " hit(s):";
    for (int i = 0; i < n && i < 12; ++i) { char b[24]; sprintf_s(b, " %p", (void*)hits[i]); s += b; }
    Log(s.c_str());
}

static void CmdScanWide(const char* text)
{
    std::wstring w; for (const char* p = text; *p; ++p) w.push_back((wchar_t)*p);
    uintptr_t hits[12];
    int n = Scan((const BYTE*)w.c_str(), w.size() * 2, hits, 12);
    std::string s = "wscan '" + std::string(text) + "' -> " + std::to_string(n) + " hit(s):";
    for (int i = 0; i < n && i < 12; ++i) { char b[24]; sprintf_s(b, " %p", (void*)hits[i]); s += b; }
    Log(s.c_str());
}

static void CmdRead(uintptr_t addr, int len)
{
    if (len <= 0 || len > 256) len = 64;
    BYTE buf[256];
    if (!SafeRead((void*)addr, buf, len)) { Log("read: unreadable address"); return; }
    char line[1100];
    int o = sprintf_s(line, "read %p [%d]:", (void*)addr, len);
    for (int i = 0; i < len; ++i) o += sprintf_s(line + o, sizeof(line) - o, " %02X", buf[i]);
    Log(line);
}

static void DumpAt(uintptr_t addr, int before, int len)
{
    int total = before + len;
    if (total > 144) total = 144;
    BYTE buf[144];
    uintptr_t start = addr - before;
    if (!SafeRead((void*)start, buf, total)) { char m[64]; sprintf_s(m, "  @%p unreadable", (void*)start); Log(m); return; }
    char line[900];
    int o = sprintf_s(line, "  @%p:", (void*)start);
    for (int i = 0; i < total; ++i) o += sprintf_s(line + o, sizeof(line) - o, " %02X", buf[i]);
    o += sprintf_s(line + o, sizeof(line) - o, "  |");
    for (int i = 0; i < total; ++i) { char c = (char)buf[i]; o += sprintf_s(line + o, sizeof(line) - o, "%c", (c >= 32 && c < 127) ? c : '.'); }
    Log(line);
}

static void CmdFind(const BYTE* needle, size_t nlen, const char* label)
{
    uintptr_t hits[8];
    int n = Scan(needle, nlen, hits, 8);
    char h[128]; sprintf_s(h, "find '%s' -> %d hit(s)", label, n); Log(h);
    for (int i = 0; i < n; ++i) DumpAt(hits[i], 16, 80);
}

static void Dispatch(const char* cmd)
{
    while (*cmd == ' ') ++cmd;
    if (_strnicmp(cmd, "modinfo", 7) == 0) { CmdModinfo(); return; }
    if (_strnicmp(cmd, "ascan ", 6) == 0) { CmdScanAscii(cmd + 6); return; }
    if (_strnicmp(cmd, "wscan ", 6) == 0) { CmdScanWide(cmd + 6); return; }
    if (_strnicmp(cmd, "afind ", 6) == 0) { CmdFind((const BYTE*)(cmd + 6), strlen(cmd + 6), cmd + 6); return; }
    if (_strnicmp(cmd, "wfind ", 6) == 0)
    {
        std::wstring w; for (const char* p = cmd + 6; *p; ++p) w.push_back((wchar_t)*p);
        CmdFind((const BYTE*)w.c_str(), w.size() * 2, cmd + 6);
        return;
    }
    if (_strnicmp(cmd, "read ", 5) == 0)
    {
        uintptr_t addr = 0; int len = 64;
        sscanf_s(cmd + 5, "%llx %d", &addr, &len);
        CmdRead(addr, len);
        return;
    }
    std::string s = "cmd (unhandled): "; s += cmd; Log(s.c_str());
}

static DWORD WINAPI Worker(LPVOID)
{
    Log("rr_proxy (dxgi) loaded — RE tool ready; send commands over the pipe");
    const char* pipeName = "\\\\.\\pipe\\rr_live";
    for (;;)
    {
        HANDLE pipe = CreateNamedPipeA(pipeName, PIPE_ACCESS_INBOUND,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT, 1, 0, 8192, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) { Sleep(1000); continue; }
        BOOL ok = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
        if (ok)
        {
            char buf[4096]; DWORD read = 0;
            while (ReadFile(pipe, buf, sizeof(buf) - 1, &read, nullptr) && read > 0)
            {
                buf[read] = 0;
                Dispatch(buf);
            }
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        HANDLE t = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
