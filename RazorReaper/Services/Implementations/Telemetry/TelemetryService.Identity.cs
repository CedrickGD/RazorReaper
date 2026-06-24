using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Generates and caches the two identifiers attached to every telemetry event:
///  • <b>install_id</b> — a per-installation GUID stored in MAUI Preferences. Survives app
///    restarts but resets if the user wipes app data or reinstalls.
///  • <b>hardware_id</b> — a SHA-256 hash of CPU/motherboard/BIOS serial numbers (read via
///    WMI). Stable across OS reinstalls. Falls back to install_id if WMI is unavailable.
/// </summary>
public sealed partial class TelemetryService
{
    private string GetOrCreateInstallId()
    {
        if (!string.IsNullOrWhiteSpace(installId))
        {
            return installId;
        }

        var existing = Preferences.Get(InstallIdPreferenceKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(existing) && Guid.TryParse(existing, out var existingGuid))
        {
            installId = existingGuid.ToString("D");
            return installId;
        }

        installId = Guid.NewGuid().ToString("D");
        Preferences.Set(InstallIdPreferenceKey, installId);
        return installId;
    }

    private string GetOrCreateHardwareId()
    {
        if (!string.IsNullOrWhiteSpace(hardwareId))
        {
            return hardwareId;
        }

        try
        {
            // UNIFIED ALGORITHM: Matches IHwidService precisely to ensure License HWID 
            // and Telemetry Session HWID are absolutely identical.
            string cpuId = GetWmiProperty("Win32_Processor", "ProcessorId");
            string diskId = GetWmiProperty("Win32_DiskDrive", "SerialNumber");
            string boardId = GetWmiProperty("Win32_BaseBoard", "SerialNumber");

            string rawId = $"{cpuId}-{diskId}-{boardId}";
            if (rawId == "UNKNOWN-UNKNOWN-UNKNOWN")
            {
                rawId = GetMachineGuid();
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawId));
            hardwareId = Convert.ToHexString(bytes).Substring(0, 32); // Use first 32 chars uppercase
            return hardwareId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate hardware ID, falling back to install_id.");
        }

        // Fall back to install_id if hardware queries fail entirely
        hardwareId = GetOrCreateInstallId();
        return hardwareId;
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
        catch { }
        return "UNKNOWN";
    }

    private string GetMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key != null)
            {
                return key.GetValue("MachineGuid")?.ToString() ?? "UNKNOWN_GUID";
            }
        }
        catch { }
        return "UNKNOWN_GUID";
    }
}
