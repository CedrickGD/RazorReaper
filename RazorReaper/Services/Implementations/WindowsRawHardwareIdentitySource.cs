using System.Management;
using Microsoft.Win32;

namespace RazorReaper.Services.Implementations;

internal interface IRawHardwareIdentitySource
{
    string GetRawHardwareIdentity();
}

internal sealed class WindowsRawHardwareIdentitySource : IRawHardwareIdentitySource
{
    private const string Unknown = "UNKNOWN";
    private const string UnknownGuid = "UNKNOWN_GUID";

    private readonly Func<string, string, IEnumerable<string?>> _queryWmiValues;
    private readonly Func<string?> _readMachineGuid;

    public WindowsRawHardwareIdentitySource()
        : this(QueryWmiValues, ReadMachineGuid)
    {
    }

    internal WindowsRawHardwareIdentitySource(
        Func<string, string, IEnumerable<string?>> queryWmiValues,
        Func<string?> readMachineGuid)
    {
        _queryWmiValues = queryWmiValues ?? throw new ArgumentNullException(nameof(queryWmiValues));
        _readMachineGuid = readMachineGuid ?? throw new ArgumentNullException(nameof(readMachineGuid));
    }

    public string GetRawHardwareIdentity()
    {
        var cpuId = GetFirstWmiValue("Win32_Processor", "ProcessorId");
        var diskId = GetFirstWmiValue("Win32_DiskDrive", "SerialNumber");
        var boardId = GetFirstWmiValue("Win32_BaseBoard", "SerialNumber");

        if (cpuId == Unknown && diskId == Unknown && boardId == Unknown)
        {
            return GetMachineGuidOrUnknown();
        }

        return $"{cpuId}-{diskId}-{boardId}";
    }

    private string GetFirstWmiValue(string wmiClass, string wmiProperty)
    {
        try
        {
            foreach (var candidate in _queryWmiValues(wmiClass, wmiProperty))
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }
        }
        catch
        {
            // An unavailable individual WMI query contributes the legacy UNKNOWN component.
        }

        return Unknown;
    }

    private string GetMachineGuidOrUnknown()
    {
        try
        {
            var machineGuid = _readMachineGuid();
            return machineGuid ?? UnknownGuid;
        }
        catch
        {
            return UnknownGuid;
        }
    }

    private static IEnumerable<string?> QueryWmiValues(string wmiClass, string wmiProperty)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT {wmiProperty} FROM {wmiClass}");
        foreach (var item in searcher.Get())
        {
            yield return item[wmiProperty]?.ToString();
        }
    }

    private static string? ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid")?.ToString();
    }
}
