using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using System.Reflection;
using System.Xml.Linq;

namespace RazorReaper.Services.Implementations;

public class UpdateService : IUpdateService
{
    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/CedrickGD/RazorReaper/master/update.xml";
    private static readonly Version FallbackVersion = new Version(0, 0, 0, 0);
    private readonly HttpClient httpClient;
    private readonly ITelemetryService telemetryService;
    private readonly ILogger<UpdateService> logger;

    public UpdateService(HttpClient httpClient, ITelemetryService telemetryService, ILogger<UpdateService> logger)
    {
        this.httpClient = httpClient;
        this.telemetryService = telemetryService;
        this.logger = logger;
    }

    public Version CurrentVersion => GetAssemblyVersion();

    public string CurrentVersionLabel => FormatVersion(CurrentVersion);

    public static string GetCurrentVersion()
    {
        return FormatVersion(GetAssemblyVersion());
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = CurrentVersion;
        try
        {
            using var response = await httpClient.GetAsync(UpdateManifestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var xmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xmlContent);
            var item = document.Root;

            if (item == null)
            {
                var result = new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Update manifest is missing required data.",
                    CheckedAt = DateTimeOffset.UtcNow
                };
                TrackUpdateTelemetry(result, "invalid_manifest", TelemetryEventStatus.Degraded);
                return result;
            }

            var versionText = item.Element("version")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(versionText) || !Version.TryParse(versionText, out var latestVersion))
            {
                var result = new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Update manifest contains an invalid version.",
                    CheckedAt = DateTimeOffset.UtcNow
                };
                TrackUpdateTelemetry(result, "invalid_version", TelemetryEventStatus.Degraded);
                return result;
            }

            var downloadUrl = item.Element("url")?.Value?.Trim();
            var changelogUrl = item.Element("changelog")?.Value?.Trim();
            var installerArgs = item.Element("args")?.Value?.Trim();
            var mandatoryText = item.Element("mandatory")?.Value?.Trim();
            var isMandatory = bool.TryParse(mandatoryText, out var mandatory) && mandatory;
            var notes = ParseNotes(item.Element("notes")?.Value);

            var successResult = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                HasUpdate = latestVersion > currentVersion,
                DownloadUrl = downloadUrl,
                ChangelogUrl = changelogUrl,
                InstallerArgs = installerArgs,
                IsMandatory = isMandatory,
                Notes = notes,
                CheckedAt = DateTimeOffset.UtcNow
            };
            TrackUpdateTelemetry(
                successResult,
                successResult.HasUpdate ? "update_available" : "up_to_date",
                TelemetryEventStatus.Ok);
            return successResult;
        }
        catch (TaskCanceledException)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Update check timed out.",
                CheckedAt = DateTimeOffset.UtcNow
            };
            TrackUpdateTelemetry(result, "timeout", TelemetryEventStatus.Degraded);
            return result;
        }
        catch (HttpRequestException)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Could not reach the update server.",
                CheckedAt = DateTimeOffset.UtcNow
            };
            TrackUpdateTelemetry(result, "network_error", TelemetryEventStatus.Degraded);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed unexpectedly.");
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Update check failed.",
                CheckedAt = DateTimeOffset.UtcNow
            };
            TrackUpdateTelemetry(result, "failed", TelemetryEventStatus.Degraded);
            return result;
        }
    }

    private void TrackUpdateTelemetry(
        UpdateCheckResult result,
        string outcome,
        TelemetryEventStatus status)
    {
        var metrics = new Dictionary<string, object?>
        {
            ["outcome"] = outcome,
            ["current_version"] = FormatVersion(result.CurrentVersion),
            ["latest_version"] = result.LatestVersion is null ? null : FormatVersion(result.LatestVersion),
            ["has_update"] = result.HasUpdate,
            ["is_mandatory"] = result.IsMandatory,
            ["checked_at"] = result.CheckedAt.ToString("O")
        };

        _ = telemetryService.TrackEventAsync(
            "update_check",
            status,
            result.ErrorMessage ?? "Update check completed.",
            metrics);
    }

    /// <summary>
    /// Turns the &lt;notes&gt; element content into a flat list of bullet strings. Accepts either
    /// markdown-style lists (lines beginning with <c>-</c>, <c>*</c>, or <c>•</c>) or plain text
    /// — leading marker characters and surrounding whitespace are stripped per line. Empty
    /// lines are dropped so a CDATA block can be indented in the XML without bleeding into UI.
    /// </summary>
    private static IReadOnlyList<string> ParseNotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var bullets = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('-', '*', '•').Trim();
            if (trimmed.Length > 0)
            {
                bullets.Add(trimmed);
            }
        }
        return bullets;
    }

    private static Version GetAssemblyVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? FallbackVersion;
    }

    private static string FormatVersion(Version version)
    {
        if (version.Build >= 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        return $"{version.Major}.{version.Minor}";
    }
}
