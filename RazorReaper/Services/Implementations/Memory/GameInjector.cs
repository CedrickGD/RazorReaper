using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations.Memory;

/// <summary>
/// LoadLibrary-style injector for rr_live.dll. See <see cref="IGameInjector"/>.
/// Same-user process, so standard rights suffice; no driver / no kernel work.
/// </summary>
public sealed class GameInjector : IGameInjector
{
    private const string ModuleName = "rr_live.dll";
    private const string PipeName = "rr_live";

    private const uint PROCESS_RIGHTS = 0x43A; // CREATE_THREAD|QUERY_INFO|VM_OPERATION|VM_WRITE|VM_READ
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;
    private readonly ILogger<GameInjector> _logger;

    public GameInjector(IProcessService process, IOptions<AppConfiguration> config, ILogger<GameInjector> logger)
    {
        _process = process;
        _config = config;
        _logger = logger;
    }

    public bool IsLoadedInGame()
    {
        var pid = ResolveGamePid();
        return pid is not null && IsModuleLoaded(pid.Value, ModuleName);
    }

    public InjectResult InjectIntoGame()
    {
        var pid = ResolveGamePid();
        if (pid is null) return new InjectResult(false, false, "ShooterGame isn't running — launch ARK first.");

        var dll = ResolveModulePath();
        if (dll is null) return new InjectResult(false, false, $"{ModuleName} not found next to RazorReaper.exe.");

        if (IsModuleLoaded(pid.Value, ModuleName))
            return new InjectResult(true, true, $"{ModuleName} is already loaded.");

        return Inject(pid.Value, dll);
    }

    public async Task<bool> SendCommandAsync(string command, CancellationToken ct = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(2000, ct);
            var bytes = Encoding.ASCII.GetBytes(command);
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pipe send failed for command {Command}", command);
            return false;
        }
    }

    private int? ResolveGamePid()
    {
        var procs = _process.GetProcessesByName(_config.Value.Ark.GameProcessName);
        try
        {
            return procs.Length > 0 ? procs[0].Id : (int?)null;
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
    }

    private static string? ResolveModulePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, ModuleName);
        return File.Exists(local) ? local : null;
    }

    private bool IsModuleLoaded(int pid, string moduleName)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            foreach (ProcessModule m in p.Modules)
            {
                if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Module enumeration failed for pid {Pid}", pid);
        }
        return false;
    }

    private InjectResult Inject(int pid, string dllPath)
    {
        var h = OpenProcess(PROCESS_RIGHTS, false, pid);
        if (h == IntPtr.Zero)
            return new InjectResult(false, false, $"OpenProcess failed (Win32 {Marshal.GetLastWin32Error()}).");

        var mem = IntPtr.Zero;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            mem = VirtualAllocEx(h, IntPtr.Zero, (uint)bytes.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (mem == IntPtr.Zero)
                return new InjectResult(false, false, $"VirtualAllocEx failed (Win32 {Marshal.GetLastWin32Error()}).");

            if (!WriteProcessMemory(h, mem, bytes, (uint)bytes.Length, out _))
                return new InjectResult(false, false, $"WriteProcessMemory failed (Win32 {Marshal.GetLastWin32Error()}).");

            var loadLib = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLib == IntPtr.Zero)
                return new InjectResult(false, false, "Could not resolve LoadLibraryW.");

            var th = CreateRemoteThread(h, IntPtr.Zero, 0, loadLib, mem, 0, out _);
            if (th == IntPtr.Zero)
                return new InjectResult(false, false, $"CreateRemoteThread failed (Win32 {Marshal.GetLastWin32Error()}).");

            WaitForSingleObject(th, 7000);
            GetExitCodeThread(th, out var code);
            CloseHandle(th);

            // The remote-thread exit code is the HMODULE truncated to 32 bits; confirm via module list.
            var ok = IsModuleLoaded(pid, ModuleName) || code != 0;
            _logger.LogInformation("Injected {Module} into {Pid}: ok={Ok} exit=0x{Code:X}", ModuleName, pid, ok, code);
            return new InjectResult(ok, false,
                ok ? $"Injected {ModuleName}." : "Injection thread ran but the module wasn't confirmed loaded.");
        }
        finally
        {
            if (mem != IntPtr.Zero) VirtualFreeEx(h, mem, 0, MEM_RELEASE);
            CloseHandle(h);
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, uint size, uint type);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, uint size, out IntPtr written);
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, uint stack, IntPtr start, IntPtr param, uint flags, out IntPtr tid);
    [DllImport("kernel32", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr h, string proc);
}
