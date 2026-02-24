using System.Reflection;
using System.Xml.Linq;
using RazorReaper.Models;

namespace RazorReaper.Services;

public class UpdateService : IUpdateService
{
    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/CedrickGD/RazorReaper/master/update.xml";
    private static readonly Version FallbackVersion = new Version(0, 0, 0, 0);
    private readonly HttpClient httpClient;

    public UpdateService(HttpClient httpClient)
    {
        this.httpClient = httpClient;
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
                return new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Update manifest is missing required data.",
                    CheckedAt = DateTimeOffset.UtcNow
                };
            }

            var versionText = item.Element("version")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(versionText) || !Version.TryParse(versionText, out var latestVersion))
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Update manifest contains an invalid version.",
                    CheckedAt = DateTimeOffset.UtcNow
                };
            }

            var downloadUrl = item.Element("url")?.Value?.Trim();
            var changelogUrl = item.Element("changelog")?.Value?.Trim();
            var mandatoryText = item.Element("mandatory")?.Value?.Trim();
            var isMandatory = bool.TryParse(mandatoryText, out var mandatory) && mandatory;

            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                HasUpdate = latestVersion > currentVersion,
                DownloadUrl = downloadUrl,
                ChangelogUrl = changelogUrl,
                IsMandatory = isMandatory,
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Update check timed out.",
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Could not reach the update server.",
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
        catch
        {
            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ErrorMessage = "Update check failed.",
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
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
