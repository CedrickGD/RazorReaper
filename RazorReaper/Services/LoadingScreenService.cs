using Microsoft.Extensions.Logging;
using RazorReaper.Services;

namespace RazorReaper.Services
{
    /// <summary>Category of an ARK movie file, derived from its file name.</summary>
    public enum ArkMovieKind
    {
        Loading,
        Startup,
        Cinematic,
        Other
    }

    /// <summary>A single video file inside ARK's ShooterGame/Content/Movies folder.</summary>
    public record ArkMovieInfo(
        string FileName,
        string DisplayName,
        string Extension,
        ArkMovieKind Kind,
        long SizeBytes,
        bool IsReplaced,
        string FullPath);

    /// <summary>Outcome of a single replace/restore operation.</summary>
    public record MovieOperationResult(bool Success, string Message);

    /// <summary>Aggregate outcome of a restore-all pass.</summary>
    public record MovieRestoreSummary(int Restored, int Failed, IReadOnlyList<string> Errors);

    /// <summary>
    /// Replaces ARK's startup/loading videos with user-supplied files and restores the
    /// pristine originals from backup. Backups live under %LOCALAPPDATA%\RazorReaper\MovieBackups
    /// and are created exactly once per file, so the untouched original is always recoverable.
    /// Mirrors the backup/restore conventions of TextureBackupService.
    /// </summary>
    public interface ILoadingScreenService
    {
        /// <summary>Full path to ShooterGame/Content/Movies, or null when no valid ARK install was found.</summary>
        string? GetMoviesFolderPath();

        /// <summary>True when the Movies folder exists on disk.</summary>
        bool MoviesFolderExists();

        /// <summary>Enumerates the video files in the Movies folder, classified and sorted.</summary>
        List<ArkMovieInfo> ListMovies();

        /// <summary>Backs up the original (first time only), then copies the user's file over the game file.</summary>
        Task<MovieOperationResult> ReplaceAsync(string movieFileName, string userFilePath, CancellationToken cancellationToken = default);

        /// <summary>Copies the pristine backup back over the game file, then removes the backup.</summary>
        Task<MovieOperationResult> RestoreAsync(string movieFileName, CancellationToken cancellationToken = default);

        /// <summary>Restores every backed-up movie and reports per-file results.</summary>
        Task<MovieRestoreSummary> RestoreAllAsync(CancellationToken cancellationToken = default);

        /// <summary>Root folder that holds the pristine originals.</summary>
        string GetBackupFolderPath();
    }
}

namespace RazorReaper.Services.Implementations
{
    public class LoadingScreenService : ILoadingScreenService
    {
        // ARK ships every movie in two containers; the game picks by name + extension,
        // so replacements must keep the exact same container. No transcoding is done.
        private static readonly string[] SupportedExtensions = { ".mp4", ".wmv" };

        // Base names shipped by the game (verified against a real install — each exists
        // as an .mp4/.wmv pair in ShooterGame/Content/Movies).
        private static readonly Dictionary<string, (string DisplayName, ArkMovieKind Kind)> KnownMovies =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["LoadingScreen"] = ("Loading Screen", ArkMovieKind.Loading),
                ["ARKTitle"] = ("Title Screen", ArkMovieKind.Startup),
                ["ARKTitle_SE"] = ("Title Screen (Survival Evolved)", ArkMovieKind.Startup),
                ["TheIsland_in"] = ("The Island — Intro", ArkMovieKind.Cinematic),
                ["TheIsland_out"] = ("The Island — Outro", ArkMovieKind.Cinematic),
                ["ScorchedEarth_in"] = ("Scorched Earth — Intro", ArkMovieKind.Cinematic),
                ["ScorchedEarth_out"] = ("Scorched Earth — Outro", ArkMovieKind.Cinematic),
                ["Aberration_in"] = ("Aberration — Intro", ArkMovieKind.Cinematic),
                ["Aberration_out"] = ("Aberration — Outro", ArkMovieKind.Cinematic),
                ["Extinction_in"] = ("Extinction — Intro", ArkMovieKind.Cinematic),
            };

        private readonly ILogger<LoadingScreenService> _logger;
        private readonly IArkPathProvider _arkPathProvider;
        private readonly string _backupRoot;

