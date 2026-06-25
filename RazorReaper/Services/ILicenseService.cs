namespace RazorReaper.Services;

public interface ILicenseService
{
    Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey);
    Task<(bool Success, string Message)> ValidateLicenseAsync();
    bool IsActivated { get; }
    bool IsPremium { get; }
    bool IsFreeTier { get; }
    string CurrentLicenseKey { get; }
    string? ExpiresAt { get; }
    string? LicenseType { get; }
    event Action OnLicenseStateChanged;
}
