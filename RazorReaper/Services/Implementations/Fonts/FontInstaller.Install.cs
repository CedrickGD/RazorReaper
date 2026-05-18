using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Per-user font registration: copy each .ttf/.otf into the user's local fonts folder, write
/// the matching registry value under <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts</c>,
/// and notify the running process via <c>AddFontResourceEx</c>. Finishes by broadcasting
/// <c>WM_FONTCHANGE</c> so already-open apps see the new font without a restart.
/// </summary>
public partial class FontInstaller
{
    private const string RegistryFontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const uint FontChangeMessage = 0x001D;
    private const uint SendMessageTimeoutFlags = 0x0002;
    private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

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
