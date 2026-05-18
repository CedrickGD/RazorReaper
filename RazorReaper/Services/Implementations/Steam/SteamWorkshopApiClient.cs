using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Calls Steam's public <c>GetPublishedFileDetails</c> endpoint to fill in title / preview /
/// size / timestamps for workshop mods we don't have full local ACF metadata for. Batches
/// requests (Steam accepts up to ~100 ids per call) and applies results in-place onto the
/// caller's <see cref="SteamWorkshopMod"/> instances.
/// </summary>
internal sealed class SteamWorkshopApiClient
{
    private const string PublishedFileDetailsEndpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const int MetadataBatchSize = 80;

    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SteamWorkshopApiClient(ILogger logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Fetch Steam-side metadata for any mod missing a title or full metadata flag.
    /// Mutates <paramref name="mods"/> in place; returns the number of workshop ids the API
    /// successfully resolved (used for the user-facing scan summary).</summary>
    public async Task<int> EnrichMissingMetadataAsync(
        List<SteamWorkshopMod> mods,
        CancellationToken cancellationToken)
    {
        var missingIds = mods
            .Where(mod => !mod.HasSteamMetadata || string.IsNullOrWhiteSpace(mod.Title))
            .Select(mod => mod.WorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missingIds.Count == 0)
        {
            return 0;
        }

        var modLookup = mods.ToDictionary(mod => mod.WorkshopId, StringComparer.Ordinal);
        var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        for (var offset = 0; offset < missingIds.Count; offset += MetadataBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = missingIds.Skip(offset).Take(MetadataBatchSize).ToList();
            var formValues = new List<KeyValuePair<string, string>>(batch.Count + 1)
            {
                new("itemcount", batch.Count.ToString(CultureInfo.InvariantCulture))
            };

            for (var index = 0; index < batch.Count; index++)
            {
                formValues.Add(new KeyValuePair<string, string>($"publishedfileids[{index}]", batch[index]));
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, PublishedFileDetailsEndpoint)
                {
                    Content = new FormUrlEncodedContent(formValues)
                };

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Steam metadata request returned {StatusCode}", response.StatusCode);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
                    !responseElement.TryGetProperty("publishedfiledetails", out var detailsElement) ||
                    detailsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var detail in detailsElement.EnumerateArray())
                {
                    ApplyApiDetail(detail, modLookup, resolvedIds);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch Steam metadata for workshop batch starting at {Offset}", offset);
            }
        }

        return resolvedIds.Count;
    }

    private static void ApplyApiDetail(
        JsonElement detail,
        Dictionary<string, SteamWorkshopMod> modLookup,
        HashSet<string> resolvedIds)
    {
        if (!detail.TryGetProperty("publishedfileid", out var idElement))
        {
            return;
        }

        var workshopId = idElement.GetString();
        if (string.IsNullOrWhiteSpace(workshopId) || !modLookup.TryGetValue(workshopId, out var mod))
        {
            return;
        }

        // Steam returns a "result" code per item; only 1 means "ok". Skip anything else so we
        // don't paint over local ACF data with a Steam "deleted/private" placeholder.
        if (detail.TryGetProperty("result", out var resultElement) &&
            resultElement.ValueKind == JsonValueKind.Number &&
            resultElement.TryGetInt32(out var resultCode) &&
            resultCode != 1)
        {
            return;
        }

        if (detail.TryGetProperty("title", out var titleElement))
        {
            var title = titleElement.GetString();
            if (!string.IsNullOrWhiteSpace(title))
            {
                mod.Title = title;
                mod.HasSteamMetadata = true;
                resolvedIds.Add(workshopId);
            }
        }

        if (detail.TryGetProperty("preview_url", out var previewElement))
        {
            var previewUrl = previewElement.GetString();
            if (!string.IsNullOrWhiteSpace(previewUrl))
            {
                mod.PreviewUrl = previewUrl;
            }
        }

        if (detail.TryGetProperty("time_updated", out var updatedElement) &&
            TryReadUnixTimestamp(updatedElement, out var steamUpdated))
        {
            mod.SteamLastUpdatedAtUtc = PickLatest(mod.SteamLastUpdatedAtUtc, steamUpdated);
        }

        if (detail.TryGetProperty("time_created", out var createdElement) &&
            TryReadUnixTimestamp(createdElement, out var steamCreated))
        {
            mod.InstalledAtUtc = PickLatest(mod.InstalledAtUtc, steamCreated);
        }

        if (detail.TryGetProperty("file_size", out var fileSizeElement) &&
            TryReadInt64(fileSizeElement, out var fileSize) &&
            fileSize > 0)
        {
            mod.SizeBytes = !mod.SizeBytes.HasValue || fileSize > mod.SizeBytes.Value
                ? fileSize
                : mod.SizeBytes;
        }
    }

    private static bool TryReadInt64(JsonElement element, out long value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static bool TryReadUnixTimestamp(JsonElement element, out DateTime timestampUtc)
    {
        timestampUtc = default;

        if (!TryReadInt64(element, out var rawValue) || rawValue <= 0)
        {
            return false;
        }

        try
        {
            timestampUtc = DateTimeOffset.FromUnixTimeSeconds(rawValue).UtcDateTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? PickLatest(DateTime? current, DateTime? candidate)
    {
        if (!candidate.HasValue)
        {
            return current;
        }

        if (!current.HasValue || candidate.Value > current.Value)
        {
            return candidate;
        }

        return current;
    }
}
