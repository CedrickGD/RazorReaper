namespace RazorReaper.Services;

public interface ILicenseService
{
    Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey);
    Task<(bool Success, string Message)> ValidateLicenseAsync();
    bool IsActivated { get; }
    string CurrentLicenseKey { get; }
    event Action OnLicenseStateChanged;
}
