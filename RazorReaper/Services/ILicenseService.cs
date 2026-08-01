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

    /// <summary>
    /// Fires only when the user has just activated a key by hand — never on the background
    /// re-validation poll. This is the one-shot "congratulations" signal the celebration
    /// overlay listens on, so it can't be re-triggered every 30 seconds.
    /// </summary>
    event Action OnLicenseActivated;
}
