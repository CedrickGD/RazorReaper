using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;
using RazorReaper.Configuration;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Checks <c>{AdminPanel.BaseUrl}/api/access/status</c> for a machine-level suspension/ban, keyed by
/// the same HWID + install id the telemetry pipeline reports, so the admin panel and this gate agree
/// on who a "user" is. Polls on a short interval so a suspension (or a lift) takes effect within one
/// cycle instead of surviving until the next launch.
/// </summary>
public sealed class AccessGateService : IAccessGateService
{
    // Mirrors TelemetryService.InstallIdPreferenceKey so both send the identical install id.
    private const string InstallIdPreferenceKey = "rr.telemetry.install_id";

    private readonly HttpClient _httpClient;
    private readonly IHwidService _hwidService;
    private readonly IOptions<AppConfiguration> _options;
    private readonly ILogger<AccessGateService> _logger;

    private Timer? _timer;
    private int _started;

    // Serializes CheckNowAsync: the timer, StartAsync and the UI "Re-check" button can otherwise
    // overlap, and a stale in-flight response could overwrite a newer applied state.
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public bool IsSuspended { get; private set; }
    public string? Mode { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset? BannedUntil { get; private set; }

    public event Action? OnAccessStateChanged;

    public AccessGateService(
        HttpClient httpClient,
        IHwidService hwidService,
        IOptions<AppConfiguration> options,
        ILogger<AccessGateService> logger)
    {
        _httpClient = httpClient;
        _hwidService = hwidService;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        // Guard against double-start (startup task + a defensive MainLayout call).
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        await CheckNowAsync().ConfigureAwait(false);

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.Value.AdminPanel.AccessCheckIntervalSeconds, 15, 3600));
        _timer = new Timer(async _ => await CheckNowAsync().ConfigureAwait(false), null, interval, interval);
    }

    public async Task<bool> CheckNowAsync()
    {
        var settings = _options.Value.AdminPanel;
        var baseUrl = settings.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return IsSuspended;
        }

        // A check is already in flight (timer tick, startup check and the UI "Re-check" button can
        // race): skip instead of queueing, so a stale response can never overwrite a newer applied
        // state. The in-flight check publishes the fresh result.
        if (!await _checkLock.WaitAsync(0).ConfigureAwait(false))
        {
            return IsSuspended;
        }

        try
        {
            var payload = new
            {
                hwid = _hwidService.GetHardwareId(),
                install_id = Preferences.Get(InstallIdPreferenceKey, string.Empty),
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60)));
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/access/status", payload, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A real answer we couldn't read (5xx/4xx): fail open, keep last-known state.
                _logger.LogInformation("Access status returned HTTP {Status}.", (int)response.StatusCode);
                return IsSuspended;
            }

            var result = await response.Content.ReadFromJsonAsync<AccessStatusResponse>(cts.Token).ConfigureAwait(false);
            if (result is null || !result.Ok)
            {
                return IsSuspended;
            }

            ApplyState(result);
            return IsSuspended;
        }
        catch (Exception ex)
        {
            // Offline / DNS / timeout: never lock a user out over a transient failure. Keep the last
            // known state; the next reachable poll still catches a server-side suspension.
            _logger.LogInformation(ex, "Access status check failed (kept last-known state).");
            return IsSuspended;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private void ApplyState(AccessStatusResponse result)
    {
        var wasSuspended = IsSuspended;
        var previousMode = Mode;
        var previousUntil = BannedUntil;

        IsSuspended = result.Suspended;
        Mode = result.Suspended ? result.Mode : null;
        Reason = result.Suspended ? result.Reason : null;
        BannedUntil = result.Suspended && DateTimeOffset.TryParse(result.BannedUntil, out var until)
            ? until.ToLocalTime()
            : null;

        if (wasSuspended != IsSuspended || previousMode != Mode || previousUntil != BannedUntil)
        {
            OnAccessStateChanged?.Invoke();
        }
    }

    private sealed class AccessStatusResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("suspended")]
        public bool Suspended { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("banned_until")]
        public string? BannedUntil { get; set; }
    }
}
