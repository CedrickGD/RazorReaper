using RazorReaper.Services;

namespace RazorReaper.UnitTests.Infrastructure;

/// <summary>
/// A machine with as many monitors as a test needs. Every call records the device name it was
/// given, because that is the whole thing under test: a resolution aimed at the second screen
/// has to arrive at the second screen, and the only evidence of that is the name user32 would
/// have been handed.
/// </summary>
public sealed class FakeDisplayApi : IDisplayApi
{
    private readonly List<DisplayAdapter> _adapters = [];
    private readonly Dictionary<string, DisplayMode> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DisplayMode>> _supported = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string? Device, DisplayMode Mode, bool Test)> _changes = [];
    private readonly List<string?> _resets = [];

    /// <summary>Device names passed to <see cref="ChangeMode"/>, applies only (never the test pass).</summary>
    public IReadOnlyList<(string? Device, DisplayMode Mode, bool Test)> Changes => _changes;

    public IReadOnlyList<string?> Resets => _resets;

    /// <summary>The code every <see cref="ChangeMode"/> returns; default is success.</summary>
    public int ChangeResult { get; set; } = DisplayChangeCodes.Successful;

    /// <summary>The code <see cref="ResetToRegistryMode"/> returns; default is success.</summary>
    public int ResetResult { get; set; } = DisplayChangeCodes.Successful;

    /// <summary>Adds a monitor with a current mode and, by default, that mode as its only one.</summary>
    public FakeDisplayApi WithMonitor(
        string deviceName,
        int width,
        int height,
        bool isPrimary = false,
        string adapterName = "NVIDIA GeForce RTX 4080",
        int refreshHz = 144,
        IEnumerable<DisplayMode>? supported = null)
    {
        var mode = new DisplayMode(width, height, refreshHz, 32);
        _adapters.Add(new DisplayAdapter(deviceName, adapterName, isPrimary));
        _current[deviceName] = mode;
        _supported[deviceName] = (supported ?? [mode]).ToList();
        return this;
    }

    /// <summary>Unplugs a monitor: it stops enumerating and stops answering for its modes.</summary>
    public void Unplug(string deviceName)
    {
        _adapters.RemoveAll(a => string.Equals(a.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        _current.Remove(deviceName);
        _supported.Remove(deviceName);
    }

    public IReadOnlyList<DisplayAdapter> EnumerateAttachedDisplays() => _adapters.ToArray();

    public DisplayMode? GetCurrentMode(string? deviceName)
        => deviceName is not null && _current.TryGetValue(deviceName, out var mode) ? mode : null;

    public IReadOnlyList<DisplayMode> GetSupportedModes(string? deviceName)
        => deviceName is not null && _supported.TryGetValue(deviceName, out var modes)
            ? modes.ToArray()
            : Array.Empty<DisplayMode>();

    public int ChangeMode(string? deviceName, DisplayMode mode, bool test)
    {
        _changes.Add((deviceName, mode, test));
        if (!test && ChangeResult == DisplayChangeCodes.Successful && deviceName is not null && _current.ContainsKey(deviceName))
        {
            _current[deviceName] = mode;
        }

        return ChangeResult;
    }

    public int ResetToRegistryMode(string? deviceName)
    {
        _resets.Add(deviceName);
        return ResetResult;
    }
}
