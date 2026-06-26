using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Storage;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

public class LicenseService : ILicenseService
{
    private readonly HttpClient _httpClient;
    private readonly IHwidService _hwidService;
    private const string ApiBaseUrl = "https://rr-admin-panel.pages.dev"; // Change to your actual worker URL
    private const string LicenseKeyPref = "RR_LicenseKey";
    // How often we re-check the server while activated. Kept short so a revoke/delete in the
    // admin panel cuts the user off within seconds instead of surviving until the next launch.
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromSeconds(30);
    private Timer? _validationTimer;

    public bool IsActivated { get; private set; }
    public bool IsPremium => IsActivated; // For now, if they are activated, they are premium.
    public bool IsFreeTier => !IsActivated;
    public string CurrentLicenseKey => Preferences.Get(LicenseKeyPref, string.Empty);
    public string? ExpiresAt { get; private set; }
    public string? LicenseType { get; private set; }
    
    public event Action? OnLicenseStateChanged;

    public LicenseService(HttpClient httpClient, IHwidService hwidService)
    {
        _httpClient = httpClient;
        _hwidService = hwidService;
        
        // Start background validation timer (disabled initially)
        _validationTimer = new Timer(async _ => await BackgroundValidateAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private async Task BackgroundValidateAsync()
    {
        if (IsActivated && !string.IsNullOrWhiteSpace(CurrentLicenseKey))
        {
            await ValidateLicenseAsync();
        }
    }

    public async Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey)
    {
        try
        {
            var hwid = _hwidService.GetHardwareId();
            var payload = new { license_key = licenseKey, hwid = hwid };
            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/license/activate", payload);
            var result = await response.Content.ReadFromJsonAsync<LicenseApiResponse>();

            if (response.IsSuccessStatusCode && result != null && result.Ok)
            {
                Preferences.Set(LicenseKeyPref, licenseKey);
                IsActivated = true;
                ExpiresAt = result.ExpiresAt;
                LicenseType = result.Type;
                OnLicenseStateChanged?.Invoke();
                _validationTimer?.Change(ValidationInterval, ValidationInterval);
                return (true, result.Message ?? "Activated successfully.");
            }
            
            _validationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return (false, result?.Error ?? "Failed to activate license.");
        }
        catch (Exception ex)
        {
            return (false, $"Network error: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> ValidateLicenseAsync()
    {
        var key = CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            IsActivated = false;
            OnLicenseStateChanged?.Invoke();
            return (false, "No license key found.");
        }

        try
        {
            var hwid = _hwidService.GetHardwareId();
            var payload = new { license_key = key, hwid = hwid };
            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/license/validate", payload);
            var result = await response.Content.ReadFromJsonAsync<LicenseApiResponse>();

            if (response.IsSuccessStatusCode && result != null && result.Ok)
            {
                IsActivated = true;
                ExpiresAt = result.ExpiresAt;
                LicenseType = result.Type;
                OnLicenseStateChanged?.Invoke();
                _validationTimer?.Change(ValidationInterval, ValidationInterval);
                return (true, "License is valid.");
            }
            
            IsActivated = false;
            _validationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            OnLicenseStateChanged?.Invoke();
            return (false, result?.Error ?? "Invalid license.");
        }
        catch (Exception)
        {
            // Transient network failure: do NOT downgrade. A real revoke/delete comes back as a
            // proper HTTP response (handled above and cuts access immediately); only an
            // unreachable server lands here. Flipping to Free on every flaky poll would kick an
            // active user out every 30s on bad wifi. Keep the last-known state and keep polling
            // so the next reachable check still catches a server-side revoke.
            return (false, "Network error. Please connect to the internet to verify license.");
        }
    }

    private class LicenseApiResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }
        
        [JsonPropertyName("message")]
        public string? Message { get; set; }
        
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }
}
