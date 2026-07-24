using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Services;

namespace RazorReaper.Services.FileModifier;

/// <summary>What was done to a modified game file, so a restore knows how to undo it.</summary>
public enum FileModAction
{
    Removed,
    Replaced
}

/// <summary>A game file RazorReaper has modified, with the pristine copy kept for restore.</summary>
public sealed record FileModEntry(
    string Id,
    string RelativePath,
    FileModAction Action,
    long OriginalSize,
    DateTime TimestampUtc);

/// <summary>Outcome of a file-modifier operation.</summary>
public sealed record FileModResult(bool Success, string Message);

/// <summary>One deletable chunk of ARK's SeekFreeContent, with its on-disk size.</summary>
public sealed record SeekFreeItem(
    string Id,
    string Label,
    string Category,
    long SizeBytes,
    bool IsShaderModel4,
    string? FolderPath);

/// <summary>Scan of ARK's SeekFreeContent: total size plus the individually deletable items.</summary>
public sealed record SeekFreeReport(bool Exists, long TotalBytes, IReadOnlyList<SeekFreeItem> Items);

/// <summary>Outcome of a SeekFree cleanup pass.</summary>
public sealed record SeekFreeResult(bool Success, string Message, long FreedBytes);

/// <summary>
/// Two related tools for advanced users, both operating on the ARK install:
/// (1) a generic file modifier that removes or replaces a single game file with a once-only backup
/// so it can always be restored, and (2) a SeekFree cleanup that deletes redundant cooked data
/// (~hundreds of GB) to reclaim disk and force ARK to load loose (modified) files.
/// SeekFree deletion is not backed up — its "restore" is Steam's Verify Integrity.
/// </summary>
public interface IFileModifierService
{
    /// <summary>True when the app is running elevated (some Steam library deletes need it).</summary>
    bool IsAdministrator { get; }

    /// <summary>Raised when the modified-file list changes.</summary>
    event Action? Changed;

    /// <summary>The resolved ARK install root, or null when the game wasn't found.</summary>
    string? GetArkPath();

    /// <summary>Files RazorReaper has removed/replaced and can restore.</summary>
    IReadOnlyList<FileModEntry> GetModifiedFiles();

    /// <summary>Back up (once) then delete a file inside the ARK install.</summary>
    Task<FileModResult> RemoveAsync(string absolutePath, CancellationToken cancellationToken = default);

    /// <summary>Back up (once) then overwrite a game file with <paramref name="sourceFilePath"/>.</summary>
    Task<FileModResult> ReplaceAsync(string targetAbsolutePath, string sourceFilePath, CancellationToken cancellationToken = default);

    /// <summary>Restore one modified file from its backup.</summary>
    Task<FileModResult> RestoreAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Restore every modified file; returns count restored and count failed.</summary>
    Task<(int Restored, int Failed)> RestoreAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Scan ARK's SeekFreeContent for deletable items and their sizes.</summary>
    Task<SeekFreeReport> ScanSeekFreeAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete the selected SeekFree items; returns freed bytes.</summary>
    Task<SeekFreeResult> DeleteSeekFreeAsync(IReadOnlyList<SeekFreeItem> items, CancellationToken cancellationToken = default);

    /// <summary>Open Steam's "verify integrity" for ARK — the way to undo a SeekFree cleanup.</summary>
    void OpenSteamVerify();
}

