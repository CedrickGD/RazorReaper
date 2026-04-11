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

    public async Task<int> BackupFilesAsync(string categoryKey, Dictionary<string, string[]> folderFiles)
    {
        return await Task.Run(() =>
        {
            int count = 0;
            string categoryBackupDir = Path.Combine(_backupRoot, categoryKey);

            foreach (var folder in folderFiles)
            {
                string folderPath = folder.Key;
                if (!Directory.Exists(folderPath))
                    continue;

                string backupSubDir = Path.Combine(categoryBackupDir, MakeRelativeBackupPath(folderPath));
                Directory.CreateDirectory(backupSubDir);

                foreach (string fileName in folder.Value)
                {
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
            }

            _logger.LogInformation("Backed up {Count} files for category '{Category}'", count, categoryKey);
            return count;
        });
    }

    public async Task<int> RestoreFilesAsync(string categoryKey, Dictionary<string, string[]> folderFiles)
    {
        return await Task.Run(() =>
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
                string folderPath = folder.Key;
                string backupSubDir = Path.Combine(categoryBackupDir, MakeRelativeBackupPath(folderPath));

                if (!Directory.Exists(backupSubDir))
                    continue;

                Directory.CreateDirectory(folderPath);

                foreach (string fileName in folder.Value)
                {
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
        });
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
