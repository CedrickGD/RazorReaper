using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Thin, reusable wrapper over the Win32 process-memory APIs (OpenProcess / Read- and
/// WriteProcessMemory / VirtualProtectEx / VirtualQueryEx). ARK-agnostic.
///
/// Deliberately limited to pure read/write/scan — NO DLL injection, NO CreateRemoteThread,
/// NO API hooks — to keep the footprint as small and anti-cheat-uninteresting as possible.
/// One process is attached at a time; the OS handle is held in a <see cref="System.Runtime.InteropServices.SafeHandle"/>
/// and never leaked. All methods are non-throwing: failures are reported via return values.
/// </summary>
public interface IProcessMemoryService : IDisposable
{
    bool IsAttached { get; }
    int? AttachedProcessId { get; }
    bool AttachedForWrite { get; }

    /// <summary>
    /// Open the target process. <paramref name="forWrite"/> = false requests the minimal
    /// rights for reading/scanning (VM_READ | QUERY_INFORMATION); true additionally requests
    /// VM_WRITE | VM_OPERATION. Refuses if the target is not 64-bit. Never throws.
    /// </summary>
    MemoryAttachResult Attach(int processId, bool forWrite);

    /// <summary>Close the handle and reset state. Idempotent.</summary>
    void Detach();

    /// <summary>Read up to <paramref name="length"/> bytes into <paramref name="buffer"/> (from index 0). Partial-read aware; returns false on any failure.</summary>
    bool TryRead(ulong address, byte[] buffer, int length, out int read);

    /// <summary>VirtualProtectEx→RW, WriteProcessMemory, then restore the original protection. Requires a write attach.</summary>
    bool TryWrite(ulong address, byte[] data, out string? error);

    /// <summary>Walk committed regions via VirtualQueryEx, filtered by region type. Lazy.</summary>
    IEnumerable<MemoryRegionInfo> EnumerateRegions(MemoryRegionFilter filter);

    /// <summary>Lower-cased loaded module names (for anti-cheat detection). Empty on failure.</summary>
    IReadOnlyList<string> EnumerateModuleNames();

    /// <summary>
    /// Scan committed readable regions for an exact byte sequence. Chunked + cancellable;
    /// overlapping reads catch boundary-straddling matches. Bounded by <paramref name="maxBytesToScan"/>.
    /// </summary>
    MemoryScanResult ScanForSequence(byte[] needle, MemoryRegionFilter filter, long maxBytesToScan, IProgress<MemoryScanProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Scan once for many needles simultaneously — a single memory traversal that searches for
    /// every pattern per chunk. Returns one <see cref="MemoryScanResult"/> per input needle (by
    /// index). Far cheaper than N separate scans when checking many sky textures at once.
    /// </summary>
    IReadOnlyList<MemoryScanResult> ScanForSequences(IReadOnlyList<byte[]> needles, MemoryRegionFilter filter, long maxBytesToScan, IProgress<MemoryScanProgress>? progress, CancellationToken ct);
}
