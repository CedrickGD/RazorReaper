using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.Memory;

/// <summary>
/// Win32 process-memory access for the Memory Patcher. See <see cref="IProcessMemoryService"/>.
/// Pure RPM/WPM/VirtualProtect — no injection or hooks. Non-throwing by contract.
/// </summary>
public sealed partial class ProcessMemoryService : IProcessMemoryService
{
    private readonly ILogger<ProcessMemoryService> _logger;
    private readonly object _lock = new();

    private SafeProcessHandle? _handle;
    private int? _pid;
    private bool _forWrite;

    public ProcessMemoryService(ILogger<ProcessMemoryService> logger) => _logger = logger;

    public bool IsAttached
    {
        get { lock (_lock) return _handle is { IsInvalid: false }; }
    }

    public int? AttachedProcessId
    {
        get { lock (_lock) return _pid; }
    }

    public bool AttachedForWrite
    {
        get { lock (_lock) return _forWrite; }
    }

    public MemoryAttachResult Attach(int processId, bool forWrite)
    {
        lock (_lock)
        {
            if (_handle is { IsInvalid: false })
            {
                return new MemoryAttachResult(MemoryAttachStatus.AlreadyAttached, _pid,
                    AntiCheatStatus.None, null, _forWrite, "Already attached — detach first.");
            }

            var access = PROCESS_VM_READ | PROCESS_QUERY_INFORMATION;
            if (forWrite) access |= PROCESS_VM_WRITE | PROCESS_VM_OPERATION;

            var raw = OpenProcess(access, false, processId);
            var err = Marshal.GetLastWin32Error();
            if (raw == IntPtr.Zero)
            {
                var status = err == ERROR_ACCESS_DENIED ? MemoryAttachStatus.AccessDenied : MemoryAttachStatus.Failed;
                var msg = err == ERROR_ACCESS_DENIED
                    ? "Access denied opening ShooterGame — run RazorReaper as administrator, or anti-cheat is blocking access."
                    : $"OpenProcess failed (Win32 error {err}).";
                _logger.LogWarning("OpenProcess({Pid}) failed: {Err}", processId, err);
                return new MemoryAttachResult(status, processId, AntiCheatStatus.None, null, forWrite, msg);
            }

            var handle = new SafeProcessHandle(raw, ownsHandle: true);

            // Architecture sanity: ShooterGame is 64-bit. A non-UNKNOWN process machine means
            // the target runs under WOW64 (32-bit) — wrong target, refuse rather than corrupt it.
            if (IsWow64Process2(handle, out var procMachine, out _) && procMachine != IMAGE_FILE_MACHINE_UNKNOWN)
            {
                handle.Dispose();
                return new MemoryAttachResult(MemoryAttachStatus.NotExpectedArchitecture, processId,
                    AntiCheatStatus.None, null, forWrite, "Target process is 32-bit; expected 64-bit ShooterGame.");
            }

            _handle = handle;
            _pid = processId;
            _forWrite = forWrite;
            _logger.LogInformation("Attached to process {Pid} (forWrite={ForWrite})", processId, forWrite);
            return new MemoryAttachResult(MemoryAttachStatus.Ok, processId, AntiCheatStatus.None, null, forWrite, "Attached.");
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_handle is not null)
            {
                try { _handle.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Handle dispose threw on detach"); }
            }
            _handle = null;
            _pid = null;
            _forWrite = false;
        }
    }

    private SafeProcessHandle? CurrentHandle
    {
        get { lock (_lock) return _handle is { IsInvalid: false } ? _handle : null; }
    }

    public bool TryRead(ulong address, byte[] buffer, int length, out int read)
    {
        read = 0;
        var handle = CurrentHandle;
        if (handle is null || length <= 0 || length > buffer.Length) return false;
        try
        {
            if (!ReadProcessMemory(handle, new IntPtr(unchecked((long)address)), buffer, new IntPtr(length), out var got))
                return false;
            read = (int)got;
            return read > 0;
        }
        catch (ObjectDisposedException)
        {
            return false; // detached mid-read
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadProcessMemory threw at {Addr:X}", address);
            return false;
        }
    }

    public bool TryWrite(ulong address, byte[] data, out string? error)
    {
        error = null;
        SafeProcessHandle? handle;
        bool forWrite;
        lock (_lock)
        {
            handle = _handle is { IsInvalid: false } ? _handle : null;
            forWrite = _forWrite;
        }
        if (handle is null) { error = "Not attached."; return false; }
        if (!forWrite) { error = "Attached read-only — re-attach with write access."; return false; }
        if (data is null || data.Length == 0) { error = "No data to write."; return false; }

        var addr = new IntPtr(unchecked((long)address));
        var size = new IntPtr(data.Length);
        try
        {
            // Flip to RW, write, then restore — never leave the page's protection changed.
            if (!VirtualProtectEx(handle, addr, size, PAGE_READWRITE, out var oldProtect))
            {
                error = $"VirtualProtectEx failed (Win32 {Marshal.GetLastWin32Error()}).";
                return false;
            }

            bool ok;
            int writeErr = 0;
            try
            {
                ok = WriteProcessMemory(handle, addr, data, size, out var written);
                writeErr = Marshal.GetLastWin32Error();
                if (ok && (int)written != data.Length)
                {
                    ok = false;
                    error = $"Partial write: {(int)written}/{data.Length} bytes.";
                }
            }
            finally
            {
                VirtualProtectEx(handle, addr, size, oldProtect, out _);
            }

            if (!ok && error is null)
                error = $"WriteProcessMemory failed (Win32 {writeErr}).";
            return ok;
        }
        catch (ObjectDisposedException)
        {
            error = "Detached during write.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write failed at {Addr:X}", address);
            error = ex.Message;
            return false;
        }
    }

    public IReadOnlyList<string> EnumerateModuleNames()
    {
        var handle = CurrentHandle;
        if (handle is null) return Array.Empty<string>();

        try
        {
            // First call sizes the array; second fills it.
            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(handle, modules, (uint)(IntPtr.Size * modules.Length), out var needed, LIST_MODULES_ALL))
                return Array.Empty<string>();

            var count = Math.Min(modules.Length, (int)(needed / IntPtr.Size));
            var names = new List<string>(count);
            var sb = new StringBuilder(260);
            for (var i = 0; i < count; i++)
            {
                sb.Clear();
                var len = GetModuleBaseNameW(handle, modules[i], sb, (uint)sb.Capacity);
                if (len > 0) names.Add(sb.ToString(0, (int)len).ToLowerInvariant());
            }
            return names;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Module enumeration failed");
            return Array.Empty<string>();
        }
    }

    public void Dispose() => Detach();
}
