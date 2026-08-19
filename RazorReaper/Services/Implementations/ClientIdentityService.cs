using System.Security.Cryptography;
using System.Text;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

internal sealed class ClientIdentityService : IClientIdentityService
{
    private const string InstallIdPreferenceKey = "rr.telemetry.install_id";

    private readonly IPreferencesStore _preferences;
    private readonly IRawHardwareIdentitySource _rawHardwareIdentitySource;
    private readonly object _identityGate = new();

    private ClientIdentity? _identity;

    public ClientIdentityService(
        IPreferencesStore preferences,
        IRawHardwareIdentitySource rawHardwareIdentitySource)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _rawHardwareIdentitySource = rawHardwareIdentitySource
            ?? throw new ArgumentNullException(nameof(rawHardwareIdentitySource));
    }

    public ClientIdentity GetIdentity()
    {
        var cached = Volatile.Read(ref _identity);
        if (cached is not null)
        {
            return cached;
        }

        lock (_identityGate)
        {
            cached = _identity;
            if (cached is not null)
            {
                return cached;
            }

            var installId = ResolveInstallId();
            var rawHardwareIdentity = _rawHardwareIdentitySource.GetRawHardwareIdentity();
            var hardwareId = HashHardwareIdentity(rawHardwareIdentity);
            cached = new ClientIdentity(installId, hardwareId);
            Volatile.Write(ref _identity, cached);
            return cached;
        }
    }

    private string ResolveInstallId()
    {
        var stored = _preferences.Get(InstallIdPreferenceKey, string.Empty);
        if (Guid.TryParse(stored, out var parsed))
        {
            return parsed.ToString("D");
        }

        var created = Guid.NewGuid().ToString("D");
        _preferences.Set(InstallIdPreferenceKey, created);
        return created;
    }

    private static string HashHardwareIdentity(string rawHardwareIdentity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawHardwareIdentity));
        return Convert.ToHexString(bytes)[..32];
    }
}
