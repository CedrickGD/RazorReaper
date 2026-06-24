using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

public class HwidService : IHwidService
{
    public string GetHardwareId()
    {
        string cpuId = GetWmiProperty("Win32_Processor", "ProcessorId");
        string diskId = GetWmiProperty("Win32_DiskDrive", "SerialNumber");
        string boardId = GetWmiProperty("Win32_BaseBoard", "SerialNumber");

        string rawId = $"{cpuId}-{diskId}-{boardId}";
        if (rawId == "UNKNOWN-UNKNOWN-UNKNOWN")
        {
            rawId = GetMachineGuid();
        }

        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawId));
        return Convert.ToHexString(bytes).Substring(0, 32); // Use first 32 chars
    }

    private string GetWmiProperty(string wmiClass, string wmiProperty)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {wmiProperty} FROM {wmiClass}");
            foreach (var item in searcher.Get())
            {
                var value = item[wmiProperty]?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }
        catch
        {
            // Ignore WMI errors
        }
        return "UNKNOWN";
    }

    private string GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key != null)
            {
                return key.GetValue("MachineGuid")?.ToString() ?? "UNKNOWN_GUID";
            }
        }
        catch { }
        return "UNKNOWN_GUID";
    }
}
