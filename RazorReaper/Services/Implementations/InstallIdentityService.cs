using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services.Http;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// rr.install.v1 client side: one ECDSA P-256 keypair per install (private half in secure
/// storage), one-time registration of the public half with the backend, and request signing.
/// Registration is best-effort: it never throws, never blocks startup, and retries transient
/// failures in the background with exponential backoff (max five attempts per process).
/// </summary>
public sealed class InstallIdentityService : IInstallIdentityService, IDisposable
{
    internal const string PrivateKeyStoreKey = "rr.install.key";
    internal const string RegisteredAtPreferenceKey = "rr.install.registered_at";
    internal const string RegisteredInstallIdPreferenceKey = "rr.install.registered_id";
    internal const string RegisterPath = "/api/install/register";
    internal const string HttpClientName = "RazorReaperTelemetry";
    private const int MinTimeoutSeconds = 3;
    private const int MaxTimeoutSeconds = 60;

    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8)
    ];

    private readonly IClientIdentityService _clientIdentity;
    private readonly ISecureValueStore _secureStore;
    private readonly IPreferencesStore _preferences;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AppConfiguration> _options;
    private readonly ILicenseService _licenseService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallIdentityService> _logger;

    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private readonly SemaphoreSlim _registerGate = new(1, 1);
    private readonly object _signGate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private ECDsa? _key;
    private bool _keyGeneratedThisProcess;
    private volatile bool _isRegistered;
    private int _retryScheduled;

    public InstallIdentityService(
        IClientIdentityService clientIdentity,
        ISecureValueStore secureStore,
        IPreferencesStore preferences,
        IHttpClientFactory httpClientFactory,
        IOptions<AppConfiguration> options,
        ILicenseService licenseService,
        TimeProvider timeProvider,
        ILogger<InstallIdentityService> logger)
    {
        _clientIdentity = clientIdentity ?? throw new ArgumentNullException(nameof(clientIdentity));
        _secureStore = secureStore ?? throw new ArgumentNullException(nameof(secureStore));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsRegistered => _isRegistered;

    /// <summary>Backoff schedule for transient registration failures. Overridable for tests.</summary>
    internal TimeSpan[] RetryDelays { get; set; } = DefaultRetryDelays;

    /// <summary>The background retry loop, when one was scheduled. Exposed for tests.</summary>
    internal Task? RetryTask { get; private set; }

    public async Task<InstallPublicKeyJwk?> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        return key is null ? null : InstallRequestSigning.ToJwk(key);
    }

    public async Task<SignedRequestHeaders?> SignAsync(
        HttpMethod method,
        Uri uri,
        byte[] body,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);

        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return null;
        }

        var installId = _clientIdentity.GetIdentity().InstallId;
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signingString = InstallRequestSigning.BuildSigningString(method, uri, timestamp, body ?? []);

        byte[] signature;
        lock (_signGate)
        {
            signature = InstallRequestSigning.Sign(key, signingString);
        }

        return new SignedRequestHeaders(installId, timestamp, InstallRequestSigning.Base64UrlEncode(signature));
    }

    public async Task EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isRegistered)
                {
                    return;
                }

                var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
                if (key is null)
                {
                    return;
                }

                var installId = _clientIdentity.GetIdentity().InstallId;
                if (!_keyGeneratedThisProcess && IsMarkedRegistered(installId))
                {
                    _isRegistered = true;
                    return;
                }

                var outcome = await RegisterWithConflictRetryAsync(cancellationToken).ConfigureAwait(false);
                if (outcome == RegistrationOutcome.Retry)
                {
                    ScheduleRetry();
                }
            }
            finally
            {
                _registerGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Caller gave up; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Install registration failed unexpectedly.");
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _key?.Dispose();
        _keyGate.Dispose();
        _registerGate.Dispose();
    }

    // ---- key management ---------------------------------------------------------------------

    private async Task<ECDsa?> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _key);
        if (cached is not null)
        {
            return cached;
        }

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _key;
            if (cached is not null)
            {
                return cached;
            }

            var key = await TryLoadKeyAsync().ConfigureAwait(false);
            if (key is null)
            {
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                _keyGeneratedThisProcess = true;
                await TryPersistKeyAsync(key).ConfigureAwait(false);
            }

            Volatile.Write(ref _key, key);
            return key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Install signing key is unavailable; requests go out unsigned.");
            return null;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private async Task<ECDsa?> TryLoadKeyAsync()
    {
        string? stored;
        try
        {
            stored = await _secureStore.GetAsync(PrivateKeyStoreKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the install signing key from secure storage.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.Trim()), out _);
            if (key.KeySize != 256)
            {
                throw new CryptographicException($"Unexpected install key size {key.KeySize}.");
            }

            return key;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored install signing key is unreadable; generating a new one.");
            key?.Dispose();
            return null;
        }
    }

    private async Task TryPersistKeyAsync(ECDsa key)
    {
        try
        {
            var pkcs8 = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            await _secureStore.SetAsync(PrivateKeyStoreKey, pkcs8).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The in-memory key still signs this session; next launch registers a fresh one.
            _logger.LogWarning(ex, "Could not persist the install signing key to secure storage.");
        }
    }

    /// <summary>Fresh install id + fresh keypair (409/401 from the backend).</summary>
    private async Task RotateIdentityAsync()
    {
        var rotated = _clientIdentity.RotateInstallId();
        _logger.LogInformation("Install identity rotated to {InstallId}.", rotated.InstallId);

        await _keyGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var old = Interlocked.Exchange(ref _key, fresh);
            old?.Dispose();
            _keyGeneratedThisProcess = true;
            await TryPersistKeyAsync(fresh).ConfigureAwait(false);
        }
        finally
        {
            _keyGate.Release();
        }

        _isRegistered = false;
        try
        {
            _preferences.Remove(RegisteredAtPreferenceKey);
            _preferences.Remove(RegisteredInstallIdPreferenceKey);
        }
        catch
        {
            // Preference storage unavailable; the next successful registration rewrites both.
        }
    }

    // ---- registration -----------------------------------------------------------------------

    private enum RegistrationOutcome
    {
        Registered,
        Conflict,
        Retry,
        Failed
    }

    private async Task<RegistrationOutcome> RegisterWithConflictRetryAsync(CancellationToken cancellationToken)
    {
        var outcome = await PostRegisterAsync(cancellationToken).ConfigureAwait(false);
        if (outcome != RegistrationOutcome.Conflict)
        {
            return outcome;
        }

        await RotateIdentityAsync().ConfigureAwait(false);
        outcome = await PostRegisterAsync(cancellationToken).ConfigureAwait(false);
        if (outcome == RegistrationOutcome.Conflict)
        {
            _logger.LogWarning("Install registration still conflicted after rotating the install id; giving up for now.");
            return RegistrationOutcome.Failed;
        }

        return outcome;
    }

    private async Task<RegistrationOutcome> PostRegisterAsync(CancellationToken cancellationToken)
    {
        var registerUri = ResolveRegisterUri();
        if (registerUri is null)
        {
            _logger.LogWarning("Install registration skipped: telemetry endpoint is not configured.");
            return RegistrationOutcome.Failed;
        }

        var key = await GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return RegistrationOutcome.Failed;
        }

        var identity = _clientIdentity.GetIdentity();
        var jwk = InstallRequestSigning.ToJwk(key);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["install_id"] = identity.InstallId,
            ["hwid"] = identity.HardwareId,
            ["public_key"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kty"] = jwk.Kty,
                ["crv"] = jwk.Crv,
                ["x"] = jwk.X,
                ["y"] = jwk.Y
            },
            ["app_version"] = SafeGetAppVersion()
        };

        var licenseKey = SafeGetLicenseKey();
        if (!string.IsNullOrWhiteSpace(licenseKey))
        {
            body["license_key"] = licenseKey;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, registerUri)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(
                _options.Value.Telemetry.RequestTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds));

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            _logger.LogInformation("Install registration for {InstallId} -> HTTP {Status}", identity.InstallId, status);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.Created:
                    MarkRegistered(identity.InstallId, await ReadRegisteredAtAsync(response, cancellationToken).ConfigureAwait(false));
                    return RegistrationOutcome.Registered;
                case HttpStatusCode.Conflict:
                case HttpStatusCode.Unauthorized:
                    _logger.LogWarning("Install {InstallId} was rejected ({Status}); rotating identity.", identity.InstallId, status);
                    return RegistrationOutcome.Conflict;
                case HttpStatusCode.TooManyRequests:
                    return RegistrationOutcome.Retry;
                default:
                    if (status >= 500)
                    {
                        return RegistrationOutcome.Retry;
                    }

                    _logger.LogWarning(
                        "Install registration failed ({Status}): {Body}",
                        status,
                        await TelemetryFormatting.SafeReadResponseAsync(response, cancellationToken).ConfigureAwait(false));
                    return RegistrationOutcome.Failed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Install registration could not reach the backend; will retry.");
            return RegistrationOutcome.Retry;
        }
    }

    private void ScheduleRetry()
    {
        if (Interlocked.Exchange(ref _retryScheduled, 1) != 0)
        {
            return;
        }

        RetryTask = Task.Run(RunRetryLoopAsync);
    }

    private async Task RunRetryLoopAsync()
    {
        var token = _lifetime.Token;
        try
        {
            foreach (var delay in RetryDelays)
            {
                await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);

                await _registerGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_isRegistered)
                    {
                        return;
                    }

                    var outcome = await RegisterWithConflictRetryAsync(token).ConfigureAwait(false);
                    if (outcome != RegistrationOutcome.Retry)
                    {
                        return;
                    }
                }
                finally
                {
                    _registerGate.Release();
                }
            }

            _logger.LogWarning("Install registration gave up after exhausting retries for this session.");
        }
        catch (OperationCanceledException)
        {
            // Service disposed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Install registration retry loop stopped unexpectedly.");
        }
        finally
        {
            Interlocked.Exchange(ref _retryScheduled, 0);
        }
    }

    private void MarkRegistered(string installId, string? registeredAt)
    {
        _isRegistered = true;
        try
        {
            _preferences.Set(RegisteredAtPreferenceKey, registeredAt ?? _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            _preferences.Set(RegisteredInstallIdPreferenceKey, installId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the install registration marker.");
        }
    }

    private bool IsMarkedRegistered(string installId)
    {
        try
        {
            var registeredId = _preferences.Get(RegisteredInstallIdPreferenceKey, string.Empty);
            var registeredAt = _preferences.Get(RegisteredAtPreferenceKey, string.Empty);
            return !string.IsNullOrWhiteSpace(registeredAt)
                && string.Equals(registeredId, installId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ReadRegisteredAtAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("registered_at", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private Uri? ResolveRegisterUri()
    {
        var endpoint = _options.Value.Telemetry.Endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new Uri(endpointUri.GetLeftPart(UriPartial.Authority) + RegisterPath);
    }

    private string? SafeGetLicenseKey()
    {
        try
        {
            var key = _licenseService.CurrentLicenseKey;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string SafeGetAppVersion()
    {
        try
        {
            var ver = AppInfo.Current.Version;
            return ver.Build > 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}";
        }
        catch
        {
            var fallback = typeof(InstallIdentityService).Assembly.GetName().Version;
            return fallback is null ? "0.0.0" : $"{fallback.Major}.{fallback.Minor}.{fallback.Build}";
        }
    }
}
