namespace RazorReaper.Services;

using RazorReaper.Models;

/// <summary>
/// Live sky apply — the in-memory counterpart to <see cref="ISkyInjectorService"/>. Instead of
/// patching .uasset files on disk (which needs a game restart), it writes a small control dir that
/// the rr d3d11 proxy reads at runtime and uses to swap the matching sky textures the moment the
/// game (re)creates them — no relaunch. Engine: native/rr_proxy/d3d11_proxy.cpp.
/// </summary>
public interface ILiveSkyService
{
    /// <summary>Absolute path of the control dir the proxy reads (%LOCALAPPDATA%\RazorReaper\LiveSky).</summary>
    string ControlDir { get; }

    /// <summary>True if live sky is currently armed (the <c>enabled</c> flag file exists).</summary>
    bool IsActive { get; }

    /// <summary>
    /// Fingerprint every discoverable SimpleSky_* texture, encode the user's image to BC3 (with a full
    /// mip chain) per sky dimension, and write the control dir so the running proxy applies it live.
    /// Also best-effort installs the proxy into ARK's Win64. Safe to call repeatedly.
    /// </summary>
    Task<LiveSkyResult> ApplyAsync(SkyInjectionOptions options, CancellationToken ct = default);

    /// <summary>Disarm live sky (remove the <c>enabled</c> flag). Already-loaded textures revert on next map load.</summary>
    Task DisableAsync(CancellationToken ct = default);
}

/// <param name="Targets">Number of SimpleSky fingerprints written.</param>
/// <param name="Dimensions">Number of distinct sky dimensions the image was encoded for.</param>
/// <param name="ProxyInstalled">True if the d3d11 proxy is present in ARK's Win64 after this call.</param>
public record LiveSkyResult(int Targets, int Dimensions, bool ProxyInstalled, IReadOnlyList<string> Errors);