public sealed class FileModifierService : IFileModifierService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILogger<FileModifierService> _logger;
    private readonly IArkPathProvider _arkPathProvider;
    private readonly IProcessService _process;
    private readonly INotificationService _notifications;
    private readonly IActivityService _activity;

    private readonly string _backupRoot;
    private readonly string _filesDir;
    private readonly string _manifestPath;
    private readonly object _gate = new();
    private List<FileModEntry> _entries;

    public FileModifierService(
        ILogger<FileModifierService> logger,
        IArkPathProvider arkPathProvider,
        IProcessService process,
        INotificationService notifications,
        IActivityService activity)
    {
        _logger = logger;
        _arkPathProvider = arkPathProvider;
        _process = process;
        _notifications = notifications;
        _activity = activity;

        _backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "FileModBackups");
        // Backups live in their own subfolder so a backup file can never collide with
        // manifest.json, and each is named by a hash of its relative path (collision-free).
        _filesDir = Path.Combine(_backupRoot, "files");
        _manifestPath = Path.Combine(_backupRoot, "manifest.json");
        _entries = LoadManifest();
    }

    public event Action? Changed;

    public bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public string? GetArkPath()
    {
        try
        {
            var path = _arkPathProvider.FindArkPath();
            return path is not null && _arkPathProvider.IsValidArkPath(path) ? path : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve ARK path");
            return null;
        }
    }

    public IReadOnlyList<FileModEntry> GetModifiedFiles()
    {
        lock (_gate) return _entries.ToList();
    }

    // ─── File modifier ──────────────────────────────────────────────────────────────────────

    public Task<FileModResult> RemoveAsync(string absolutePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                var (arkPath, relative, error) = ValidateTarget(absolutePath);
                if (arkPath is null) return new FileModResult(false, error!);

                if (!File.Exists(absolutePath))
                    return new FileModResult(false, "That file no longer exists.");

                if (EntryForRelative(relative!) is not null)
                    return new FileModResult(false, "That file is already modified — restore it first.");

                var size = new FileInfo(absolutePath).Length;
                var backupPath = BackupPathFor(relative!);

                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_filesDir);
                // Once-only backup keyed off the file, not the manifest: never overwrite an
                // existing pristine backup (e.g. if the manifest was lost but the backup survived).
                if (!File.Exists(backupPath)) File.Copy(absolutePath, backupPath, overwrite: false);
                File.Delete(absolutePath);

                AddEntry(new FileModEntry(Guid.NewGuid().ToString("N"), relative!, FileModAction.Removed, size, DateTime.UtcNow));
                _logger.LogInformation("File modifier removed {Relative}", relative);
                TryActivity($"Removed game file {Path.GetFileName(relative!)}", "warning");
                return new FileModResult(true, $"Removed {Path.GetFileName(relative!)} — backed up and restorable.");
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException)
            {
                return new FileModResult(false, "Access denied — try running RazorReaper as Administrator.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File modifier remove failed for {Path}", absolutePath);
                return new FileModResult(false, $"Could not remove the file: {ex.Message}");
            }
        }, cancellationToken);

    public Task<FileModResult> ReplaceAsync(string targetAbsolutePath, string sourceFilePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                var (arkPath, relative, error) = ValidateTarget(targetAbsolutePath);
                if (arkPath is null) return new FileModResult(false, error!);

                if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                    return new FileModResult(false, "The replacement file no longer exists.");

                if (string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(targetAbsolutePath), StringComparison.OrdinalIgnoreCase))
                    return new FileModResult(false, "The replacement is the same file as the target.");

                var targetExists = File.Exists(targetAbsolutePath);
                if (!targetExists && EntryForRelative(relative!) is null)
                    return new FileModResult(false, "The target game file wasn't found, so there is nothing to replace.");

                var existing = EntryForRelative(relative!);
                var backupPath = BackupPathFor(relative!);

                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_filesDir);

                // Back up the pristine original exactly once; a second replace keeps the first backup.
                long originalSize = existing?.OriginalSize ?? 0;
                if (existing is null)
                {
                    originalSize = targetExists ? new FileInfo(targetAbsolutePath).Length : 0;
                    // Once-only backup keyed off the file: never clobber a pristine backup that
                    // already exists on disk (manifest lost but backup survived).
                    if (targetExists && !File.Exists(backupPath)) File.Copy(targetAbsolutePath, backupPath, overwrite: false);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolutePath)!);
                File.Copy(sourceFilePath, targetAbsolutePath, overwrite: true);

                if (existing is null)
                {
                    AddEntry(new FileModEntry(Guid.NewGuid().ToString("N"), relative!, FileModAction.Replaced, originalSize, DateTime.UtcNow));
                }
                _logger.LogInformation("File modifier replaced {Relative}", relative);
                TryActivity($"Replaced game file {Path.GetFileName(relative!)}", "warning");
                return new FileModResult(true, $"Replaced {Path.GetFileName(relative!)} — original backed up and restorable.");
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException)
            {
                return new FileModResult(false, "Access denied — try running RazorReaper as Administrator.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File modifier replace failed for {Path}", targetAbsolutePath);
                return new FileModResult(false, $"Could not replace the file: {ex.Message}");
            }
        }, cancellationToken);

    public Task<FileModResult> RestoreAsync(string id, CancellationToken cancellationToken = default)
        => Task.Run(() => RestoreCore(id), cancellationToken);

    public Task<(int Restored, int Failed)> RestoreAllAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var restored = 0;
            var failed = 0;
            foreach (var entry in GetModifiedFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (RestoreCore(entry.Id).Success) restored++;
                else failed++;
            }
            return (restored, failed);
        }, cancellationToken);

    private FileModResult RestoreCore(string id)
    {
        try
        {
            var entry = GetModifiedFiles().FirstOrDefault(e => e.Id == id);
            if (entry is null) return new FileModResult(false, "That modification is no longer tracked.");

            var arkPath = GetArkPath();
            if (arkPath is null) return new FileModResult(false, "ARK installation not found.");

            var targetPath = Path.Combine(arkPath, entry.RelativePath);
            var backupPath = BackupPathFor(entry.RelativePath);

            if (entry.Action == FileModAction.Removed && !File.Exists(backupPath))
            {
                // A removed file with no backup can't be recovered by us.
                return new FileModResult(false, $"No backup found for {Path.GetFileName(entry.RelativePath)} — use Steam's Verify Integrity.");
            }

            if (File.Exists(backupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(backupPath, targetPath, overwrite: true);
                File.Delete(backupPath);
            }
            else
            {
                // Replaced a file that never existed originally → restore means remove our copy.
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }

            RemoveEntry(entry.Id);
            _logger.LogInformation("File modifier restored {Relative}", entry.RelativePath);
            return new FileModResult(true, $"Restored {Path.GetFileName(entry.RelativePath)}.");
        }
        catch (UnauthorizedAccessException)
        {
            return new FileModResult(false, "Access denied — try running RazorReaper as Administrator.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File modifier restore failed for {Id}", id);
            return new FileModResult(false, $"Could not restore the file: {ex.Message}");
        }
    }

    /// <summary>Validate a target is a real file path inside the ARK install; returns (arkPath, relative, error).</summary>
    private (string? ArkPath, string? Relative, string? Error) ValidateTarget(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return (null, null, "No file selected.");

        var arkPath = GetArkPath();
        if (arkPath is null)
            return (null, null, "ARK installation not found — is the game installed through Steam?");

        string full, arkFull;
        try
        {
            full = Path.GetFullPath(absolutePath);
            arkFull = Path.GetFullPath(arkPath);
        }
        catch
        {
            return (null, null, "That path could not be read.");
        }

        var prefix = arkFull.EndsWith(Path.DirectorySeparatorChar) ? arkFull : arkFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return (null, null, "For safety, only files inside the ARK install folder can be modified.");

        var relative = full.Substring(prefix.Length);
        return (arkPath, relative, null);
    }

    // ─── SeekFree cleanup ─────────────────────────────────────────────────────────────────────

    public Task<SeekFreeReport> ScanSeekFreeAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var arkPath = GetArkPath();
            if (arkPath is null) return new SeekFreeReport(false, 0, Array.Empty<SeekFreeItem>());

            var sfcRoot = Path.Combine(arkPath, "ShooterGame", "SeekFreeContent");
            if (!Directory.Exists(sfcRoot)) return new SeekFreeReport(false, 0, Array.Empty<SeekFreeItem>());

            var items = new List<SeekFreeItem>();
            long total = 0;

            try
            {
                // 1) Shader-Model-4 variants (*.SM4) — only used with the -sm4 launch option.
                long sm4Bytes = 0;
                foreach (var file in Directory.EnumerateFiles(sfcRoot, "*.SM4", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { sm4Bytes += new FileInfo(file).Length; } catch { }
                }
                if (sm4Bytes > 0)
                {
                    items.Add(new SeekFreeItem("sm4", "Shader Model 4 files (*.SM4)", "Redundant shader variants", sm4Bytes, true, null));
                }

                // 2) Per-map folders under Maps\ and Mods\ (delete maps you don't play).
                foreach (var group in new[] { "Maps", "Mods" })
                {
                    var groupDir = Path.Combine(sfcRoot, group);
                    if (!Directory.Exists(groupDir)) continue;
                    foreach (var folder in Directory.EnumerateDirectories(groupDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var size = DirectorySize(folder, cancellationToken);
                        var name = Path.GetFileName(folder);
                        items.Add(new SeekFreeItem($"map:{group}:{name}", name, group == "Maps" ? "Map data" : "Official map (mod)", size, false, folder));
                    }
                }

                // 3) Core blueprints — removing forces loose (modded) core data to load.
                var coreDir = Path.Combine(sfcRoot, "PrimalEarth", "CoreBlueprints");
                if (Directory.Exists(coreDir))
                {
                    var size = DirectorySize(coreDir, cancellationToken);
                    items.Add(new SeekFreeItem("core", "Core blueprints", "Core game data (advanced)", size, false, coreDir));
                }

                total = DirectorySize(sfcRoot, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SeekFree scan encountered an error");
            }

            items = items.OrderByDescending(i => i.SizeBytes).ToList();
            return new SeekFreeReport(true, total, items);
        }, cancellationToken);

    public Task<SeekFreeResult> DeleteSeekFreeAsync(IReadOnlyList<SeekFreeItem> items, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (items is null || items.Count == 0)
                return new SeekFreeResult(false, "Nothing selected.", 0);

            var arkPath = GetArkPath();
            if (arkPath is null) return new SeekFreeResult(false, "ARK installation not found.", 0);

            var sfcRoot = Path.Combine(arkPath, "ShooterGame", "SeekFreeContent");
            var sfcFull = Path.GetFullPath(sfcRoot);
            long freed = 0;
            var failures = 0;

            // Delete the SM4 set first so it isn't double-counted against folders being removed too.
            foreach (var item in items.Where(i => i.IsShaderModel4))
            {
                foreach (var file in SafeEnumerate(sfcRoot, "*.SM4"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var size = new FileInfo(file).Length;
                        File.Delete(file);
                        freed += size;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        _logger.LogWarning(ex, "Could not delete SM4 file {File}", file);
                    }
                }
            }

            foreach (var item in items.Where(i => !i.IsShaderModel4 && i.FolderPath is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Guard: only ever delete folders that live under SeekFreeContent.
                var folderFull = Path.GetFullPath(item.FolderPath!);
                if (!folderFull.StartsWith(sfcFull, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folderFull))
                {
                    failures++;
                    continue;
                }

                try
                {
                    var size = DirectorySize(folderFull, cancellationToken);
                    Directory.Delete(folderFull, recursive: true);
                    freed += size;
                }
                catch (Exception ex)
                {
                    failures++;
                    _logger.LogWarning(ex, "Could not delete SeekFree folder {Folder}", folderFull);
                }
            }

            if (freed > 0)
                TryActivity($"SeekFree cleanup freed {FormatBytes(freed)}", "success");

            var message = failures == 0
                ? $"Freed {FormatBytes(freed)}."
                : $"Freed {FormatBytes(freed)}, but {failures} item(s) could not be deleted (try running as Administrator).";
            return new SeekFreeResult(failures == 0, message, freed);
        }, cancellationToken);

    public void OpenSteamVerify()
    {
        try { _process.Start("steam://validate/346110"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open Steam verify");
            _notifications.ShowError("Could not open Steam — start it manually and verify the ARK files.");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────

    private static long DirectorySize(string path, CancellationToken cancellationToken)
    {
        long size = 0;
        foreach (var file in SafeEnumerate(path, "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { size += new FileInfo(file).Length; } catch { }
        }
        return size;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { return Array.Empty<string>(); }
    }

    private FileModEntry? EntryForRelative(string relative)
    {
        lock (_gate) return _entries.FirstOrDefault(e =>
            string.Equals(e.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Collision-free backup path for a relative game path: a SHA-256 hash of the
    /// case-normalized relative path (so two distinct files never share one backup) plus the
    /// original extension for readability, inside the dedicated files subfolder.
    /// </summary>
    private string BackupPathFor(string relativePath)
    {
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        var ext = Path.GetExtension(relativePath);
        return Path.Combine(_filesDir, hash + ext);
    }

    private void AddEntry(FileModEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            SaveManifestLocked();
        }
        RaiseChanged();
    }

    private void RemoveEntry(string id)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            SaveManifestLocked();
        }
        RaiseChanged();
    }

    private List<FileModEntry> LoadManifest()
    {
        try
        {
            if (File.Exists(_manifestPath))
            {
                var loaded = JsonSerializer.Deserialize<List<FileModEntry>>(File.ReadAllText(_manifestPath));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load file-modifier manifest — starting empty");
        }
        return new List<FileModEntry>();
    }

    private void SaveManifestLocked()
    {
        try
        {
            Directory.CreateDirectory(_backupRoot);
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(_entries, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save file-modifier manifest");
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "FileModifier Changed subscriber threw"); }
    }

    private void TryActivity(string title, string type)
    {
        try { _activity.AddActivity(title, type); }
        catch { /* best-effort */ }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.0} {units[unit]}";
    }
}
