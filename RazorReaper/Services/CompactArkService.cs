using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RazorReaper.Services
{
    /// <summary>
    /// Overall NTFS compression state of the ARK install directory.
    /// </summary>
    public enum CompactArkState
    {
        Unknown,
        NotCompacted,
        PartiallyCompacted,
        Compacted
    }

    /// <summary>
    /// Result of scanning the ARK install: sizes, file counts, and environment checks.
    /// </summary>
    public sealed record CompactArkAnalysis(
        string? InstallPath,
        bool InstallFound,
        bool IsNtfs,
        string? DriveFormat,
        bool IsGameRunning,
        long LogicalBytes,
        long OnDiskBytes,
        int TotalFiles,
        int CompressedFiles,
        CompactArkState State)
    {
        public long SavedBytes => Math.Max(0, LogicalBytes - OnDiskBytes);
    }

    /// <summary>Progress snapshot while scanning the install directory.</summary>
    public sealed record CompactArkAnalyzeProgress(int FilesScanned, long LogicalBytes, long OnDiskBytes);

    /// <summary>Progress snapshot while compact.exe is running.</summary>
    public sealed record CompactArkOperationProgress(int FilesProcessed, int TotalFiles, string? CurrentFile);

    /// <summary>Outcome of a compress or uncompress run.</summary>
    public sealed record CompactArkOperationResult(
        bool Success,
        bool Cancelled,
        int FilesProcessed,
        long LogicalBytes,
        long OnDiskBytesBefore,
        long OnDiskBytesAfter,
        string? ErrorMessage);

    /// <summary>
    /// Service that shrinks the ARK install using Windows transparent NTFS compression
    /// (compact.exe with the LZX algorithm), and measures logical vs on-disk sizes.
    /// </summary>
    public interface ICompactArkService
    {
        /// <summary>
        /// Scans the ARK install directory on a background thread: resolves the install path,
        /// verifies the volume is NTFS, checks whether the game is running, and sums logical
        /// size vs size-on-disk per file to detect the current compression state.
        /// </summary>
        Task<CompactArkAnalysis> AnalyzeAsync(
            IProgress<CompactArkAnalyzeProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs compact.exe /C /S /I /EXE:LZX over the ARK install, streaming per-file
        /// progress. Cancelling the token kills the compact.exe process.
        /// </summary>
        Task<CompactArkOperationResult> CompressAsync(
            IProgress<CompactArkOperationProgress> progress,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs compact.exe /U /S /I /EXE over the ARK install to restore files to their
        /// uncompressed size. Cancelling the token kills the compact.exe process.
        /// </summary>
        Task<CompactArkOperationResult> UncompressAsync(
            IProgress<CompactArkOperationProgress> progress,
            CancellationToken cancellationToken = default);

        /// <summary>Checks whether ARK (ShooterGame) is currently running.</summary>
        bool IsArkRunning();
    }
}

namespace RazorReaper.Services.Implementations
{
    using RazorReaper.Services;

    /// <summary>
    /// Implementation of <see cref="ICompactArkService"/> wrapping Windows compact.exe.
    /// </summary>
    public class CompactArkService : ICompactArkService
    {
        private const string GameProcessName = "ShooterGame";
        private const uint InvalidFileSize = 0xFFFFFFFF;

        // Per-file success line printed by compact.exe while compressing, e.g.
        // "ShooterGame.exe 236032 : 118784 = 2.0 to 1 [OK]"
        private static readonly Regex CompressOkLineRegex = new(
            @"^(?<name>.+?)\s+[\d.,]+\s*:\s*[\d.,]+\s*=\s*[\d.,]+\s+\S+\s+1\s+\[OK\]\s*$",
            RegexOptions.Compiled);

        // Per-file success line while uncompressing, e.g. "ShooterGame.exe [OK]"
        private static readonly Regex SimpleOkLineRegex = new(
            @"^(?<name>.+?)\s+\[OK\]\s*$",
            RegexOptions.Compiled);

        private readonly ILogger<CompactArkService> _logger;
        private readonly IArkPathProvider _arkPathProvider;
        private readonly IProcessService _processService;

        // 0 = idle, 1 = an operation is running. Guards against double-starts.
        private int _operationRunning;

        public CompactArkService(
            ILogger<CompactArkService> logger,
            IArkPathProvider arkPathProvider,
            IProcessService processService)
        {
            _logger = logger;
            _arkPathProvider = arkPathProvider;
            _processService = processService;
        }

        /// <inheritdoc/>
        public bool IsArkRunning() => _processService.IsProcessRunning(GameProcessName);

        /// <inheritdoc/>
        public Task<CompactArkAnalysis> AnalyzeAsync(
            IProgress<CompactArkAnalyzeProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => AnalyzeCore(progress, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public Task<CompactArkOperationResult> CompressAsync(
            IProgress<CompactArkOperationProgress> progress,
            CancellationToken cancellationToken = default)
        {
            return RunOperationAsync(compress: true, progress, cancellationToken);
        }

        /// <inheritdoc/>
        public Task<CompactArkOperationResult> UncompressAsync(
            IProgress<CompactArkOperationProgress> progress,
            CancellationToken cancellationToken = default)
        {
            return RunOperationAsync(compress: false, progress, cancellationToken);
        }

        // ---------------------------------------------------------------- analyze

        private CompactArkAnalysis AnalyzeCore(
            IProgress<CompactArkAnalyzeProgress>? progress,
            CancellationToken cancellationToken)
        {
            var arkPath = _arkPathProvider.FindArkPath();
            bool installFound = arkPath != null && _arkPathProvider.IsValidArkPath(arkPath);
            bool gameRunning = IsArkRunning();

            if (!installFound || arkPath == null)
            {
                _logger.LogWarning("Compact ARK analysis: no valid ARK install found (candidate: {Path})", arkPath);
                return new CompactArkAnalysis(
                    arkPath, false, false, null, gameRunning,
                    0, 0, 0, 0, CompactArkState.Unknown);
            }

            var (isNtfs, driveFormat) = GetDriveFormat(arkPath);

            _logger.LogInformation(
                "Compact ARK analysis starting for {Path} (NTFS: {IsNtfs}, game running: {GameRunning})",
                arkPath, isNtfs, gameRunning);

            var measurement = MeasureDirectory(arkPath, progress, cancellationToken);
            var state = ClassifyState(measurement);

            _logger.LogInformation(
                "Compact ARK analysis done: {Files} files, logical {Logical} bytes, on disk {OnDisk} bytes, state {State}",
                measurement.TotalFiles, measurement.LogicalBytes, measurement.OnDiskBytes, state);

            return new CompactArkAnalysis(
                arkPath,
                true,
                isNtfs,
                driveFormat,
                gameRunning,
                measurement.LogicalBytes,
                measurement.OnDiskBytes,
                measurement.TotalFiles,
                measurement.CompressedFiles,
                state);
        }

        private (bool IsNtfs, string? DriveFormat) GetDriveFormat(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root))
                {
                    return (false, null);
                }

                var drive = new DriveInfo(root);
                var format = drive.DriveFormat;
                return (string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase), format);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read drive format for {Path}", path);
                return (false, null);
            }
        }

        private DirectoryMeasurement MeasureDirectory(
            string directory,
            IProgress<CompactArkAnalyzeProgress>? progress,
            CancellationToken cancellationToken)
        {
            long logicalBytes = 0;
            long onDiskBytes = 0;
            int totalFiles = 0;
            int compressedFiles = 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // Default skips Hidden|System which would under-count; only skip reparse points.
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var throttle = Stopwatch.StartNew();

            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // File vanished or is locked in a way that hides metadata — skip it.
                    continue;
                }

                long sizeOnDisk = GetSizeOnDisk(file, length);

                logicalBytes += length;
                onDiskBytes += sizeOnDisk;
                totalFiles++;
                if (sizeOnDisk < length)
                {
                    compressedFiles++;
                }

                if (throttle.ElapsedMilliseconds >= 150)
                {
                    progress?.Report(new CompactArkAnalyzeProgress(totalFiles, logicalBytes, onDiskBytes));
                    throttle.Restart();
                }
            }

            progress?.Report(new CompactArkAnalyzeProgress(totalFiles, logicalBytes, onDiskBytes));
            return new DirectoryMeasurement(logicalBytes, onDiskBytes, totalFiles, compressedFiles);
        }

        private static long GetSizeOnDisk(string path, long fallback)
        {
            // \\?\ prefix keeps long paths working through the Win32 call.
            var extendedPath = path.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? path
                : @"\\?\" + path;

            uint low = GetCompressedFileSizeW(extendedPath, out uint high);
            if (low == InvalidFileSize && Marshal.GetLastWin32Error() != 0)
            {
                return fallback;
            }

            return ((long)high << 32) | low;
        }

        private static CompactArkState ClassifyState(DirectoryMeasurement m)
        {
            if (m.TotalFiles == 0 || m.LogicalBytes == 0)
            {
                return CompactArkState.Unknown;
            }

            double savings = 1.0 - ((double)m.OnDiskBytes / m.LogicalBytes);
            double compressedShare = (double)m.CompressedFiles / m.TotalFiles;

            // Tiny savings happen naturally (cluster rounding); require a real signal.
            if (savings < 0.02 || compressedShare < 0.05)
            {
                return CompactArkState.NotCompacted;
            }

            return compressedShare >= 0.75
                ? CompactArkState.Compacted
                : CompactArkState.PartiallyCompacted;
        }

        // ---------------------------------------------------------------- compact.exe

        private async Task<CompactArkOperationResult> RunOperationAsync(
            bool compress,
            IProgress<CompactArkOperationProgress> progress,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _operationRunning, 1, 0) != 0)
            {
                return Fail("Another compact operation is already running.");
            }

            try
            {
                return await Task.Run(
                    () => RunOperationCore(compress, progress, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _operationRunning, 0);
            }
        }

        private CompactArkOperationResult RunOperationCore(
            bool compress,
            IProgress<CompactArkOperationProgress> progress,
            CancellationToken cancellationToken)
        {
            var operationName = compress ? "compress" : "uncompress";

            try
            {
                var arkPath = _arkPathProvider.FindArkPath();
                if (arkPath == null || !_arkPathProvider.IsValidArkPath(arkPath))
                {
                    return Fail("ARK installation not found.");
                }

                var (isNtfs, driveFormat) = GetDriveFormat(arkPath);
                if (!isNtfs)
                {
                    return Fail($"The ARK drive is not NTFS ({driveFormat ?? "unknown format"}). NTFS compression is unavailable.");
                }

                if (IsArkRunning())
                {
                    return Fail("ARK is currently running. Close the game before changing compression.");
                }

                var compactPath = Path.Combine(Environment.SystemDirectory, "compact.exe");
                if (!File.Exists(compactPath))
                {
                    return Fail("compact.exe was not found in the Windows system directory.");
                }

                // Pre-measure for before-size and the progress denominator.
                var before = MeasureDirectory(arkPath, null, cancellationToken);
                if (before.TotalFiles == 0)
                {
                    return Fail("No files found in the ARK install directory.");
                }

                var arguments = compress
                    ? $"/C /S:\"{arkPath}\" /I /EXE:LZX"
                    : $"/U /S:\"{arkPath}\" /I /EXE";

                _logger.LogInformation(
                    "Starting compact.exe {Operation} for {Path} ({Files} files): compact.exe {Args}",
                    operationName, arkPath, before.TotalFiles, arguments);

                int filesProcessed = 0;
                var stderrTail = new StringBuilder();
                var throttle = Stopwatch.StartNew();
                int totalFiles = before.TotalFiles;

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = compactPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = arkPath
                    }
                };

                process.OutputDataReceived += (_, e) =>
                {
                    var current = TryParseFileLine(e.Data, compress);
                    if (current == null)
                    {
                        return;
                    }

                    int done = Interlocked.Increment(ref filesProcessed);
                    if (throttle.ElapsedMilliseconds >= 100 || done >= totalFiles)
                    {
                        progress.Report(new CompactArkOperationProgress(done, totalFiles, current));
                        throttle.Restart();
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                    {
                        return;
                    }

                    lock (stderrTail)
                    {
                        stderrTail.AppendLine(e.Data.Trim());
                        if (stderrTail.Length > 2000)
                        {
                            stderrTail.Remove(0, stderrTail.Length - 2000);
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            _logger.LogInformation("Cancelling compact.exe {Operation} — killing process tree", operationName);
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to kill compact.exe on cancellation");
                    }
                });

                // No timeout: LZX over a large install legitimately takes a long while.
                process.WaitForExit();

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "compact.exe {Operation} cancelled after {Files} files", operationName, filesProcessed);
                    return new CompactArkOperationResult(
                        Success: false,
                        Cancelled: true,
                        FilesProcessed: filesProcessed,
                        LogicalBytes: before.LogicalBytes,
                        OnDiskBytesBefore: before.OnDiskBytes,
                        OnDiskBytesAfter: before.OnDiskBytes,
                        ErrorMessage: null);
                }

                if (process.ExitCode != 0)
                {
                    string tail;
                    lock (stderrTail)
                    {
                        tail = stderrTail.ToString().Trim();
                    }

                    _logger.LogError(
                        "compact.exe {Operation} failed with exit code {ExitCode}. Stderr tail: {Stderr}",
                        operationName, process.ExitCode, tail);

                    var detail = string.IsNullOrEmpty(tail) ? string.Empty : $" ({tail})";
                    return Fail($"compact.exe exited with code {process.ExitCode}.{detail}");
                }

                // Post-measure for the after-size.
                var after = MeasureDirectory(arkPath, null, CancellationToken.None);

                _logger.LogInformation(
                    "compact.exe {Operation} finished: {Files} files, on disk {Before} -> {After} bytes",
                    operationName, filesProcessed, before.OnDiskBytes, after.OnDiskBytes);

                return new CompactArkOperationResult(
                    Success: true,
                    Cancelled: false,
                    FilesProcessed: filesProcessed,
                    LogicalBytes: after.LogicalBytes,
                    OnDiskBytesBefore: before.OnDiskBytes,
                    OnDiskBytesAfter: after.OnDiskBytes,
                    ErrorMessage: null);
            }
            catch (OperationCanceledException)
            {
                return new CompactArkOperationResult(
                    Success: false,
                    Cancelled: true,
                    FilesProcessed: 0,
                    LogicalBytes: 0,
                    OnDiskBytesBefore: 0,
                    OnDiskBytesAfter: 0,
                    ErrorMessage: null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during compact.exe {Operation}", operationName);
                return Fail($"Unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the file name from a per-file "[OK]" output line, or null when the line
        /// is a header, summary, or blank line that should not count toward progress.
        /// </summary>
        private static string? TryParseFileLine(string? line, bool compress)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var trimmed = line.Trim();
            if (!trimmed.EndsWith("[OK]", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (compress)
            {
                var match = CompressOkLineRegex.Match(trimmed);
                if (match.Success)
                {
                    return match.Groups["name"].Value.Trim();
                }
            }

            var simple = SimpleOkLineRegex.Match(trimmed);
            return simple.Success ? simple.Groups["name"].Value.Trim() : trimmed;
        }

        private static CompactArkOperationResult Fail(string message)
        {
            return new CompactArkOperationResult(
                Success: false,
                Cancelled: false,
                FilesProcessed: 0,
                LogicalBytes: 0,
                OnDiskBytesBefore: 0,
                OnDiskBytesAfter: 0,
                ErrorMessage: message);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        private sealed record DirectoryMeasurement(
            long LogicalBytes,
            long OnDiskBytes,
            int TotalFiles,
            int CompressedFiles);
    }
}