        public LoadingScreenService(ILogger<LoadingScreenService> logger, IArkPathProvider arkPathProvider)
        {
            _logger = logger;
            _arkPathProvider = arkPathProvider;
            _backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RazorReaper",
                "MovieBackups");
        }

        public string? GetMoviesFolderPath()
        {
            try
            {
                var arkPath = _arkPathProvider.FindArkPath();
                if (arkPath is null || !_arkPathProvider.IsValidArkPath(arkPath))
                {
                    return null;
                }

                return Path.Combine(arkPath, "ShooterGame", "Content", "Movies");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve the ARK Movies folder");
                return null;
            }
        }

        public bool MoviesFolderExists()
        {
            var moviesDir = GetMoviesFolderPath();
            return moviesDir is not null && Directory.Exists(moviesDir);
        }

        public List<ArkMovieInfo> ListMovies()
        {
            var movies = new List<ArkMovieInfo>();

            try
            {
                var moviesDir = GetMoviesFolderPath();
                if (moviesDir is null || !Directory.Exists(moviesDir))
                {
                    return movies;
                }

                foreach (var filePath in Directory.EnumerateFiles(moviesDir))
                {
                    var extension = Path.GetExtension(filePath);
                    if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(filePath);
                    var baseName = Path.GetFileNameWithoutExtension(filePath);
                    var (displayName, kind) = KnownMovies.TryGetValue(baseName, out var known)
                        ? known
                        : (baseName, ArkMovieKind.Other);

                    long size = 0;
                    try
                    {
                        size = new FileInfo(filePath).Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read size of {FilePath}", filePath);
                    }

                    movies.Add(new ArkMovieInfo(
                        fileName,
                        displayName,
                        extension.ToLowerInvariant(),
                        kind,
                        size,
                        IsReplaced: File.Exists(GetBackupPath(fileName)),
                        FullPath: filePath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list ARK movie files");
            }

            return movies
                .OrderBy(m => m.Kind)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Extension, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Task<MovieOperationResult> ReplaceAsync(string movieFileName, string userFilePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                try
                {
                    var (moviesDir, dirError) = ResolveMoviesDir();
                    if (moviesDir is null)
                    {
                        return new MovieOperationResult(false, dirError!);
                    }

                    if (!IsSafeFileName(movieFileName))
                    {
                        return new MovieOperationResult(false, "Invalid movie file name.");
                    }

                    var targetExtension = Path.GetExtension(movieFileName).ToLowerInvariant();
                    if (!SupportedExtensions.Contains(targetExtension, StringComparer.OrdinalIgnoreCase))
                    {
                        return new MovieOperationResult(false, $"{movieFileName} is not a supported ARK movie file (.mp4 / .wmv).");
                    }

                    if (string.IsNullOrWhiteSpace(userFilePath) || !File.Exists(userFilePath))
                    {
                        return new MovieOperationResult(false, "The selected video file no longer exists.");
                    }

                    // Basic playability probe: correct container extension + non-empty file.
                    // ARK plays these by name, so the container must match — no transcoding here.
                    var userExtension = Path.GetExtension(userFilePath).ToLowerInvariant();
                    if (!string.Equals(userExtension, targetExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MovieOperationResult(false,
                            $"Wrong format: {movieFileName} needs a {targetExtension} file, but you picked a {(string.IsNullOrEmpty(userExtension) ? "file without an extension" : userExtension + " file")}. RazorReaper does not convert videos — pick a {targetExtension} video.");
                    }

                    long userSize;
                    try
                    {
                        userSize = new FileInfo(userFilePath).Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read size of {FilePath}", userFilePath);
                        return new MovieOperationResult(false, "The selected video file could not be read.");
                    }

                    if (userSize <= 0)
                    {
                        return new MovieOperationResult(false, "The selected video file is empty (0 bytes).");
                    }

                    var targetPath = Path.Combine(moviesDir, movieFileName);
                    if (string.Equals(Path.GetFullPath(userFilePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        return new MovieOperationResult(false, "That is the game's own video file — pick your replacement video instead.");
                    }

                    var backupPath = GetBackupPath(movieFileName);
                    var hasBackup = File.Exists(backupPath);

                    if (!hasBackup && !File.Exists(targetPath))
                    {
                        return new MovieOperationResult(false, $"{movieFileName} was not found in the Movies folder, so there is no original to replace.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Back up the pristine original exactly once. Replacing again later keeps
                    // the first backup, so the untouched file is never overwritten by a custom one.
                    if (!hasBackup)
                    {
                        Directory.CreateDirectory(_backupRoot);
                        File.Copy(targetPath, backupPath, overwrite: false);
                        _logger.LogInformation("Backed up original movie {FileName} to {BackupPath}", movieFileName, backupPath);
                    }

                    File.Copy(userFilePath, targetPath, overwrite: true);
                    _logger.LogInformation("Replaced movie {FileName} with {SourcePath}", movieFileName, userFilePath);

                    return new MovieOperationResult(true, $"{movieFileName} replaced — your video plays on the next game start.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to replace movie {FileName}", movieFileName);
                    return new MovieOperationResult(false, $"Replacing {movieFileName} failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public Task<MovieOperationResult> RestoreAsync(string movieFileName, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RestoreCore(movieFileName), cancellationToken);
        }

        public Task<MovieRestoreSummary> RestoreAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                var errors = new List<string>();
                var restored = 0;

                List<string> backupNames;
                try
                {
                    backupNames = Directory.Exists(_backupRoot)
                        ? Directory.EnumerateFiles(_backupRoot)
                            .Select(Path.GetFileName)
                            .Where(name => !string.IsNullOrEmpty(name))
                            .Select(name => name!)
                            .ToList()
                        : new List<string>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to enumerate movie backups");
                    return new MovieRestoreSummary(0, 1, new[] { $"Could not read the backup folder: {ex.Message}" });
                }

                foreach (var name in backupNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var result = RestoreCore(name);
                    if (result.Success)
                    {
                        restored++;
                    }
                    else
                    {
                        errors.Add(result.Message);
                    }
                }

                _logger.LogInformation("Movie restore-all finished: {Restored} restored, {Failed} failed", restored, errors.Count);
                return new MovieRestoreSummary(restored, errors.Count, errors);
            }, cancellationToken);
        }

        public string GetBackupFolderPath() => _backupRoot;

        private MovieOperationResult RestoreCore(string movieFileName)
        {
            try
            {
                var (moviesDir, dirError) = ResolveMoviesDir();
                if (moviesDir is null)
                {
                    return new MovieOperationResult(false, dirError!);
                }

                if (!IsSafeFileName(movieFileName))
                {
                    return new MovieOperationResult(false, "Invalid movie file name.");
                }

                var backupPath = GetBackupPath(movieFileName);
                if (!File.Exists(backupPath))
                {
                    return new MovieOperationResult(false, $"No backup found for {movieFileName} — it has not been replaced.");
                }

                var targetPath = Path.Combine(moviesDir, movieFileName);
                File.Copy(backupPath, targetPath, overwrite: true);

                // The pristine file is back in the game folder, so the backup copy is
                // no longer needed — removing it is what marks the movie as "original".
                File.Delete(backupPath);
                _logger.LogInformation("Restored original movie {FileName}", movieFileName);

                return new MovieOperationResult(true, $"{movieFileName} restored to the original.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore movie {FileName}", movieFileName);
                return new MovieOperationResult(false, $"Restoring {movieFileName} failed: {ex.Message}");
            }
        }

        private (string? MoviesDir, string? Error) ResolveMoviesDir()
        {
            var moviesDir = GetMoviesFolderPath();
            if (moviesDir is null)
            {
                return (null, "ARK installation not found — is the game installed through Steam?");
            }

            if (!Directory.Exists(moviesDir))
            {
                return (null, $"ARK's Movies folder is missing: {moviesDir}");
            }

            return (moviesDir, null);
        }

        private string GetBackupPath(string movieFileName) => Path.Combine(_backupRoot, movieFileName);

        private static bool IsSafeFileName(string fileName) =>
            !string.IsNullOrWhiteSpace(fileName)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !fileName.Contains("..", StringComparison.Ordinal);
    }
}
