using System.Buffers;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.Memory;

public sealed partial class ProcessMemoryService
{
    // User-mode address space on x64 tops out below this; the VirtualQueryEx walk stops here.
    private const ulong UserSpaceMax = 0x00007FFFFFFF0000UL;
    private const int ScanChunk = 8 * 1024 * 1024; // 8 MiB

    public IEnumerable<MemoryRegionInfo> EnumerateRegions(MemoryRegionFilter filter)
    {
        var handle = CurrentHandle;
        if (handle is null) yield break;

        var mbiSize = new IntPtr(System.Runtime.InteropServices.Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
        ulong cursor = 0;

        while (cursor < UserSpaceMax)
        {
            if (VirtualQueryEx(handle, new IntPtr(unchecked((long)cursor)), out var mbi, mbiSize) == IntPtr.Zero)
                break;

            var regionSize = (ulong)mbi.RegionSize.ToInt64();
            if (regionSize == 0) break;

            if (mbi.State == MEM_COMMIT && TypeAllowed(mbi.Type, filter))
            {
                yield return new MemoryRegionInfo(
                    (ulong)mbi.BaseAddress.ToInt64(),
                    regionSize,
                    mbi.Protect,
                    mbi.State,
                    mbi.Type,
                    IsReadableProtect(mbi.Protect));
            }

            var next = cursor + regionSize;
            if (next <= cursor) break; // overflow guard
            cursor = next;
        }
    }

    private static bool TypeAllowed(uint type, MemoryRegionFilter filter)
    {
        if (type == MEM_PRIVATE) return (filter & MemoryRegionFilter.IncludePrivate) != 0;
        if (type == MEM_MAPPED) return (filter & MemoryRegionFilter.IncludeMapped) != 0;
        if (type == MEM_IMAGE) return (filter & MemoryRegionFilter.IncludeImage) != 0;
        return false;
    }

    public MemoryScanResult ScanForSequence(byte[] needle, MemoryRegionFilter filter, long maxBytesToScan,
        IProgress<MemoryScanProgress>? progress, CancellationToken ct)
        => ScanForSequences(new[] { needle }, filter, maxBytesToScan, progress, ct)[0];

    public IReadOnlyList<MemoryScanResult> ScanForSequences(IReadOnlyList<byte[]> needles, MemoryRegionFilter filter,
        long maxBytesToScan, IProgress<MemoryScanProgress>? progress, CancellationToken ct)
    {
        var k = needles.Count;
        var matchLists = new List<ulong>[k];
        var lastAddr = new ulong[k];
        var hasLast = new bool[k];
        var maxLen = 0;
        for (var i = 0; i < k; i++)
        {
            matchLists[i] = new List<ulong>();
            var len = needles[i]?.Length ?? 0;
            if (len > maxLen) maxLen = len;
        }

        long bytesScanned = 0;
        var regionsScanned = 0;
        var cancelled = false;
        var capReached = false;

        MemoryScanResult Build(int i) => new(matchLists[i], bytesScanned, regionsScanned, cancelled, capReached);

        if (maxLen == 0 || maxLen > ScanChunk)
        {
            var empties = new MemoryScanResult[k];
            for (var i = 0; i < k; i++) empties[i] = Build(i);
            return empties;
        }

        var overlap = maxLen - 1; // advance overlap sized for the longest needle
        var buffer = ArrayPool<byte>.Shared.Rent(ScanChunk);
        try
        {
            foreach (var region in EnumerateRegions(filter))
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (bytesScanned >= maxBytesToScan) { capReached = true; break; }
                regionsScanned++;
                if (!region.Readable) continue;

                var addr = region.BaseAddress;
                var end = region.BaseAddress + region.RegionSize;

                while (addr < end)
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    if (bytesScanned >= maxBytesToScan) { capReached = true; break; }

                    var remaining = end - addr;
                    var finalChunk = remaining <= (ulong)ScanChunk;
                    var want = finalChunk ? (int)remaining : ScanChunk;

                    if (!TryRead(addr, buffer, want, out var got) || got <= 0)
                        break; // page freed/guarded mid-scan — abandon the rest of this region

                    var isFinal = finalChunk || got < want;
                    var span = buffer.AsSpan(0, got);

                    for (var i = 0; i < k; i++)
                    {
                        var needle = needles[i];
                        var n = needle?.Length ?? 0;
                        if (n == 0) continue;

                        var idx = 0;
                        while (idx <= got - n)
                        {
                            var found = span.Slice(idx).IndexOf(needle);
                            if (found < 0) break;
                            var abs = idx + found;
                            var a = addr + (ulong)abs;
                            // Ascending order + per-needle high-water mark dedups the overlap re-reads
                            // (the advance uses the longest needle's overlap, so shorter needles can
                            // legitimately re-see a match across the chunk boundary).
                            if (!hasLast[i] || a > lastAddr[i])
                            {
                                matchLists[i].Add(a);
                                lastAddr[i] = a;
                                hasLast[i] = true;
                            }
                            idx = abs + 1;
                        }
                    }

                    bytesScanned += got;
                    if (progress is not null)
                    {
                        var total = 0;
                        for (var i = 0; i < k; i++) total += matchLists[i].Count;
                        progress.Report(new MemoryScanProgress(bytesScanned, regionsScanned, total));
                    }

                    if (isFinal) break;
                    addr += (ulong)(got - overlap);
                }

                if (cancelled || capReached) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var results = new MemoryScanResult[k];
        for (var i = 0; i < k; i++) results[i] = Build(i);
        return results;
    }
}
