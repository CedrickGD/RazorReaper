namespace RazorReaper.Models;

/// <summary>
/// Whether a known anti-cheat module was detected in the attached process. Drives the
/// warn-but-allow banner in the Memory Patcher — it never blocks the user, only informs.
/// </summary>
public enum AntiCheatStatus
{
    /// <summary>No known anti-cheat module found in the process.</summary>
    None,
    /// <summary>A BattlEye/EAC module is loaded — surface a loud warning.</summary>
    Detected,
    /// <summary>Module enumeration was denied or failed — we can't tell, so warn cautiously.</summary>
    Unknown
}

/// <summary>Outcome of an <c>OpenProcess</c> attach attempt.</summary>
public enum MemoryAttachStatus
{
    Ok,
    ProcessNotFound,
    MultipleProcesses,
    AccessDenied,
    NotExpectedArchitecture,
    AlreadyAttached,
    Disabled,
    Failed
}

/// <summary>Result of attaching to ShooterGame, including the anti-cheat read-out.</summary>
public sealed record MemoryAttachResult(
    MemoryAttachStatus Status,
    int? ProcessId,
    AntiCheatStatus AntiCheat,
    string? AntiCheatModule,
    bool ForWrite,
    string Message)
{
    public bool Attached => Status == MemoryAttachStatus.Ok;
}

/// <summary>
/// Which committed memory region types the scan should walk. Texture data lands in
/// private/mapped pages; module images (code/data) are excluded so a write can never
/// land in executable module memory.
/// </summary>
[Flags]
public enum MemoryRegionFilter
{
    None = 0,
    IncludePrivate = 1, // MEM_PRIVATE
    IncludeMapped = 2,  // MEM_MAPPED — texture upload staging often lands here
    IncludeImage = 4,   // MEM_IMAGE — module code/data, skip for texture scans
    Default = IncludePrivate | IncludeMapped
}

/// <summary>One committed region returned by the VirtualQueryEx walk.</summary>
public sealed record MemoryRegionInfo(
    ulong BaseAddress,
    ulong RegionSize,
    uint Protect,
    uint State,
    uint Type,
    bool Readable);

/// <summary>Progress callback payload for long scans.</summary>
public sealed record MemoryScanProgress(
    long BytesScanned,
    int RegionsScanned,
    int MatchesSoFar);

/// <summary>Result of a byte-sequence scan across the target's committed memory.</summary>
public sealed record MemoryScanResult(
    IReadOnlyList<ulong> MatchAddresses,
    long BytesScanned,
    int RegionsScanned,
    bool Cancelled,
    bool CapReached)
{
    public int MatchCount => MatchAddresses.Count;
}

/// <summary>
/// Read-only scan finding for one injected sky texture: how many times its original
/// data-region bytes appear in the live process (0 is the common, expected case — the
/// CPU copy is usually freed after GPU upload).
/// </summary>
public sealed record TextureScanFinding(
    string TexturePath,
    int Width,
    int Height,
    SkyTextureKind Kind,
    int DataSize,
    int MatchCount,
    IReadOnlyList<ulong> Addresses);

/// <summary>Result of attempting a live write of patched bytes for one texture.</summary>
public sealed record LiveTexturePatchResult(
    string TexturePath,
    int MatchesFound,
    int MatchesWritten,
    int MatchesFailed,
    IReadOnlyList<string> Errors)
{
    public bool AnyWritten => MatchesWritten > 0;
}
