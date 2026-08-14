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
    private const string ExpiresAtPref = "RR_LicenseExpiresAt";
    private const string LicenseTypePref = "RR_LicenseType";
    // How often we re-check the server while activated. Kept short so a revoke/delete in the
    // admin panel cuts the user off within seconds instead of surviving until the next launch.
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromSeconds(30);
    private Timer? _validationTimer;

    public bool IsActivated { get; private set; }
    // Premium = activated and not past the license end. The expiry check runs locally on every
    // read so a timed license cuts off at expires_at even when the machine is offline — the
    // offline grace below restores the last validated state, never more than that.
    public bool IsPremium => IsActivated && !IsExpired(ExpiresAt);
    public bool IsFreeTier => !IsPremium;
    public string CurrentLicenseKey => Preferences.Get(LicenseKeyPref, string.Empty);
    public string? ExpiresAt { get; private set; }
    public string? LicenseType { get; private set; }

    public event Action? OnLicenseStateChanged;
    public event Action? OnLicenseActivated;

    public LicenseService(HttpClient httpClient, IHwidService hwidService)
    {
        _httpClient = httpClient;
        _hwidService = hwidService;

        _validationTimer = new Timer(async _ => await BackgroundValidateAsync(), null, Timeout.Infinite, Timeout.Infinite);

        // Offline grace: restore the last server-validated state so a valid license works
        // without a network round-trip at startup. The 30s poll still re-validates as soon as
        // the server is reachable, and a server-side revoke/delete downgrades immediately.
        var cachedKey = CurrentLicenseKey;
        if (!string.IsNullOrWhiteSpace(cachedKey))
        {
            var cachedExpiry = Preferences.Get(ExpiresAtPref, string.Empty);
            var expiresAt = string.IsNullOrWhiteSpace(cachedExpiry) ? null : cachedExpiry;
            if (!IsExpired(expiresAt))
            {
                IsActivated = true;
                ExpiresAt = expiresAt;
                LicenseType = Preferences.Get(LicenseTypePref, string.Empty) is { Length: > 0 } t ? t : null;
            }
            _validationTimer?.Change(TimeSpan.FromSeconds(2), ValidationInterval);
        }
    }

    private static bool IsExpired(string? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(expiresAt)) return false; // lifetime
        return DateTimeOffset.TryParse(expiresAt, out var end) && end <= DateTimeOffset.UtcNow;
    }

    private void PersistValidatedState()
    {
        Preferences.Set(ExpiresAtPref, ExpiresAt ?? string.Empty);
        Preferences.Set(LicenseTypePref, LicenseType ?? string.Empty);
    }

    private void ClearCachedState()
    {
        // The key itself must go too: with only expiry/type removed, the next launch would
        // read the surviving key, see no expiry, and resurrect the license as an unlimited
        // lifetime via the offline-grace path — after the server explicitly rejected it.
        // Only the explicit-rejection path calls this, never a transient network failure.
        Preferences.Remove(LicenseKeyPref);
        Preferences.Remove(ExpiresAtPref);
        Preferences.Remove(LicenseTypePref);
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
                PersistValidatedState();
                OnLicenseStateChanged?.Invoke();
                // Raised after the state change so the UI has already re-rendered into its
                // premium form by the time the celebration overlay covers it.
                OnLicenseActivated?.Invoke();
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
                PersistValidatedState();
                OnLicenseStateChanged?.Invoke();
                _validationTimer?.Change(ValidationInterval, ValidationInterval);
                return (true, "License is valid.");
            }

            // The server explicitly rejected the key (revoked, expired, deleted) — drop the
            // offline-grace cache too, otherwise the next restart would resurrect premium.
            IsActivated = false;
            ExpiresAt = null;
            LicenseType = null;
            ClearCachedState();
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
