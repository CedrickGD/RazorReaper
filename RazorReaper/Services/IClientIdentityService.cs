namespace RazorReaper.Services;

public sealed record ClientIdentity(string InstallId, string HardwareId);

public interface IClientIdentityService
{
    ClientIdentity GetIdentity();

    /// <summary>
    /// Replaces the install id with a fresh GUID (same preference key) and returns the new
    /// identity. Used when the backend rejects the current id (409/401 on install registration).
    /// </summary>
    ClientIdentity RotateInstallId();
}
