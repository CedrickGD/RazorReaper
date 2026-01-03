namespace RazorReaper.Models;

public class UpdateCheckResult
{
    public Version CurrentVersion { get; set; } = new Version(0, 0, 0, 0);
    public Version? LatestVersion { get; set; }
    public string? DownloadUrl { get; set; }
    public string? ChangelogUrl { get; set; }
    public bool IsMandatory { get; set; }
    public DateTime? CheckedAt { get; set; }
    public string? Error { get; set; }

    public bool IsUpdateAvailable =>
        LatestVersion != null && LatestVersion > CurrentVersion;

    public string DisplayCurrentVersion => CurrentVersion.ToString();
    public string DisplayLatestVersion => LatestVersion?.ToString() ?? "Unknown";
}
