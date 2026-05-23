using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations.CustomLab;

/// <summary>
/// Phase 3b skeleton: discovery + backup + restore. Encoding (BC3 + BGRA8 splice) lands in
/// Phase 3c. <see cref="InjectAsync"/> currently performs backup-only to validate the discovery
/// and backup pipeline end-to-end without risk of corrupting game files.
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

        var patched = 0;
        var skipped = 0;
        var errors = new List<string>();

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

                    EnsureBackup(contentRoot, path, bytes);

                    // Phase 3b: backup-only. Phase 3c plugs in BC3 / BGRA8 splice + WriteAllBytes here.
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
            errors.Count == 0 ? $"Sky inject (backup pass) → {patched} texture(s)"
                              : $"Sky inject (backup pass) → {patched} ok, {errors.Count} errors",
            errors.Count == 0 ? "success" : "warning");

        _ = _telemetry.TrackEventAsync(
            "custom_lab.sky_inject",
            errors.Count == 0 ? TelemetryEventStatus.Ok : TelemetryEventStatus.Degraded,
            metrics: new Dictionary<string, object?>
            {
                ["patched"] = patched,
                ["skipped"] = skipped,
                ["errors"] = errors.Count,
                ["mode"] = options.Mode.ToString(),
                ["tile"] = options.TileSize,
                ["phase"] = "3b-backup-only"
            },
            cancellationToken: ct);

        return new SkyInjectionResult(patched, skipped, errors);
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
            foreach (var bak in Directory.EnumerateFiles(BackupFolderPath, "*.bak"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var relPath = Path.GetFileNameWithoutExtension(bak); // 'PrimalEarth_Environment_Sky_SimpleSky_0.uasset' from .bak
                    var originalRel = DecodeBackupName(relPath);
                    if (originalRel is null)
                    {
                        skipped++;
                        continue;
                    }
                    var originalPath = Path.Combine(contentRoot, originalRel);
                    if (!File.Exists(originalPath))
                    {
                        skipped++;
                        continue;
                    }
                    File.Copy(bak, originalPath, overwrite: true);
                    restored++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Restore failed for {Backup}", bak);
                    errors.Add($"{Path.GetFileName(bak)}: {ex.Message}");
                }
            }
        }, ct);

        _activity.AddActivity(
            errors.Count == 0 ? $"Sky restored → {restored} file(s)"
                              : $"Sky restore → {restored} ok, {errors.Count} errors",
            errors.Count == 0 ? "info" : "warning");

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
        return relPath.Replace('\\', '_').Replace('/', '_') + ".bak";
    }

    private static string? DecodeBackupName(string backupFileBaseName)
    {
        // 'PrimalEarth_Environment_Sky_SimpleSky_0.uasset' → 'PrimalEarth\Environment\Sky\SimpleSky_0.uasset'.
        // We can't recover the separators losslessly (the SimpleSky_0 underscore is real), so we strip
        // the .uasset extension, restore separators on everything else, then re-append .uasset.
        if (!backupFileBaseName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            return null;
        var stem = backupFileBaseName[..^".uasset".Length];

        // Greedy approach: replace ALL underscores with backslashes, then walk the resulting path
        // looking for a *_N suffix that should keep its underscore (SimpleSky_0 etc). This is
        // ambiguous in the general case; we resolve by checking whether the candidate file exists.
        // For now we just round-trip via the inverse of EncodeBackupName by trying both.
        // Phase 3c will likely store an index file (sky-backup-map.json) to avoid this ambiguity.
        var withSeparators = stem.Replace('_', '\\') + ".uasset";
        return withSeparators;
    }
}
