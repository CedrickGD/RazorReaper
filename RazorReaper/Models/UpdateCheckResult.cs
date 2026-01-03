namespace RazorReaper.Models;

public sealed class UpdateCheckResult
{
    public Version CurrentVersion { get; init; } = new Version(0, 0, 0, 0);
    public Version? LatestVersion { get; init; }
    public bool HasUpdate { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ChangelogUrl { get; init; }
    public bool IsMandatory { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}
