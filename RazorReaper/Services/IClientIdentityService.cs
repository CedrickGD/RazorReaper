namespace RazorReaper.Services;

public sealed record ClientIdentity(string InstallId, string HardwareId);

public interface IClientIdentityService
{
    ClientIdentity GetIdentity();
}
