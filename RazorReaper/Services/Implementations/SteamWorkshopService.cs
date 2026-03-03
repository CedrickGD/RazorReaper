using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RazorReaper.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Scans local Steam libraries and Steam metadata for installed ARK workshop mods.
/// </summary>
public sealed class SteamWorkshopService : ISteamWorkshopService
{
    private const string ArkAppId = "346110";
    private const string PublishedFileDetailsEndpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const int MetadataBatchSize = 80;

    private static readonly Regex LibraryPathRegex = new(@"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LegacyLibraryPathRegex = new(@"^\s*""\d+""\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex QuotedTokenRegex = new(@"""((?:\\.|[^""])*)""", RegexOptions.Compiled);

    private readonly ILogger<SteamWorkshopService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SteamWorkshopService(
        ILogger<SteamWorkshopService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public async Task<SteamWorkshopScanResult> ScanInstalledArkModsAsync(CancellationToken cancellationToken = default)
    {
        var result = new SteamWorkshopScanResult();

        try
        {
            var steamPath = GetSteamInstallPath();
            result.SteamPath = steamPath;

            if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            {
                result.Warnings.Add("Steam install path was not found in Windows registry.");
                return result;
            }

            result.SteamDetected = true;
            var libraries = await GetLibraryPathsAsync(steamPath, result.Warnings, cancellationToken);
            result.LibrariesScanned.AddRange(libraries);

            var modsById = new Dictionary<string, SteamWorkshopMod>(StringComparer.Ordinal);

            foreach (var libraryPath in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var acfMetadata = await ReadAppWorkshopMetadataAsync(libraryPath, result.Warnings, cancellationToken);
                var workshopPath = Path.Combine(libraryPath, "steamapps", "workshop", "content", ArkAppId);
                if (!Directory.Exists(workshopPath))
                {
                    continue;
                }

                IEnumerable<string> modDirectories;
                try
                {
                    modDirectories = Directory.EnumerateDirectories(workshopPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not enumerate workshop content directory: {WorkshopPath}", workshopPath);
                    result.Warnings.Add($"Could not read workshop folder: {workshopPath}");
                    continue;
                }

                foreach (var modDirectory in modDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var workshopId = Path.GetFileName(modDirectory);
                    if (!IsNumeric(workshopId))
                    {
                        continue;
                    }

                    if (!modsById.TryGetValue(workshopId, out var mod))
                    {
                        mod = new SteamWorkshopMod
                        {
                            WorkshopId = workshopId
                        };
                        modsById[workshopId] = mod;
                    }

                    ApplyLocalMetadata(mod, libraryPath, modDirectory);

                    if (acfMetadata.TryGetValue(workshopId, out var itemMetadata))
                    {
                        ApplyAcfMetadata(mod, itemMetadata);
                    }
                }
            }

            var mods = modsById.Values.ToList();
            result.MetadataResolvedCount = await EnrichSteamMetadataAsync(mods, cancellationToken);

            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.Title))
                {
                    mod.Title = $"Workshop Item {mod.WorkshopId}";
                }
            }

            result.Mods = mods
                .OrderByDescending(m => m.EffectiveLastUpdatedUtc ?? DateTime.MinValue)
                .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while scanning installed ARK workshop mods.");
            result.Warnings.Add("Unexpected error while scanning Steam Workshop mods. Check logs for details.");
        }
        finally
        {
            result.ScannedAtUtc = DateTime.UtcNow;
        }

        return result;
    }

