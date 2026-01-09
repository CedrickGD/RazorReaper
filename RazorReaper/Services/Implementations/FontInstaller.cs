using System.Drawing.Text;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Ensures selected font presets are installed for the current Windows user.
/// </summary>
public class FontInstaller : IFontInstaller
{
    private const string RegistryFontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const uint FontChangeMessage = 0x001D;
    private const uint SendMessageTimeoutFlags = 0x0002;
    private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf" };

    private sealed class FontPackageDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string PackageFileName { get; }
        public string DownloadUrl { get; }
        public IReadOnlyList<string> FamilyNames { get; }
        public IReadOnlyList<string> FilePrefixes { get; }

        public FontPackageDefinition(
            string id,
            string displayName,
            string packageFileName,
            string downloadUrl,
            IReadOnlyList<string> familyNames,
            IReadOnlyList<string> filePrefixes)
        {
            Id = id;
            DisplayName = displayName;
            PackageFileName = packageFileName;
            DownloadUrl = downloadUrl;
            FamilyNames = familyNames;
            FilePrefixes = filePrefixes;
        }
    }

    private static readonly IReadOnlyList<FontPackageDefinition> FontPackages = new[]
    {
        new FontPackageDefinition(
            "jetbrains-mono-nerd",
            "JetBrains Mono Nerd Font",
            "JetBrainsMono.zip",
            "https://github.com/ryanoasis/nerd-fonts/releases/latest/download/JetBrainsMono.zip",
            new[]
            {
                "JetBrainsMono Nerd Font",
                "JetBrainsMono Nerd Font Mono",
                "JetBrainsMono Nerd Font Propo"
            },
            new[]
            {
                "JetBrainsMonoNerdFont",
                "JetBrainsMonoNerdFontMono",
                "JetBrainsMonoNerdFontPropo"
            }),
        new FontPackageDefinition(
            "space-grotesk",
            "Space Grotesk",
            "SpaceGrotesk[wght].ttf",
            "https://github.com/google/fonts/raw/main/ofl/spacegrotesk/SpaceGrotesk%5Bwght%5D.ttf",
            new[] { "Space Grotesk" },
            new[] { "SpaceGrotesk" }),
        new FontPackageDefinition(
            "manrope",
            "Manrope",
            "Manrope[wght].ttf",
            "https://github.com/google/fonts/raw/main/ofl/manrope/Manrope%5Bwght%5D.ttf",
            new[] { "Manrope" },
            new[] { "Manrope" }),
        new FontPackageDefinition(
            "fira-code-nerd",
            "Fira Code Nerd Font",
            "FiraCode.zip",
            "https://github.com/ryanoasis/nerd-fonts/releases/latest/download/FiraCode.zip",
            new[]
            {
                "FiraCode Nerd Font",
                "FiraCode Nerd Font Mono",
                "FiraCode Nerd Font Propo"
            },
            new[]
            {
                "FiraCodeNerdFont",
                "FiraCodeNerdFontMono",
                "FiraCodeNerdFontPropo"
            })
    };

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

        var preset = FindPreset(presetId);
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
            foreach (var preset in FontPackages)
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

        var preset = FindPreset(presetId);
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

            var fontFiles = new List<string>();
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

                fontFiles = Directory
                    .EnumerateFiles(extractPath, "*.*", SearchOption.AllDirectories)
                    .Where(path => FontExtensions.Contains(Path.GetExtension(path)))
                    .Where(path => HasFontPrefix(Path.GetFileName(path), preset.FilePrefixes))
                    .ToList();
            }
            else if (FontExtensions.Contains(Path.GetExtension(packagePath)))
            {
                if (HasFontPrefix(Path.GetFileName(packagePath), preset.FilePrefixes))
                {
                    fontFiles.Add(packagePath);
                }
            }
            else
            {
                TryDeleteTempPackage(packagePath, tempRoot);
                throw new InvalidDataException("Font package is not a valid zip archive.");
            }

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

    private FontInstallResult InstallFontFiles(IEnumerable<string> fontFiles)
    {
        var result = new FontInstallResult();
        var fontsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "Fonts");

        Directory.CreateDirectory(fontsDirectory);

        using var registryKey = Registry.CurrentUser.CreateSubKey(RegistryFontsKey, true);

        foreach (var fontFile in fontFiles)
        {
            var fileName = Path.GetFileName(fontFile);
            var destinationPath = Path.Combine(fontsDirectory, fileName);

            try
            {
                if (File.Exists(destinationPath))
                {
                    result.SkippedCount++;
                }
                else
                {
                    File.Copy(fontFile, destinationPath, false);
                    result.InstalledCount++;
                }

                var registryName = $"{Path.GetFileNameWithoutExtension(fileName)} (TrueType)";
                registryKey?.SetValue(registryName, fileName, RegistryValueKind.String);

                var added = AddFontResourceEx(destinationPath, 0, IntPtr.Zero);
                if (added == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    logger.LogWarning(
                        "AddFontResourceEx failed for {FontPath} (Win32 error {Error}).",
                        destinationPath,
                        error);
                }
            }
            catch (IOException ex)
            {
                result.FailedCount++;
                logger.LogWarning(ex, "Skipping locked font file: {FontPath}", destinationPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                result.FailedCount++;
                logger.LogWarning(ex, "Skipping inaccessible font file: {FontPath}", destinationPath);
            }
        }

        BroadcastFontChange();
        return result;
    }

    private static bool HasFontPrefix(string fileName, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static FontPackageDefinition? FindPreset(string presetId)
    {
        return FontPackages.FirstOrDefault(preset =>
            string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));
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

    private static void BroadcastFontChange()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            FontChangeMessage,
            IntPtr.Zero,
            IntPtr.Zero,
            SendMessageTimeoutFlags,
            1000,
            out _);
    }

    [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
