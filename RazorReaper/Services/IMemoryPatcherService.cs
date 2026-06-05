using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Live-apply orchestration on top of <see cref="IProcessMemoryService"/>. Attaches to a
/// running ShooterGame, scans for the Sky Injector's patched textures in memory, and (gated)
/// writes them live + nudges a GPU re-upload via the console. Intended for unofficial /
/// single-player only; anti-cheat is detected and surfaced (warn-but-allow), never blocked.
/// </summary>
public interface IMemoryPatcherService : IDisposable
{
    bool IsAttached { get; }
    int? AttachedProcessId { get; }
    bool AttachedForWrite { get; }
    AntiCheatStatus AntiCheat { get; }
    string? AntiCheatModule { get; }

    /// <summary>Raised on attach / detach / auto-detach so the UI can refresh.</summary>
    event Action? StateChanged;

    /// <summary>Open ShooterGame. <paramref name="forWrite"/> requests write rights (needed for live apply).</summary>
    Task<MemoryAttachResult> AttachAsync(bool forWrite, CancellationToken ct = default);

    /// <summary>Close the handle and stop tracking. Idempotent.</summary>
    void Detach();

    /// <summary>
    /// READ-ONLY discovery: for each injected sky texture, report how many times its original
    /// data-region bytes still appear in the live process. 0 matches is the common, expected
    /// case (the CPU copy is usually freed after GPU upload). Writes nothing.
    /// </summary>
    Task<IReadOnlyList<TextureScanFinding>> ScanSkyTexturesAsync(IProgress<MemoryScanProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// EXPERIMENTAL, gated: locate each injected texture's original bytes in memory, overwrite
    /// them with the patched bytes, then nudge a GPU re-upload via console streaming commands.
    /// Requires <paramref name="allowWrite"/> and a write attach. May not visibly change the sky.
    /// </summary>
    Task<IReadOnlyList<LiveTexturePatchResult>> ApplySkyLiveAsync(bool allowWrite, IProgress<MemoryScanProgress>? progress = null, CancellationToken ct = default);

    // ── Advanced / manual mode (discover-from-scratch tooling) ──────────────
    Task<MemoryScanResult> ScanForHexAsync(string hexPattern, IProgress<MemoryScanProgress>? progress = null, CancellationToken ct = default);
    bool TryReadHex(ulong address, int length, out string hex, out string? error);
    bool TryWriteHex(ulong address, string hexBytes, out string? error);

    /// <summary>Send a raw console command to the live game (e.g. a manual streaming nudge).</summary>
    Task<bool> SendConsoleAsync(string command, CancellationToken ct = default);
}
