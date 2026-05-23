using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;
using SkiaSharp;

namespace RazorReaper.Services.Implementations.CustomLab;

/// <summary>
/// Discovery + backup + BC3/BGRA8 encode + splice. Reads each candidate .uasset,
/// parses its texture header, backs up the original on first inject, then writes
/// freshly-encoded bytes into the data region.
/// </summary>
public class SkyInjectorService : ISkyInjectorService
{
    // Substring matches against the .uasset filename — mirrors t1m's inject_custom_sky.py:181-194.
    private static readonly string[] DxtFilenameSubstrings = new[]
    {
        "SimpleSky_0", "SimpleSky_1", "SimpleSky_2",
        "SE_SimpleSky", "T_EXT_SimpleSky",
        "T_EXT_Sky_Wastleland_Cloud_Frame",
        "SimpleSky_Bog", "SimpleSky_Snow",
        "SimpleSky_Ocean", "SimpleSky_Volcano"
    };

    private static readonly HashSet<string> DxtFilenameExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "SimpleSky_Rain.uasset",
        "SimpleSky_Bog_Rain.uasset",
        "SimpleSky_Snow_Rain.uasset",
        "SimpleSky_Ocean_Rain.uasset",
        "SimpleSky_Volcano_Rain.uasset",
        "T_EXT_SimpleSky_Rain.uasset"
    };

    // Gen2 sky textures — t1m's restore script knows about these but the inject script doesn't
    // patch them. We do.
    private const string Gen2RelativeRoot = @"Genesis2\Environment\Sky\Snapshots";

    // Mod 3371620674 ships a BGRA8 sky texture instead of DXT5.
    private const string Bgra8ModRelativePath =
        @"Mods\3371620674\Assets\Textures\blue-sky-with-white-clouds.uasset";

    private readonly ILogger<SkyInjectorService> _logger;
    private readonly IArkPathProvider _arkPaths;
    private readonly IActivityService _activity;
    private readonly ITelemetryService _telemetry;
    private readonly ICustomLabSettingsService _settings;
    private readonly IProcessService _process;
    private readonly IOptions<AppConfiguration> _config;

    public SkyInjectorService(
        ILogger<SkyInjectorService> logger,
        IArkPathProvider arkPaths,
        IActivityService activity,
        ITelemetryService telemetry,
        ICustomLabSettingsService settings,
        IProcessService process,
        IOptions<AppConfiguration> config)
    {
        _logger = logger;
        _arkPaths = arkPaths;
        _activity = activity;
        _telemetry = telemetry;
        _settings = settings;
        _process = process;
        _config = config;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper");
        Directory.CreateDirectory(appData);
        BackupFolderPath = Path.Combine(appData, "SkyBackups");
    }

    public string BackupFolderPath { get; }

    public bool HasBackup()
    {
        return Directory.Exists(BackupFolderPath)
            && Directory.EnumerateFiles(BackupFolderPath, "*.bak").Any();
    }

    public (long Bytes, int Files) GetBackupStats()
    {
        if (!Directory.Exists(BackupFolderPath)) return (0, 0);
        long total = 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(BackupFolderPath, "*.bak"))
            {
                try
                {
                    total += new FileInfo(file).Length;
                    count++;
                }
                catch
                {
                    // Skip files that disappear mid-enumeration; this is best-effort.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stat sky backup folder");
        }
        return (total, count);
    }

    public async Task<int> ClearBackupsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(BackupFolderPath)) return 0;

        var deleted = 0;
        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(BackupFolderPath, "*.bak"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete backup {File}", file);
                }
            }
        }, ct);

        if (deleted > 0)
        {
            _activity.AddActivity($"Custom Lab: cleared {deleted} sky backup(s)", "warning");
            await _settings.ClearSkyTimestampsAsync();
            _ = _telemetry.TrackEventAsync(
                "custom_lab.sky_backups_cleared",
                metrics: new Dictionary<string, object?> { ["deleted"] = deleted },
                cancellationToken: ct);
        }
        return deleted;
    }

    public void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupFolderPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = BackupFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open backup folder");
        }
    }

    public async Task<IReadOnlyList<SkyTextureInfo>> DiscoverSkyTexturesAsync(CancellationToken ct = default)
    {
        await _settings.LoadAsync();
        var contentRoot = GetArkContentRoot();
        if (contentRoot is null) return Array.Empty<SkyTextureInfo>();

        var results = new List<SkyTextureInfo>();
        await Task.Run(() =>
        {
            foreach (var path in EnumerateCandidateUassetPaths(contentRoot))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (TryClassify(path, bytes, out var info))
                    {
                        results.Add(info);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Sky texture parse skipped: {Path}", path);
                }
            }
        }, ct);

        return results;
    }

    public async Task<SkyInjectionResult> InjectAsync(SkyInjectionOptions options, CancellationToken ct = default)
    {
        await _settings.LoadAsync();
        if (!_settings.Current.MasterEnabled)
        {
            return new SkyInjectionResult(0, 0, new[] { "Custom Lab is disabled — enable it in Settings before injecting." });
        }
        if (!_settings.Current.Accepted)
        {
            return new SkyInjectionResult(0, 0, new[] { "Read Me has not been accepted." });
        }

        var contentRoot = GetArkContentRoot();
        if (contentRoot is null)
        {
            return new SkyInjectionResult(0, 0, new[] { "ARK installation not found. Verify ARK is installed via Steam." });
        }

        Directory.CreateDirectory(BackupFolderPath);

        // Source image (or synthesized color), prepared once. Sky textures share their dimensions
        // in a small set ({256², 512², 1024², 2048²}) so we cache the encoded bytes per (w, h, kind).
        SKBitmap? source = null;
        try
        {
            if (options.Mode == SkyInjectionMode.Image)
            {
                if (string.IsNullOrWhiteSpace(options.ImagePath) || !File.Exists(options.ImagePath))
                    return new SkyInjectionResult(0, 0, new[] { "Image file not found." });

                source = SkyImagePipeline.Load(options.ImagePath);
                if (source is null)
                    return new SkyInjectionResult(0, 0, new[] { "Image could not be decoded — unsupported format?" });

                if (options.FlipVertically)
                {
                    var flipped = SkyImagePipeline.FlipVertically(source);
                    source.Dispose();
                    source = flipped;
                }
            }
            else
            {
                source = SkyImagePipeline.SynthesizeSolidColor(options.HexColor);
                if (source is null)
                    return new SkyInjectionResult(0, 0, new[] { $"Invalid hex color: {options.HexColor}" });
            }

            var tileSize = options.Mode == SkyInjectionMode.SolidColor ? 1 : Math.Clamp(options.TileSize, 1, 4);

            var patched = 0;
            var skipped = 0;
            var errors = new List<string>();

            var dxt5Cache = new Dictionary<(int W, int H), byte[]>();
            var bgra8Cache = new Dictionary<(int W, int H), byte[]>();
            var encoder = new BcEncoder
            {
                OutputOptions =
                {
                    Format = CompressionFormat.Bc3,
                    Quality = CompressionQuality.Balanced,
                    GenerateMipMaps = false
                }
            };

            await Task.Run(() =>
            {
                foreach (var path in EnumerateCandidateUassetPaths(contentRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (!TryClassify(path, bytes, out var info))
                        {
                            skipped++;
                            continue;
                        }

                        var key = (info.Width, info.Height);
                        byte[] encoded;
                        if (info.Kind == SkyTextureKind.Dxt5)
                        {
                            if (!dxt5Cache.TryGetValue(key, out encoded!))
                            {
                                using var prepared = SkyImagePipeline.ResizeAndTile(source!, info.Width, info.Height, tileSize);
                                var rgba = SkyImagePipeline.GetRgbaBytes(prepared);
                                // EncodeToRawBytes returns a jagged array of mip levels; we set
                                // GenerateMipMaps=false so only the base level (index 0) is produced.
                                var mips = encoder.EncodeToRawBytes(rgba, info.Width, info.Height, PixelFormat.Rgba32);
                                encoded = mips[0];
                                dxt5Cache[key] = encoded;
                            }
                        }
                        else
                        {
                            if (!bgra8Cache.TryGetValue(key, out encoded!))
                            {
                                using var prepared = SkyImagePipeline.ResizeAndTile(source!, info.Width, info.Height, tileSize);
                                encoded = SkyImagePipeline.GetBgraBytes(prepared);
                                bgra8Cache[key] = encoded;
                            }
                        }

                        if (encoded.Length != info.DataSize)
                        {
                            errors.Add($"{Path.GetFileName(path)}: encoded {encoded.Length} bytes but file expects {info.DataSize}");
                            continue;
                        }

                        EnsureBackup(contentRoot, path, bytes);

                        Buffer.BlockCopy(encoded, 0, bytes, info.DataOffset, info.DataSize);
                        File.WriteAllBytes(path, bytes);
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        var rel = SafeRelative(contentRoot, path);
                        _logger.LogWarning(ex, "Inject failed for {Path}", path);
                        errors.Add($"{rel}: {ex.Message}");
                    }
                }
            }, ct);

            _activity.AddActivity(
                errors.Count == 0 ? $"Sky injected → {patched} texture(s)"
                                  : $"Sky inject → {patched} ok, {errors.Count} errors",
                errors.Count == 0 ? "success" : "warning");

            if (patched > 0) await _settings.MarkSkyInjectedAsync();

            _ = _telemetry.TrackEventAsync(
                "custom_lab.sky_inject",
                errors.Count == 0 ? TelemetryEventStatus.Ok : TelemetryEventStatus.Degraded,
                metrics: new Dictionary<string, object?>
                {
                    ["patched"] = patched,
                    ["skipped"] = skipped,
                    ["errors"] = errors.Count,
                    ["mode"] = options.Mode.ToString(),
                    ["tile"] = tileSize,
                    ["dxt5_dims"] = dxt5Cache.Count,
                    ["bgra8_dims"] = bgra8Cache.Count
                },
                cancellationToken: ct);

            return new SkyInjectionResult(patched, skipped, errors);
        }
        finally
        {
            source?.Dispose();
        }
    }

    public async Task<SkyInjectionResult> RestoreAsync(CancellationToken ct = default)
    {
        await _settings.LoadAsync();
        var contentRoot = GetArkContentRoot();
        if (contentRoot is null)
        {
            return new SkyInjectionResult(0, 0, new[] { "ARK installation not found." });
        }
        if (!Directory.Exists(BackupFolderPath))
        {
            return new SkyInjectionResult(0, 0, new[] { "No backups to restore. Inject a sky first." });
        }

        var restored = 0;
        var skipped = 0;
        var errors = new List<string>();

        await Task.Run(() =>
        {
            // Walk the ARK content tree (not the backup folder). For each candidate, compute the
            // backup name the inject pass would have produced and restore if it exists. This sidesteps
            // the can't-distinguish-underscores problem we'd hit decoding "PrimalEarth_…_SimpleSky_0".
            foreach (var path in EnumerateCandidateUassetPaths(contentRoot))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rel = SafeRelative(contentRoot, path);
                    var backupName = EncodeBackupName(rel);
                    var backupPath = Path.Combine(BackupFolderPath, backupName);
                    if (!File.Exists(backupPath))
                    {
                        skipped++;
                        continue;
                    }
                    File.Copy(backupPath, path, overwrite: true);
                    restored++;
                }
                catch (Exception ex)
                {
                    var rel = SafeRelative(contentRoot, path);
                    _logger.LogWarning(ex, "Restore failed for {Path}", path);
                    errors.Add($"{rel}: {ex.Message}");
                }
            }
        }, ct);

        _activity.AddActivity(
            errors.Count == 0 ? $"Sky restored → {restored} file(s)"
                              : $"Sky restore → {restored} ok, {errors.Count} errors",
            errors.Count == 0 ? "info" : "warning");

        if (restored > 0) await _settings.MarkSkyRestoredAsync();

        _ = _telemetry.TrackEventAsync(
            "custom_lab.sky_restore",
            errors.Count == 0 ? TelemetryEventStatus.Ok : TelemetryEventStatus.Degraded,
            metrics: new Dictionary<string, object?>
            {
                ["restored"] = restored,
                ["skipped"] = skipped,
                ["errors"] = errors.Count
            },
            cancellationToken: ct);

        return new SkyInjectionResult(restored, skipped, errors);
    }

    private string? GetArkContentRoot()
    {
        var ark = _arkPaths.FindArkPath();
        if (string.IsNullOrEmpty(ark)) return null;
        var content = Path.Combine(ark, "ShooterGame", "Content");
        return Directory.Exists(content) ? content : null;
    }

    private static IEnumerable<string> EnumerateCandidateUassetPaths(string contentRoot)
    {
        foreach (var path in Directory.EnumerateFiles(contentRoot, "*.uasset", SearchOption.AllDirectories))
        {
            if (IsSkyCandidate(path)) yield return path;
        }
    }

    private static bool IsSkyCandidate(string path)
    {
        var name = Path.GetFileName(path);

        // BGRA8 mod sky.
        if (path.EndsWith(Bgra8ModRelativePath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Gen2 (new for our port; t1m's inject script skips these).
        if (path.Contains(Gen2RelativeRoot, StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("Gen2_Sky", StringComparison.OrdinalIgnoreCase))
            return true;

        // Exact-match Rain variants (avoid partial-match noise like SimpleSky_Rain_Frame).
        if (DxtFilenameExact.Contains(name)) return true;

        // Substring matches for the SimpleSky_* family.
        foreach (var substr in DxtFilenameSubstrings)
        {
            if (name.Contains(substr, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryClassify(string path, ReadOnlySpan<byte> bytes, out SkyTextureInfo info)
    {
        info = null!;

        if (path.EndsWith(Bgra8ModRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            if (UAssetTextureParser.TryParseBgra8(bytes, out var w, out var h, out var off, out var size))
            {
                info = new SkyTextureInfo
                {
                    Path = path,
                    Kind = SkyTextureKind.Bgra8,
                    Width = w,
                    Height = h,
                    DataOffset = off,
                    DataSize = size
                };
                return true;
            }
            return false;
        }

        if (UAssetTextureParser.TryParseDxt5(bytes, out var dw, out var dh, out var doff, out var dsize))
        {
            info = new SkyTextureInfo
            {
                Path = path,
                Kind = SkyTextureKind.Dxt5,
                Width = dw,
                Height = dh,
                DataOffset = doff,
                DataSize = dsize
            };
            return true;
        }

        return false;
    }

    private void EnsureBackup(string contentRoot, string filePath, byte[] originalBytes)
    {
        var rel = SafeRelative(contentRoot, filePath);
        var backupName = EncodeBackupName(rel);
        var backupPath = Path.Combine(BackupFolderPath, backupName);
        if (File.Exists(backupPath)) return;
        File.WriteAllBytes(backupPath, originalBytes);
    }

    private static string SafeRelative(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        // Normalize to backslashes for parity with Python's os.path.relpath under Windows.
        return rel.Replace('/', '\\');
    }

    private static string EncodeBackupName(string relPath)
    {
        // t1m's flat naming: replace separators with underscores, append .bak.
        // The reverse direction is ambiguous (SimpleSky_0 has a real underscore), so RestoreAsync
        // walks the ARK content tree and re-encodes each candidate's path to look up its backup
        // rather than trying to decode the .bak filename.
        return relPath.Replace('\\', '_').Replace('/', '_') + ".bak";
    }
}
