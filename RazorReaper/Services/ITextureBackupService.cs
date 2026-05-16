namespace RazorReaper.Services;

public interface ITextureBackupService
{
    /// <summary>
    /// Backs up texture files to AppData, then deletes the originals.
    /// </summary>
    /// <returns>Number of files backed up and removed.</returns>
    Task<int> BackupFilesAsync(string categoryKey, Dictionary<string, string[]> folderFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores previously backed-up files to their original locations, then removes the backup.
    /// </summary>
    /// <returns>Number of files restored.</returns>
    Task<int> RestoreFilesAsync(string categoryKey, Dictionary<string, string[]> folderFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a backup exists for the given category.
    /// </summary>
    bool IsBackedUp(string categoryKey);

    /// <summary>
    /// Returns all category keys that currently have active backups.
    /// </summary>
    List<string> GetBackedUpCategories();

    /// <summary>
    /// Returns the root backup folder path.
    /// </summary>
    string GetBackupFolderPath();

    /// <summary>
    /// Converts a display name (with emoji, spaces) into a sanitized file-safe key.
    /// </summary>
    string SanitizeCategoryKey(string displayName);
}
