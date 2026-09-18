using System.Runtime.InteropServices;

namespace RazorReaper.Services
{
    /// <summary>
    /// The real <see cref="IDisplayApi"/>: EnumDisplayDevices / EnumDisplaySettings /
    /// ChangeDisplaySettingsEx, and nothing else. It holds no state and makes no decisions — a
    /// device name arrives, a user32 call is made, a result comes back. Everything that has to
    /// choose (which monitor, what to do when it is gone, whether to revert) lives in
    /// <see cref="StretchedResService"/>, where it can be tested.
    ///
    /// Applies use CDS_FULLSCREEN: a temporary change that is never written to the registry, so
    /// a reboot — or the auto-revert one layer up — always restores the normal desktop mode.
    /// NVAPI custom-mode creation is deliberately not used; only modes the driver already
    /// reports are ever applied.
    /// </summary>
    public sealed class Win32DisplayApi : IDisplayApi
    {
        public IReadOnlyList<DisplayAdapter> EnumerateAttachedDisplays()
        {
            var list = new List<DisplayAdapter>();
            uint index = 0;
            while (true)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, index, ref dd, 0))
                {
                    break;
                }

                index++;

                // Mirroring drivers (capture and remote-desktop shims) enumerate like a monitor
                // and cannot take a resolution change; offering one lists a screen that is not there.
                var attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                var mirroring = (dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
                if (!attached || mirroring || string.IsNullOrEmpty(dd.DeviceName))
                {
                    continue;
                }

                list.Add(new DisplayAdapter(
                    dd.DeviceName,
                    (dd.DeviceString ?? string.Empty).Trim(),
                    (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
            }

            return list;
        }

        public DisplayMode? GetCurrentMode(string? deviceName)
        {
            var dm = NewDevMode();
            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) == 0)
            {
                return null;
            }

            return ToMode(dm);
        }

        public IReadOnlyList<DisplayMode> GetSupportedModes(string? deviceName)
        {
            var modes = new List<DisplayMode>();
            var dm = NewDevMode();
            var i = 0;
            while (EnumDisplaySettings(deviceName, i, ref dm) != 0)
            {
                modes.Add(ToMode(dm));
                i++;
                dm = NewDevMode();
            }

            return modes;
        }

        public int ChangeMode(string? deviceName, DisplayMode mode, bool test)
        {
            // Start from the current mode so everything this service does not set — position,
            // orientation, the colour depth the device is actually in — survives the change.
            var target = NewDevMode();
            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref target) == 0)
            {
                return DisplayChangeCodes.BadParam;
            }

            target.dmPelsWidth = (uint)mode.Width;
            target.dmPelsHeight = (uint)mode.Height;
            if (mode.RefreshHz > 0) target.dmDisplayFrequency = (uint)mode.RefreshHz;
            if (mode.BitsPerPel > 0) target.dmBitsPerPel = (uint)mode.BitsPerPel;
            target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
            target.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();

            return ChangeDisplaySettingsEx(
                deviceName, ref target, IntPtr.Zero, test ? CDS_TEST : CDS_FULLSCREEN, IntPtr.Zero);
        }

        public int ResetToRegistryMode(string? deviceName)
            // A null DEVMODE with no flags resets the device to its registry-persisted mode.
            => ChangeDisplaySettingsEx(deviceName, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

        private static DisplayMode ToMode(in DEVMODE dm) => new(
            (int)dm.dmPelsWidth,
            (int)dm.dmPelsHeight,
            (int)dm.dmDisplayFrequency,
            (int)dm.dmBitsPerPel);

        // ────────────────────────────────────────────────────────────────────
        // Win32 interop
        // ────────────────────────────────────────────────────────────────────

        private const int ENUM_CURRENT_SETTINGS = -1;

        // ChangeDisplaySettingsEx dwFlags
        private const uint CDS_TEST = 0x00000002;
        private const uint CDS_FULLSCREEN = 0x00000004;

        // dmFields
        private const uint DM_BITSPERPEL = 0x00040000;
        private const uint DM_PELSWIDTH = 0x00080000;
        private const uint DM_PELSHEIGHT = 0x00100000;
        private const uint DM_DISPLAYFREQUENCY = 0x00400000;

        // DISPLAY_DEVICE.StateFlags
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

        private static DEVMODE NewDevMode() => new()
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
        };

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        // Overload used to apply a specific mode.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        // Overload used to reset to the registry-persisted mode (lpDevMode == NULL).
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;

            // Display union: POINTL dmPosition + dmDisplayOrientation + dmDisplayFixedOutput
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }
    }
}
