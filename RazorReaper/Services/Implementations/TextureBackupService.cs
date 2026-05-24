using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

public class TextureBackupService : ITextureBackupService
{
    private readonly ILogger<TextureBackupService> _logger;
    private readonly string _backupRoot;

    public TextureBackupService(ILogger<TextureBackupService> logger)
    {
        _logger = logger;
        _backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper",
            "TextureBackups");
    }

    public Task<int> BackupFilesAsync(string categoryKey, Dictionary<string, string[]> folderFiles, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            int count = 0;
            string categoryBackupDir = Path.Combine(_backupRoot, categoryKey);

            // Snapshot which folder entries had no subdirectories at the start of the operation.
            // Those are "leaf" folders (e.g. UI/Inventory/Textures/FullBgAnim) that the user wants
            // physically removed after their files get backed up — not just emptied. Parent folders
            // that contain subdirectories at startup (e.g. UI/Inventory/Textures itself, which holds
            // FullBgAnim and PanelBgAnim) must NOT be deleted, even when our backup later makes them
            // look empty after the subfolder cleanup. We compute this once, up front, to avoid
            // ordering dependencies between sibling entries.
            var removableLeafFolders = folderFiles.Keys
                .Where(p =>
                {
                    try
                    {
                        return Directory.Exists(p)
                            && !Directory.EnumerateDirectories(p).Any();
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folderFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string folderPath = folder.Key;
                if (!Directory.Exists(folderPath))
                    continue;

                string backupSubDir = Path.Combine(categoryBackupDir, MakeRelativeBackupPath(folderPath));
                Directory.CreateDirectory(backupSubDir);

                foreach (string fileName in folder.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(fileName))
                        continue;

                    string sourcePath = Path.Combine(folderPath, fileName);
                    string destPath = Path.Combine(backupSubDir, fileName);

                    if (!File.Exists(sourcePath))
                        continue;

                    try
                    {
                        File.Copy(sourcePath, destPath, overwrite: true);
                        File.Delete(sourcePath);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to backup file {FilePath}", sourcePath);
                    }
                }

                // Leaf-folder cleanup: if this entry was a leaf at startup and nothing
                // remains in it now, remove the directory itself. RestoreFilesAsync's
                // Directory.CreateDirectory call recreates it on revert.
                if (removableLeafFolders.Contains(folderPath))
                {
                    try
                    {
                        if (Directory.Exists(folderPath)
                            && !Directory.EnumerateFileSystemEntries(folderPath).Any())
                        {
                            Directory.Delete(folderPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove emptied folder {FolderPath}", folderPath);
                    }
                }
            }

            _logger.LogInformation("Backed up {Count} files for category '{Category}'", count, categoryKey);
            return count;
        }, cancellationToken);
    }

    public Task<int> RestoreFilesAsync(string categoryKey, Dictionary<string, string[]> folderFiles, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            int count = 0;
            string categoryBackupDir = Path.Combine(_backupRoot, categoryKey);

            if (!Directory.Exists(categoryBackupDir))
            {
                _logger.LogWarning("No backup found for category '{Category}'", categoryKey);
                return 0;
            }

            foreach (var folder in folderFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string folderPath = folder.Key;
                string backupSubDir = Path.Combine(categoryBackupDir, MakeRelativeBackupPath(folderPath));

                if (!Directory.Exists(backupSubDir))
                    continue;

                Directory.CreateDirectory(folderPath);

                foreach (string fileName in folder.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(fileName))
                        continue;

                    string backupPath = Path.Combine(backupSubDir, fileName);
                    string originalPath = Path.Combine(folderPath, fileName);

                    if (!File.Exists(backupPath))
                        continue;

                    try
                    {
                        File.Copy(backupPath, originalPath, overwrite: true);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to restore file {FilePath}", backupPath);
                    }
                }
            }

            // Clean up the backup folder for this category
            try
            {
                Directory.Delete(categoryBackupDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean up backup folder for '{Category}'", categoryKey);
            }

            _logger.LogInformation("Restored {Count} files for category '{Category}'", count, categoryKey);
            return count;
        }, cancellationToken);
    }

    public bool IsBackedUp(string categoryKey)
    {
        string categoryBackupDir = Path.Combine(_backupRoot, categoryKey);
        return Directory.Exists(categoryBackupDir)
            && Directory.EnumerateFiles(categoryBackupDir, "*", SearchOption.AllDirectories).Any();
    }

    public List<string> GetBackedUpCategories()
    {
        if (!Directory.Exists(_backupRoot))
            return new List<string>();

        return Directory.GetDirectories(_backupRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && IsBackedUp(name!))
            .Select(name => name!)
            .ToList();
    }

    public string GetBackupFolderPath() => _backupRoot;

    public string SanitizeCategoryKey(string displayName)
    {
        // Strip emoji and non-ASCII characters
        string sanitized = Regex.Replace(displayName, @"[^\x20-\x7E]", "");
        sanitized = sanitized.Trim();
        // Replace " - " and spaces with dashes
        sanitized = sanitized.Replace(" - ", "-");
        sanitized = sanitized.Replace(" ", "-");
        sanitized = sanitized.ToLowerInvariant();
        // Collapse multiple dashes
        sanitized = Regex.Replace(sanitized, @"-{2,}", "-");
        sanitized = sanitized.Trim('-');
        return sanitized;
    }

    private static string MakeRelativeBackupPath(string absolutePath)
    {
        // "C:\Games\ARK\..." -> "C\Games\ARK\..."
        return absolutePath.Replace(":", "");
    }
}
