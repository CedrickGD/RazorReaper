using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Per-preset image override storage. Each preset can have a single user-supplied image saved
/// under <c>LocalAppData/RazorReaper/PresetImages/&lt;slug&gt;.&lt;ext&gt;</c>. Overrides win
/// over the hosted <c>images/presets/&lt;slug&gt;.png</c> CDN image when reading.
/// </summary>
public partial class IniPresetService
{
    private const string DefaultPresetImage = "images/presets/default.png";

    /// <inheritdoc/>
    public async Task<string?> GetPresetImageSourceAsync(string presetName, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(presetName))
            {
                var slug = ToSlug(presetName);

                // 1) User override wins. We base64-encode it so the WebView can load it without
                //    needing a writable wwwroot or a custom URI handler.
                var overridePath = FindOverrideImagePath(slug);
                if (overridePath != null)
                {
                    var dataUrl = await TryReadAsDataUrlAsync(overridePath, ct);
                    if (dataUrl != null)
                    {
                        return dataUrl;
                    }
                }

                // 2) Hosted preset image from the media CDN. Only built-in presets ship CDN
                //    images — skipping the lookup for customs avoids a guaranteed 404 on
                //    every resolve (the media cache does not remember misses).
                if (GetPresetByName(presetName)?.IsCustom != true)
                {
                    var hosted = await _hostedMedia.GetSrcAsync($"images/presets/{slug}.png", ct: ct);
                    if (hosted != null)
                    {
                        return hosted;
                    }
                }
            }

            return await _hostedMedia.GetSrcAsync(DefaultPresetImage, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving image source for preset: {PresetName}", presetName);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetPresetImageAsync(string presetName, Stream sourceStream, string extension)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName) || sourceStream == null)
            {
                return false;
            }

            var normalizedExt = NormalizeImageExtension(extension);
            if (normalizedExt == null)
            {
                _logger.LogWarning("Rejected preset image override with unsupported extension '{Ext}' for {Preset}", extension, presetName);
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_image_set",
                    TelemetryEventStatus.Degraded,
                    "Unsupported image extension.",
                    new Dictionary<string, object?> { ["preset_name"] = presetName, ["extension"] = extension });
                return false;
            }

            if (!Directory.Exists(_customImagesDir))
            {
                Directory.CreateDirectory(_customImagesDir);
            }

            var slug = ToSlug(presetName);

            // Remove any pre-existing override (could be a different extension).
            DeleteExistingOverrides(slug);

            var destPath = Path.Combine(_customImagesDir, $"{slug}{normalizedExt}");

            await using (var dest = File.Create(destPath))
            {
                await sourceStream.CopyToAsync(dest);
            }

            _ = _telemetryService.TrackEventAsync(
                "ini_preset_image_set",
                TelemetryEventStatus.Ok,
                "Preset image override saved.",
                new Dictionary<string, object?> { ["preset_name"] = presetName });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting custom image for preset {Preset}", presetName);
            _ = _telemetryService.TrackEventAsync(
                "ini_preset_image_set",
                TelemetryEventStatus.Down,
                ex.Message,
                new Dictionary<string, object?> { ["preset_name"] = presetName });
            return false;
        }
    }

    /// <inheritdoc/>
    public bool ResetPresetImage(string presetName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return false;
            }

            var slug = ToSlug(presetName);
            var removed = DeleteExistingOverrides(slug);

            if (removed)
            {
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_image_reset",
                    TelemetryEventStatus.Ok,
                    "Preset image override removed.",
                    new Dictionary<string, object?> { ["preset_name"] = presetName });
            }

            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting custom image for preset {Preset}", presetName);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool HasCustomImage(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return false;
        }

        try
        {
            return FindOverrideImagePath(ToSlug(presetName)) != null;
        }
        catch
        {
            return false;
        }
    }

    private string? FindOverrideImagePath(string slug)
    {
        if (!Directory.Exists(_customImagesDir))
        {
            return null;
        }

        foreach (var ext in AllowedImageExtensions)
        {
            var candidate = Path.Combine(_customImagesDir, $"{slug}{ext}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<string?> TryReadAsDataUrlAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/jpeg"
            };

            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read preset image override at {Path}", filePath);
            return null;
        }
    }

    private bool DeleteExistingOverrides(string slug)
    {
        if (!Directory.Exists(_customImagesDir))
        {
            return false;
        }

        var removedAny = false;
        foreach (var ext in AllowedImageExtensions)
        {
            var path = Path.Combine(_customImagesDir, $"{slug}{ext}");
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removedAny = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete preset image override at {Path}", path);
                }
            }
        }
        return removedAny;
    }

    private static string? NormalizeImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var ext = extension.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.'))
        {
            ext = "." + ext;
        }

        if (ext == ".jpeg")
        {
            ext = ".jpg";
        }

        return AllowedImageExtensions.Contains(ext) ? ext : null;
    }
}
