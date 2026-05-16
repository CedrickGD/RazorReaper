using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Implementation of IFileSystemService for file system operations.
/// </summary>
public class FileSystemService : IFileSystemService
{
    private readonly ILogger<FileSystemService> _logger;

    public FileSystemService(ILogger<FileSystemService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> ReadAllTextAsync(string path)
    {
        try
        {
            _logger.LogDebug("Reading file: {Path}", path);
            return await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file: {Path}", path);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task WriteAllTextAsync(string path, string content)
    {
        try
        {
            _logger.LogDebug("Writing file: {Path}", path);
            await File.WriteAllTextAsync(path, content);
            _logger.LogInformation("File written successfully: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing file: {Path}", path);
            throw;
        }
    }

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if file exists: {Path}", path);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if directory exists: {Path}", path);
            return false;
        }
    }

    /// <inheritdoc/>
    public void OpenFile(string path)
    {
        try
        {
            _logger.LogInformation("Opening file in default application: {Path}", path);
            // Process.Start returns a Process handle even for shell-executed launches; dispose it
            // immediately so we don't leak the handle (we don't track lifetime here).
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening file: {Path}", path);
            throw;
        }
    }

    /// <inheritdoc/>
    public string? GetDirectoryName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting directory name from path: {Path}", path);
            return null;
        }
    }

    /// <inheritdoc/>
    public string Combine(params string[] paths)
    {
        try
        {
            return Path.Combine(paths);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error combining paths: {Paths}", string.Join(", ", paths));
            throw;
        }
    }
}
