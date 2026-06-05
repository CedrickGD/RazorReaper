using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RazorReaper.Services.Implementations.Memory;

/// <summary>
/// P/Invoke surface + native constants/structs for <see cref="ProcessMemoryService"/>.
/// Kept in its own partial, mirroring the Crosshair service's interop split.
/// </summary>
public sealed partial class ProcessMemoryService
{
    // ── OpenProcess access rights ───────────────────────────────────────────
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // ── Memory state / type ─────────────────────────────────────────────────
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint MEM_MAPPED = 0x40000;
    private const uint MEM_IMAGE = 0x1000000;

    // ── Page protection ─────────────────────────────────────────────────────
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    private const uint PAGE_GUARD = 0x100;

    // ── Misc ────────────────────────────────────────────────────────────────
    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
    private const uint LIST_MODULES_ALL = 0x03;
    private const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1; // 4-byte pad on x64
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle handle, IntPtr address, [Out] byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(SafeProcessHandle handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(SafeProcessHandle handle, IntPtr address, IntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(SafeProcessHandle handle, IntPtr address, out MEMORY_BASIC_INFORMATION mbi, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(SafeProcessHandle handle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModulesEx(SafeProcessHandle handle, [Out] IntPtr[] modules, uint cb, out uint needed, uint filterFlag);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleBaseNameW(SafeProcessHandle handle, IntPtr module, System.Text.StringBuilder baseName, uint size);

    /// <summary>True if the page protection allows reads (and isn't a guard/no-access page).</summary>
    private static bool IsReadableProtect(uint protect)
    {
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        var p = protect & 0xFF;
        return p is PAGE_READONLY or PAGE_READWRITE or PAGE_WRITECOPY
            or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;
    }
}
