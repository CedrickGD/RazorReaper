using System.Drawing.Text;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Ensures selected font presets are installed for the current Windows user. The font catalog
/// (and "is this font's file?" matching) lives in <see cref="FontPackageDefinition"/>; the
/// Win32 registration plumbing lives in the <c>FontInstaller.Install.cs</c> partial.
/// </summary>
public partial class FontInstaller : IFontInstaller
{
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf" };

    private readonly HttpClient httpClient;
    private readonly ILogger<FontInstaller> logger;
    private readonly SemaphoreSlim installLock = new(1, 1);

    private sealed class FontInstallResult
    {
        public int InstalledCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }

        public bool HasAny => InstalledCount + SkippedCount > 0;
    }

    public FontInstaller(HttpClient httpClient, ILogger<FontInstaller> logger)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public bool IsFontInstalled(string presetId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var preset = FontPackageDefinition.FindById(presetId);
        if (preset == null)
        {
            return false;
        }

        try
        {
            using var fonts = new InstalledFontCollection();
            foreach (var family in fonts.Families)
            {
                if (preset.FamilyNames.Any(name =>
                    string.Equals(name, family.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to query installed fonts.");
        }

        return false;
    }

    public async Task EnsurePresetFontsInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await installLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var preset in FontPackageDefinition.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await EnsureFontInstalledCoreAsync(preset, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to install font preset: {Preset}", preset.DisplayName);
                }
            }
        }
        finally
        {
            installLock.Release();
        }
    }

    public async Task EnsureFontInstalledAsync(string presetId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var preset = FontPackageDefinition.FindById(presetId);
        if (preset == null)
        {
            logger.LogWarning("Unknown font preset requested: {PresetId}", presetId);
            return;
        }

        await installLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureFontInstalledCoreAsync(preset, cancellationToken);
        }
        finally
        {
            installLock.Release();
        }
    }

    private async Task EnsureFontInstalledCoreAsync(FontPackageDefinition preset, CancellationToken cancellationToken)
    {
        if (IsFontInstalled(preset.Id))
        {
            logger.LogInformation("{Preset} already installed.", preset.DisplayName);
            return;
        }

        logger.LogInformation("{Preset} not detected. Installing for current user.", preset.DisplayName);

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "RazorReaper",
            "fonts",
            preset.Id,
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempRoot);

        try
        {
            var packagePath = await GetFontPackageAsync(preset, tempRoot, cancellationToken);
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                logger.LogWarning("Font package not available for {Preset}.", preset.DisplayName);
                return;
            }

            var fontFiles = ResolveFontFiles(preset, packagePath, tempRoot);
            if (fontFiles.Count == 0)
            {
                TryDeleteTempPackage(packagePath, tempRoot);
                throw new InvalidDataException("Font package did not include expected font files.");
            }

            var result = InstallFontFiles(fontFiles);

            if (!result.HasAny)
            {
                throw new InvalidOperationException("Font files could not be installed.");
            }
            logger.LogInformation("{Preset} installed successfully.", preset.DisplayName);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up font temp path: {Path}", tempRoot);
            }
        }
    }

    /// <summary>Decide whether <paramref name="packagePath"/> is a zip (extract first) or a raw
    /// font file, and return the list of expected font files derived from it. Throws if the
    /// payload looks like neither.</summary>
    private static List<string> ResolveFontFiles(FontPackageDefinition preset, string packagePath, string tempRoot)
    {
        if (IsZipFile(packagePath))
        {
            var extractPath = Path.Combine(tempRoot, "extracted");
            Directory.CreateDirectory(extractPath);

            try
            {
                ZipFile.ExtractToDirectory(packagePath, extractPath);
            }
            catch (InvalidDataException)
            {
                TryDeleteTempPackage(packagePath, tempRoot);
                throw;
            }

            return Directory
                .EnumerateFiles(extractPath, "*.*", SearchOption.AllDirectories)
                .Where(path => FontExtensions.Contains(Path.GetExtension(path)))
                .Where(path => preset.MatchesFilePrefix(Path.GetFileName(path)))
                .ToList();
        }

        if (FontExtensions.Contains(Path.GetExtension(packagePath)))
        {
            return preset.MatchesFilePrefix(Path.GetFileName(packagePath))
                ? new List<string> { packagePath }
                : new List<string>();
        }

        TryDeleteTempPackage(packagePath, tempRoot);
        throw new InvalidDataException("Font package is not a valid zip archive.");
    }

    private async Task<string?> GetFontPackageAsync(
        FontPackageDefinition preset,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        var bundledPath = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "assets",
            "fonts",
            preset.PackageFileName);

        if (File.Exists(bundledPath))
        {
            logger.LogInformation("Using bundled font package at {Path}", bundledPath);
            return bundledPath;
        }

        var downloadPath = Path.Combine(tempRoot, preset.PackageFileName);
        logger.LogInformation("Downloading {Preset} from {Url}", preset.DisplayName, preset.DownloadUrl);

        using var response = await httpClient.GetAsync(preset.DownloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(downloadPath);
        await source.CopyToAsync(destination, cancellationToken);

        return downloadPath;
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 4)
            {
                return false;
            }

            var signature = new byte[4];
            var read = stream.Read(signature, 0, signature.Length);
            if (read < 4)
            {
                return false;
            }

            return signature[0] == 0x50 && signature[1] == 0x4B &&
                (signature[2] == 0x03 || signature[2] == 0x05 || signature[2] == 0x07) &&
                (signature[3] == 0x04 || signature[3] == 0x06 || signature[3] == 0x08);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteTempPackage(string packagePath, string tempRoot)
    {
        try
        {
            if (packagePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
