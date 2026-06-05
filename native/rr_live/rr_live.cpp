// rr_live.dll — RazorReaper's in-process helper for ARK (ShooterGame.exe).
//
// Phase 0: prove the injection + IPC pipeline.
//   - On load, drop a marker file (%TEMP%\rr_live.log) so RazorReaper can confirm we ran.
//   - Open a named pipe (\\.\pipe\rr_live) and log every command received.
// Phase 1 will dispatch those commands into Unreal's object system to reload textures live.
//
// Build (x64): see native/rr_live/build.ps1

#include <windows.h>
#include <string>
#include <cstdio>

static void Log(const char* msg)
{
    char tmp[MAX_PATH];
    DWORD n = GetTempPathA(MAX_PATH, tmp);
    if (n == 0 || n > MAX_PATH) return;
    std::string path = std::string(tmp) + "rr_live.log";

    HANDLE h = CreateFileA(path.c_str(), FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    SetFilePointer(h, 0, nullptr, FILE_END);
    char line[1024];
    int len = sprintf_s(line, "[pid=%lu tick=%lu] %s\r\n",
        GetCurrentProcessId(), GetTickCount(), msg);
    DWORD written = 0;
    if (len > 0) WriteFile(h, line, (DWORD)len, &written, nullptr);
    CloseHandle(h);
}

static DWORD WINAPI Worker(LPVOID)
{
    Log("rr_live loaded into ShooterGame");

    const char* pipeName = "\\\\.\\pipe\\rr_live";
    for (;;)
    {
        HANDLE pipe = CreateNamedPipeA(
            pipeName,
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            1, 0, 4096, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            Sleep(1000);
            continue;
        }

        BOOL connected = ConnectNamedPipe(pipe, nullptr)
            ? TRUE
            : (GetLastError() == ERROR_PIPE_CONNECTED);

        if (connected)
        {
            char buf[4096];
            DWORD read = 0;
            while (ReadFile(pipe, buf, sizeof(buf) - 1, &read, nullptr) && read > 0)
            {
                buf[read] = 0;
                std::string m = std::string("cmd: ") + buf;
                Log(m.c_str());
                // Phase 1: parse "reload <path>" and call into Unreal here.
            }
        }

        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
    return 0;
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
