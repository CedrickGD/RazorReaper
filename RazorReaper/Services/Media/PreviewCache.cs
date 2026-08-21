using System.Runtime.InteropServices;

namespace RazorReaper.Services.Media;

/// <summary>
/// Backs the playable preview on the Convert page. The WebView maps
/// <see cref="VirtualHost"/> onto <see cref="Directory"/>, so a file placed here can be
/// handed to a &lt;video&gt; tag and streams from disk with Range support — a 35 MB clip as
/// a base64 data URL would have to be held in memory in full, twice.
///
/// The picked file is hard-linked in where possible, so nothing is duplicated on disk and
/// the user's original is never read-modified or moved. A copy is the fallback when the
/// link cannot be made (different volume, or a filesystem without link support).
/// </summary>
public static class PreviewCache
{
    public const string VirtualHost = "rr-preview.local";

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RazorReaper",
        "PreviewCache");

    /// <summary>
    /// Containers a WebView will actually play. An .avi or .mkv would give a black box with
    /// controls, which is worse than the still frame we fall back to.
    /// </summary>
    private static readonly HashSet<string> PlayableVideo =
        new(StringComparer.OrdinalIgnoreCase) { "mp4", "webm", "mov", "m4v" };

    private static readonly HashSet<string> PlayableAudio =
        new(StringComparer.OrdinalIgnoreCase) { "mp3", "wav", "ogg", "m4a", "aac", "opus", "flac" };

    public static bool CanPlay(string path)
    {
        var ext = MediaFormats.Normalize(Path.GetExtension(path));
        return PlayableVideo.Contains(ext) || PlayableAudio.Contains(ext);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// Publishes <paramref name="sourcePath"/> under the virtual host and returns the URL to
    /// point a media element at, or null when it could not be published.
    /// </summary>
    public static string? Publish(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        if (!CanPlay(sourcePath)) return null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            Clear();

            // A fresh name each time, so the WebView never serves a cached previous file
            // under a name it has already loaded.
            var name = $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
            var target = Path.Combine(Directory, name);

            if (!CreateHardLinkW(target, sourcePath, IntPtr.Zero))
            {
                File.Copy(sourcePath, target, overwrite: true);
            }

            return $"https://{VirtualHost}/{name}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops previously published files; they are throwaway by design.</summary>
    public static void Clear()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
            {
                try { File.Delete(file); } catch { /* still open in the WebView */ }
            }
        }
        catch
        {
            // Nothing here is load-bearing.
        }
    }
}
