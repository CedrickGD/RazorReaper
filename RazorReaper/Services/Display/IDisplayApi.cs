namespace RazorReaper.Services
{
    /// <summary>
    /// One display mode as the driver reports it. These are exactly the four DEVMODE fields
    /// <see cref="StretchedResService"/> ever sets (DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL |
    /// DM_DISPLAYFREQUENCY), so a mode captured here round-trips through an apply unchanged.
    /// </summary>
    public readonly record struct DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPel);

    /// <summary>
    /// A display output attached to the desktop. <paramref name="DeviceName"/> is the Win32
    /// display-device name (<c>\.\DISPLAY1</c>) — the same name space the crosshair overlay's
    /// monitor list uses, and the one <c>EnumDisplaySettings</c> /
    /// <c>ChangeDisplaySettingsEx</c> take, so a name picked in one feature means the same
    /// screen in the other.
    /// </summary>
    public sealed record DisplayAdapter(string DeviceName, string AdapterName, bool IsPrimary);

    /// <summary>The <c>DISP_CHANGE_*</c> return codes, shared by the Win32 layer and its fakes.</summary>
    public static class DisplayChangeCodes
    {
        public const int Successful = 0;
        public const int Restart = 1;
        public const int Failed = -1;
        public const int BadMode = -2;
        public const int NotUpdated = -3;
        public const int BadFlags = -4;
        public const int BadParam = -5;
        public const int BadDualView = -6;
    }

    /// <summary>
    /// The Win32 display surface, behind an interface.
    ///
    /// Everything here is a call into user32 that needs a real display attached to answer, which
    /// is why it is a seam: the device-name plumbing above it — which monitor a preference
    /// resolves to, what happens when that monitor is unplugged, which device an apply is aimed
    /// at — is the part that has to be right on a machine with three screens, and it is the part
    /// that cannot be exercised on a build agent with none. A fake of this interface lets those
    /// decisions be tested without pretending to own a second monitor.
    ///
    /// Every method takes an explicit device name. Passing <c>null</c> keeps user32's own
    /// meaning: the display device the calling thread is on, which is what this service passed
    /// for every call before monitors were selectable.
    /// </summary>
    public interface IDisplayApi
    {
        /// <summary>Every output currently attached to the desktop, in adapter order.</summary>
        IReadOnlyList<DisplayAdapter> EnumerateAttachedDisplays();

        /// <summary>The device's current desktop mode, or null when it cannot be read.</summary>
        DisplayMode? GetCurrentMode(string? deviceName);

        /// <summary>Every mode the driver reports for the device, unfiltered and undeduped.</summary>
        IReadOnlyList<DisplayMode> GetSupportedModes(string? deviceName);

        /// <summary>
        /// Applies (or, with <paramref name="test"/>, only validates) a mode on the device.
        /// Returns a <see cref="DisplayChangeCodes"/> value. The change is temporary — it is
        /// never written to the registry, so a reboot always brings the normal desktop back.
        /// </summary>
        int ChangeMode(string? deviceName, DisplayMode mode, bool test);

        /// <summary>Resets the device to its registry-persisted (normal) mode.</summary>
        int ResetToRegistryMode(string? deviceName);
    }
}
