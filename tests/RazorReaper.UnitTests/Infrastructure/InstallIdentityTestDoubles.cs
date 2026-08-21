using RazorReaper.Services;

namespace RazorReaper.UnitTests.Infrastructure;

public sealed class StubLicenseService : ILicenseService
{
    public string CurrentLicenseKey { get; set; } = string.Empty;
    public bool IsActivated => !string.IsNullOrEmpty(CurrentLicenseKey);
    public bool IsPremium => IsActivated;
    public bool IsFreeTier => !IsPremium;
    public string? ExpiresAt => null;
    public string? LicenseType => null;

    public event Action OnLicenseStateChanged
    {
        add { }
        remove { }
    }

    public event Action OnLicenseActivated
    {
        add { }
        remove { }
    }

    public Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey)
        => Task.FromResult((false, "not used"));

    public Task<(bool Success, string Message)> ValidateLicenseAsync()
        => Task.FromResult((false, "not used"));
}

/// <summary>Fixed identity whose install id can be rotated on demand (records rotations).</summary>
public sealed class RotatingClientIdentityService(string installId, string hardwareId) : IClientIdentityService
{
    private ClientIdentity _identity = new(installId, hardwareId);

    public int RotationCount { get; private set; }

    public ClientIdentity GetIdentity() => _identity;

    public ClientIdentity RotateInstallId()
    {
        RotationCount++;
        _identity = new ClientIdentity(Guid.NewGuid().ToString("D"), _identity.HardwareId);
        return _identity;
    }
}
