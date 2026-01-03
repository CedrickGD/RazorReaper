using System.Net.Http;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services
{
    public static class UpdateService
    {
        private const string UpdateManifestUrl = "https://raw.githubusercontent.com/CedrickGD/RazorReaper/master/update.xml";

        public static string GetCurrentVersion()
        {
            return GetCurrentVersionValue().ToString();
        }

        public static Version GetCurrentVersionValue()
        {
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        }

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync(HttpClient httpClient, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(httpClient);

            var result = new UpdateCheckResult
            {
                CurrentVersion = GetCurrentVersionValue(),
                CheckedAt = DateTime.Now
            };

            try
            {
                var manifestContent = await httpClient.GetStringAsync(UpdateManifestUrl, cancellationToken);
                var document = XDocument.Parse(manifestContent);
                var root = document.Root;

                if (root == null)
                {
                    result.Error = "Update manifest is empty.";
                    return result;
                }

                var versionString = root.Element("version")?.Value?.Trim();
                if (Version.TryParse(versionString, out var latestVersion))
                {
                    result.LatestVersion = latestVersion;
                }
                else
                {
                    result.Error = "Couldn't read the latest version number.";
                    return result;
                }

                result.DownloadUrl = root.Element("url")?.Value?.Trim();
                result.ChangelogUrl = root.Element("changelog")?.Value?.Trim();
                result.IsMandatory = bool.TryParse(root.Element("mandatory")?.Value, out var mandatory) && mandatory;
                result.CheckedAt = DateTime.Now;

                logger?.LogInformation("Update manifest pulled. Current={Current} Latest={Latest} Mandatory={Mandatory}",
                    result.CurrentVersion, result.LatestVersion, result.IsMandatory);
            }
            catch (Exception ex)
            {
                result.Error = "Unable to check for updates right now.";
                logger?.LogWarning(ex, "Failed to retrieve update manifest from {ManifestUrl}", UpdateManifestUrl);
            }

            return result;
        }
    }
}