    private static string? GetSteamInstallPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var steamPath =
                ReadRegistryString(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") ??
                ReadRegistryString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath") ??
                ReadRegistryString(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath");

            return string.IsNullOrWhiteSpace(steamPath) ? null : NormalizePath(steamPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRegistryString(string keyName, string valueName)
    {
        return Registry.GetValue(keyName, valueName, null) as string;
    }

    private async Task<List<string>> GetLibraryPathsAsync(
        string steamPath,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            steamPath
        };

        var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            warnings.Add("Steam libraryfolders.vdf was not found. Only the default Steam library was scanned.");
            return libraries.ToList();
        }

        try
        {
            var content = await File.ReadAllTextAsync(libraryFoldersPath, cancellationToken);

            foreach (Match match in LibraryPathRegex.Matches(content))
            {
                var path = NormalizePath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    libraries.Add(path);
                }
            }

            foreach (Match match in LegacyLibraryPathRegex.Matches(content))
            {
                var path = NormalizePath(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    libraries.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse libraryfolders.vdf at {Path}", libraryFoldersPath);
            warnings.Add("Failed to parse Steam libraryfolders.vdf. Only default library paths may be shown.");
        }

        return libraries
            .Where(Directory.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, WorkshopAcfMetadata>> ReadAppWorkshopMetadataAsync(
        string libraryPath,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var acfPath = Path.Combine(libraryPath, "steamapps", "workshop", $"appworkshop_{ArkAppId}.acf");
        if (!File.Exists(acfPath))
        {
            return new Dictionary<string, WorkshopAcfMetadata>(StringComparer.Ordinal);
        }

        try
        {
            var content = await File.ReadAllTextAsync(acfPath, cancellationToken);
            return ParseAppWorkshopMetadata(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading ACF metadata: {AcfPath}", acfPath);
            warnings.Add($"Could not read {Path.GetFileName(acfPath)} in {libraryPath}.");
            return new Dictionary<string, WorkshopAcfMetadata>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, WorkshopAcfMetadata> ParseAppWorkshopMetadata(string content)
    {
        var metadataById = new Dictionary<string, WorkshopAcfMetadata>(StringComparer.Ordinal);
        var contextStack = new Stack<string>();
        string? pendingKey = null;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(pendingKey))
                {
                    contextStack.Push(pendingKey);
                    pendingKey = null;
                }

                continue;
            }

            if (trimmed.StartsWith("}", StringComparison.Ordinal))
            {
                if (contextStack.Count > 0)
                {
                    contextStack.Pop();
                }

                pendingKey = null;
                continue;
            }

            var tokens = ExtractQuotedTokens(trimmed);
            if (tokens.Count == 1)
            {
                pendingKey = tokens[0];

                if (trimmed.EndsWith("{", StringComparison.Ordinal))
                {
                    contextStack.Push(pendingKey);
                    pendingKey = null;
                }

                continue;
            }

            if (tokens.Count >= 2)
            {
                pendingKey = null;
                ApplyAcfPair(contextStack, tokens[0], tokens[1], metadataById);
            }
        }

        return metadataById;
    }

    private static void ApplyAcfPair(
        Stack<string> contextStack,
        string key,
        string value,
        IDictionary<string, WorkshopAcfMetadata> metadataById)
    {
        if (contextStack.Count < 2)
        {
            return;
        }

        var stack = contextStack.ToArray();
        var itemId = stack[0];
        var section = stack[1];

        if (!IsNumeric(itemId))
        {
            return;
        }

        if (!section.Equals("WorkshopItemsInstalled", StringComparison.OrdinalIgnoreCase) &&
            !section.Equals("WorkshopItemDetails", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!metadataById.TryGetValue(itemId, out var metadata))
        {
            metadata = new WorkshopAcfMetadata();
            metadataById[itemId] = metadata;
        }

        var normalizedKey = key.ToLowerInvariant();
        switch (normalizedKey)
        {
            case "title":
                if (string.IsNullOrWhiteSpace(metadata.Title))
                {
                    metadata.Title = value;
                }
                break;

            case "size":
                if (TryParseInt64(value, out var size))
                {
                    metadata.SizeBytes = size;
                }
                break;

            case "timeupdated":
                if (TryParseUnixTimestamp(value, out var timeUpdated))
                {
                    metadata.TimeUpdatedUtc = timeUpdated;
                }
                break;

            case "timetouched":
                if (TryParseUnixTimestamp(value, out var timeTouched))
                {
                    metadata.TimeTouchedUtc = timeTouched;
                }
                break;
        }
    }

    private static List<string> ExtractQuotedTokens(string line)
    {
        var tokens = new List<string>();
        foreach (Match match in QuotedTokenRegex.Matches(line))
        {
            tokens.Add(UnescapeVdfValue(match.Groups[1].Value));
        }

        return tokens;
    }

    private static string UnescapeVdfValue(string value)
    {
        return value
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    private static bool TryParseInt64(string value, out long parsed)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseUnixTimestamp(string value, out DateTime timestampUtc)
    {
        timestampUtc = default;

        if (!TryParseInt64(value, out var seconds) || seconds <= 0)
        {
            return false;
        }

        try
        {
            timestampUtc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePath(string value)
    {
        var normalized = value
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim();

        try
        {
            return Path.GetFullPath(normalized);
        }
        catch
        {
            return normalized;
        }
    }

    private static void ApplyLocalMetadata(SteamWorkshopMod mod, string libraryPath, string contentPath)
    {
        DateTime? localLastUpdated = null;
        DateTime? localInstalledAt = null;

        try
        {
            localLastUpdated = Directory.GetLastWriteTimeUtc(contentPath);
        }
        catch
        {
        }

        try
        {
            localInstalledAt = Directory.GetCreationTimeUtc(contentPath);
        }
        catch
        {
        }

        var shouldReplacePath = !mod.LocalLastUpdatedAtUtc.HasValue ||
                                (localLastUpdated.HasValue && localLastUpdated.Value > mod.LocalLastUpdatedAtUtc.Value);

        if (shouldReplacePath)
        {
            mod.LibraryPath = libraryPath;
            mod.ContentPath = contentPath;
        }

        mod.LocalLastUpdatedAtUtc = PickLatest(mod.LocalLastUpdatedAtUtc, localLastUpdated);
        mod.InstalledAtUtc = PickLatest(mod.InstalledAtUtc, localInstalledAt);
    }

    private static void ApplyAcfMetadata(SteamWorkshopMod mod, WorkshopAcfMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            mod.Title = metadata.Title;
            mod.HasSteamMetadata = true;
        }

        if (metadata.SizeBytes.HasValue && metadata.SizeBytes > 0)
        {
            mod.SizeBytes = metadata.SizeBytes.Value;
        }

        mod.SteamLastUpdatedAtUtc = PickLatest(mod.SteamLastUpdatedAtUtc, metadata.TimeUpdatedUtc);
        mod.InstalledAtUtc = PickLatest(mod.InstalledAtUtc, metadata.TimeTouchedUtc);
    }

    private async Task<int> EnrichSteamMetadataAsync(List<SteamWorkshopMod> mods, CancellationToken cancellationToken)
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
                    if (!detail.TryGetProperty("publishedfileid", out var idElement))
                    {
                        continue;
                    }

                    var workshopId = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(workshopId) || !modLookup.TryGetValue(workshopId, out var mod))
                    {
                        continue;
                    }

                    if (detail.TryGetProperty("result", out var resultElement) &&
                        resultElement.ValueKind == JsonValueKind.Number &&
                        resultElement.TryGetInt32(out var resultCode) &&
                        resultCode != 1)
                    {
                        continue;
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

    private sealed class WorkshopAcfMetadata
    {
        public string? Title { get; set; }
        public long? SizeBytes { get; set; }
        public DateTime? TimeUpdatedUtc { get; set; }
        public DateTime? TimeTouchedUtc { get; set; }
    }
}
