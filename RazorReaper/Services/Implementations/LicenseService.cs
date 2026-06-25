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
                return (true, result.Message ?? "Activated successfully.");
            }
            
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
                return (true, "License is valid.");
            }
            
            IsActivated = false;
            OnLicenseStateChanged?.Invoke();
            return (false, result?.Error ?? "Invalid license.");
        }
        catch (Exception)
        {
            // If network fails but we have a key, we might want to allow offline access briefly,
            // but for a strict HWID DRM, we enforce online check.
            IsActivated = false;
            OnLicenseStateChanged?.Invoke();
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
