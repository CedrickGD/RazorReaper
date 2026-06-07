using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.CustomLab;

/// <summary>
/// Writes the control dir the rr d3d11 proxy reads to swap sky textures live. Format (must match
/// native/rr_proxy/d3d11_proxy.cpp exactly):
///   %LOCALAPPDATA%\RazorReaper\LiveSky\
///     enabled          — presence = armed
///     gen.txt          — integer bumped each apply (signals the proxy to re-skin already-loaded textures)
///     targets.txt      — "&lt;fnv1a64-hex&gt; &lt;W&gt; &lt;H&gt;" per SimpleSky original
///     sky_&lt;W&gt;x&lt;H&gt;.bin — the user's image as a BC3 full mip chain for that dimension
/// </summary>
public class LiveSkyService : ILiveSkyService
{
    private readonly ISkyInjectorService _injector;
    private readonly IArkPathProvider _arkPaths;
    private readonly ILogger<LiveSkyService> _logger;

    public LiveSkyService(ISkyInjectorService injector, IArkPathProvider arkPaths, ILogger<LiveSkyService> logger)
    {
        _injector = injector;
        _arkPaths = arkPaths;
        _logger = logger;
        ControlDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "LiveSky");
    }

    public string ControlDir { get; }

    public bool IsActive => File.Exists(Path.Combine(ControlDir, "enabled"));

    // FNV-1a-64 — byte-identical to FNV1a64() in d3d11_proxy.cpp.
    private static ulong Fnv1a64(byte[] data, int offset, int len)
    {
        ulong h = 14695981039346656037UL;
        for (var i = 0; i < len; i++) { h ^= data[offset + i]; h *= 1099511628211UL; }
        return h;
    }

    public async Task<LiveSkyResult> ApplyAsync(SkyInjectionOptions options, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (options.Mode != SkyInjectionMode.Image)
        {
            // The live engine swaps in a user image; solid-color uses the same path via a 4x4 source.
        }

        // 1) Load the source image (or synthesized color), once.
        SkiaSharp.SKBitmap? source = options.Mode == SkyInjectionMode.Image
            ? (string.IsNullOrWhiteSpace(options.ImagePath) || !File.Exists(options.ImagePath)
                ? null : SkyImagePipeline.Load(options.ImagePath))
            : SkyImagePipeline.SynthesizeSolidColor(options.HexColor);
        if (source is null)
            return new LiveSkyResult(0, 0, false, new[] { "Image could not be loaded — pick a valid file." });

        try
        {
            if (options is { Mode: SkyInjectionMode.Image, FlipVertically: true })
            {
                var flipped = SkyImagePipeline.FlipVertically(source);
                source.Dispose();
                source = flipped;
            }

            // 2) Fingerprint every discoverable SimpleSky_* (DXT5/BC3) original.
            var infos = await _injector.DiscoverSkyTexturesAsync(ct);
            var targets = new List<string>();
            var dims = new HashSet<(int W, int H)>();
            foreach (var info in infos)
            {
                ct.ThrowIfCancellationRequested();
                if (info.Kind != SkyTextureKind.Dxt5) continue;   // engine matches BC3 only
                try
                {
                    var bytes = await File.ReadAllBytesAsync(info.Path, ct);
                    if (info.DataOffset < 0 || info.DataSize <= 0 || info.DataOffset + info.DataSize > bytes.Length)
                        continue;
                    var hash = Fnv1a64(bytes, info.DataOffset, info.DataSize);
                    targets.Add($"{hash:x16} {info.Width} {info.Height}");
                    dims.Add((info.Width, info.Height));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Live sky fingerprint skipped: {Path}", info.Path);
                }
            }

            if (targets.Count == 0)
                return new LiveSkyResult(0, 0, false,
                    new[] { "No SimpleSky_* textures found to fingerprint — this map's sky may not be replaceable." });

            // 3) Encode the user's image to BC3 (full mip chain) per sky dimension.
            Directory.CreateDirectory(ControlDir);
            var tile = options.Mode == SkyInjectionMode.SolidColor ? 1 : Math.Clamp(options.TileSize, 1, 4);
            var encoder = new BcEncoder
            {
                OutputOptions = { Format = CompressionFormat.Bc3, Quality = CompressionQuality.Balanced, GenerateMipMaps = true }
            };
            foreach (var (w, h) in dims)
            {
                ct.ThrowIfCancellationRequested();
                using var prepared = SkyImagePipeline.ResizeAndTile(source, w, h, tile);
                var rgba = SkyImagePipeline.GetRgbaBytes(prepared);
                var mips = encoder.EncodeToRawBytes(rgba, w, h, PixelFormat.Rgba32);  // jagged, base..1x1
                using var blob = new MemoryStream();
                foreach (var mip in mips) blob.Write(mip, 0, mip.Length);
                await File.WriteAllBytesAsync(Path.Combine(ControlDir, $"sky_{w}x{h}.bin"), blob.ToArray(), ct);
            }

            // 4) targets + gen bump + arm.
            await File.WriteAllLinesAsync(Path.Combine(ControlDir, "targets.txt"), targets.Distinct(), ct);
            await File.WriteAllTextAsync(Path.Combine(ControlDir, "gen.txt"), NextGen().ToString(), ct);
            await File.WriteAllTextAsync(Path.Combine(ControlDir, "enabled"), "1", ct);

            var proxyInstalled = EnsureProxyInstalled(errors);
            _logger.LogInformation("Live sky applied: {Targets} targets, {Dims} dims, proxy={Proxy}",
                targets.Count, dims.Count, proxyInstalled);
            return new LiveSkyResult(targets.Distinct().Count(), dims.Count, proxyInstalled, errors);
        }
        finally
        {
            source.Dispose();
        }
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        try
        {
            var enabled = Path.Combine(ControlDir, "enabled");
            if (File.Exists(enabled)) File.Delete(enabled);
            // bump gen so a future proxy poll notices the disarm
            if (Directory.Exists(ControlDir))
                File.WriteAllText(Path.Combine(ControlDir, "gen.txt"), NextGen().ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live sky disable failed");
        }
        return Task.CompletedTask;
    }

    private int NextGen()
    {
        try
        {
            var p = Path.Combine(ControlDir, "gen.txt");
            if (File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out var g)) return g + 1;
        }
        catch { /* best effort */ }
        return 1;
    }

    // Best-effort: ensure the proxy is present in ARK's Win64. Does NOT overwrite an existing
    // d3d11.dll (it may be loaded by a running ARK); a fresh install needs one ARK relaunch to load.
    private bool EnsureProxyInstalled(List<string> errors)
    {
        try
        {
            var ark = _arkPaths.FindArkPath();
            if (string.IsNullOrEmpty(ark)) { errors.Add("ARK install not found — install the proxy manually."); return false; }
            var win64 = Path.Combine(ark, "ShooterGame", "Binaries", "Win64");
            if (!Directory.Exists(win64)) { errors.Add("ARK Win64 folder not found."); return false; }

            var proxy = Path.Combine(win64, "d3d11.dll");
            if (File.Exists(proxy)) return true;   // already installed (possibly loaded) — leave it

            var orig = Path.Combine(win64, "d3d11orig.dll");
            if (!File.Exists(orig))
            {
                var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d11.dll");
                if (File.Exists(sys)) File.Copy(sys, orig, false);
            }
            var shipped = Path.Combine(AppContext.BaseDirectory, "native", "d3d11.dll");
            if (File.Exists(shipped)) { File.Copy(shipped, proxy, false); return true; }

            errors.Add("Proxy d3d11.dll not bundled with the app — install it into ARK's Win64 manually.");
            return false;
        }
        catch (Exception ex)
        {
            errors.Add($"Proxy install failed: {ex.Message}");
            return false;
        }
    }
}
