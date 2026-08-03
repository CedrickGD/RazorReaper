using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Media;

/// <summary>Setup phase reported while ffmpeg is being fetched for the first time.</summary>
public enum FfmpegSetupPhase
{
    Downloading,
    Extracting,
    Done
}

/// <summary>Progress of the one-time ffmpeg download+extract (percent is -1 when indeterminate).</summary>
public sealed record FfmpegSetupProgress(FfmpegSetupPhase Phase, int Percent);

/// <summary>
/// Locates a bundled ffmpeg.exe, downloading it once into
/// %LOCALAPPDATA%\RazorReaper\Tools\ffmpeg on first use (the repo ships no binaries).
/// Nothing in the app depends on ffmpeg until the user asks to convert a video, so the
/// ~80 MB fetch is deferred to that moment and cached forever after.
/// </summary>
public interface IFfmpegProvider
{
    /// <summary>True when ffmpeg.exe is already present locally (no download needed).</summary>
    bool IsInstalled { get; }

    /// <summary>Path where ffmpeg.exe lives (whether or not it exists yet).</summary>
    string FfmpegPath { get; }

    /// <summary>
    /// Ensure ffmpeg.exe is available, downloading + extracting it if necessary.
    /// Returns the exe path, or null if it could not be obtained. Concurrent callers share one fetch.
    /// </summary>
    Task<string?> EnsureAsync(IProgress<FfmpegSetupProgress>? progress, CancellationToken cancellationToken = default);
}

public sealed class FfmpegProvider : IFfmpegProvider
{
    // Static win64 builds. gyan.dev "essentials" is the smaller primary (~110 MB zip);
    // BtbN's GPL build is the fallback. Both bundle ffmpeg.exe under a bin/ folder.
    private static readonly string[] DownloadUrls =
    {
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
    };

    private readonly ILogger<FfmpegProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _toolsDir;
    private readonly string _ffmpegPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FfmpegProvider(ILogger<FfmpegProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "Tools", "ffmpeg");
        _ffmpegPath = Path.Combine(_toolsDir, "ffmpeg.exe");

        // Shipped alongside the exe by the csproj (see the Toolsfmpeg.exe ItemGroup).
        // Preferred over the downloaded copy so a normal install converts offline on the
        // very first run. The download path below stays as the fallback for builds made
        // from a fresh clone, where the gitignored binary is absent.
        _bundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    }

    private readonly string _bundledPath;

    /// <summary>The bundled binary if it shipped, otherwise the downloaded one.</summary>
    private string? ResolveExisting()
    {
        if (File.Exists(_bundledPath)) return _bundledPath;
        if (File.Exists(_ffmpegPath)) return _ffmpegPath;
        return null;
    }

    public bool IsInstalled => ResolveExisting() is not null;

    public string FfmpegPath => ResolveExisting() ?? _ffmpegPath;

    public async Task<string?> EnsureAsync(IProgress<FfmpegSetupProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (ResolveExisting() is { } existing) return existing;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ResolveExisting() is { } raced) return raced;

            Directory.CreateDirectory(_toolsDir);
            var tempZip = Path.Combine(_toolsDir, $"ffmpeg-download-{Guid.NewGuid():N}.zip");
            var tempExtract = Path.Combine(_toolsDir, $"extract-{Guid.NewGuid():N}");

            try
            {
                Exception? lastError = null;
                foreach (var url in DownloadUrls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await DownloadAsync(url, tempZip, progress, cancellationToken);

                        progress?.Report(new FfmpegSetupProgress(FfmpegSetupPhase.Extracting, -1));
                        var extracted = ExtractFfmpeg(tempZip, tempExtract);
                        if (extracted is null)
                        {
                            lastError = new InvalidOperationException("ffmpeg.exe not found inside the downloaded archive");
                            continue;
                        }

                        // Atomic install: extracted is on the same volume as _ffmpegPath, so
                        // Move is a rename — a crash leaves either no file or the complete one,
                        // never a truncated binary that IsInstalled would wrongly accept.
                        File.Move(extracted, _ffmpegPath, overwrite: true);
                        progress?.Report(new FfmpegSetupProgress(FfmpegSetupPhase.Done, 100));
                        _logger.LogInformation("ffmpeg installed to {Path} from {Url}", _ffmpegPath, url);
                        return _ffmpegPath;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        _logger.LogWarning(ex, "ffmpeg fetch from {Url} failed; trying next source", url);
                    }
                    finally
                    {
                        TryDelete(tempZip);
                        TryDeleteDir(tempExtract);
                    }
                }

                _logger.LogError(lastError, "Could not obtain ffmpeg from any source");
                return null;
            }
            finally
            {
                TryDelete(tempZip);
                TryDeleteDir(tempExtract);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(string url, string targetPath, IProgress<FfmpegSetupProgress>? progress, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        long bytesRead = 0;
        var lastReportedPercent = -1;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        int read;
        while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesRead += read;

            if (totalBytes is > 0)
            {
                var percent = (int)(bytesRead * 100 / totalBytes.Value);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(new FfmpegSetupProgress(FfmpegSetupPhase.Downloading, percent));
                }
            }
            else
            {
                progress?.Report(new FfmpegSetupProgress(FfmpegSetupPhase.Downloading, -1));
            }
        }
    }

    /// <summary>Extract only ffmpeg.exe from the archive; returns its path or null if absent.</summary>
    private static string? ExtractFfmpeg(string zipPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        var dest = Path.Combine(extractDir, "ffmpeg.exe");
        entry.ExtractToFile(dest, overwrite: true);
        return dest;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete temp file {Path}", path); }
    }

    private void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete temp dir {Path}", path); }
    }
}
