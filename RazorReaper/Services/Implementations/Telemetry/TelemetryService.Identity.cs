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
            var components = new StringBuilder();

            // CPU ProcessorId — burned into the chip, survives OS reinstalls and renames
            try
            {
                using var cpu = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (var obj in cpu.Get())
                {
                    components.Append(obj["ProcessorId"]?.ToString()?.Trim());
                    break;
                }
            }
            catch
            {
                // WMI query may fail on locked-down systems.
            }

            // Motherboard SerialNumber
            try
            {
                using var board = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var obj in board.Get())
                {
                    components.Append(obj["SerialNumber"]?.ToString()?.Trim());
                    break;
                }
            }
            catch
            {
                // WMI query may fail on locked-down systems.
            }

            // BIOS SerialNumber — another hardware-level constant
            try
            {
                using var bios = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
                foreach (var obj in bios.Get())
                {
                    components.Append(obj["SerialNumber"]?.ToString()?.Trim());
                    break;
                }
            }
            catch
            {
                // WMI query may fail on locked-down systems.
            }

            if (components.Length > 0)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(components.ToString()));
                hardwareId = Convert.ToHexString(hash).ToLowerInvariant();
                return hardwareId;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate hardware ID, falling back to install_id.");
        }

        // Fall back to install_id if hardware queries fail entirely
        hardwareId = GetOrCreateInstallId();
        return hardwareId;
    }
}
