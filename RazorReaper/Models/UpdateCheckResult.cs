namespace RazorReaper.Models;

public sealed class UpdateCheckResult
{
    public Version CurrentVersion { get; init; } = new Version(0, 0, 0, 0);
    public Version? LatestVersion { get; init; }
    public bool HasUpdate { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ChangelogUrl { get; init; }
    public string? InstallerArgs { get; init; }
    public bool IsMandatory { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Human-readable release notes from the update manifest's <c>&lt;notes&gt;</c> element.
    /// One bullet per non-empty line; leading dashes/asterisks are stripped at parse time so
    /// the markup can be authored either as a markdown list or as plain lines.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}
