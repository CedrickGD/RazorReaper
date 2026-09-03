using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Diagnostics;

/// <summary>
/// Runs every approved provider concurrently. A slow or broken feature becomes one unavailable
/// row instead of blocking the report, and all output is normalized to the backend's hard bounds.
/// </summary>
public sealed class DiagnosticSnapshotService : IDiagnosticSnapshotService
{
    public const int MaxSerializedBytes = 12 * 1024;
    public const int MaxProviders = 12;
    public const int MaxChecksPerProvider = 32;

    public static readonly IReadOnlyList<string> RequiredProviderIds =
    [
        "app_runtime",
        "windows_host",
        "identity_license_access",
        "ark_environment",
        "core_features",
        "ark_tweaks",
        "custom_ark",
        "automation",
        "mods_intel",
        "utilities",
        "help_support",
        "settings_operations",
    ];

    private readonly IReadOnlyDictionary<string, IDiagnosticProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiagnosticSnapshotService> _logger;
    private readonly TimeSpan _providerTimeout;

    public DiagnosticSnapshotService(
        IEnumerable<IDiagnosticProvider> providers,
        TimeProvider timeProvider,
        ILogger<DiagnosticSnapshotService> logger)
        : this(providers, timeProvider, logger, TimeSpan.FromMilliseconds(1500))
    {
    }

    internal DiagnosticSnapshotService(
        IEnumerable<IDiagnosticProvider> providers,
        TimeProvider timeProvider,
        ILogger<DiagnosticSnapshotService> logger,
        TimeSpan providerTimeout)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _providerTimeout = providerTimeout > TimeSpan.Zero
            ? providerTimeout
            : throw new ArgumentOutOfRangeException(nameof(providerTimeout));

        // Duplicate registrations cannot produce duplicate backend rows. Keep the first and log
        // the wiring error; RequiredProviderIds below still guarantees an exact 12-row envelope.
        _providers = providers
            .Where(p => p is not null && RequiredProviderIds.Contains(p.ProviderId, StringComparer.Ordinal))
            .GroupBy(p => p.ProviderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public async Task<FeedbackDiagnostics> CaptureAsync(
        string? sourceRoute,
        CancellationToken cancellationToken = default)
    {
        var generatedAt = _timeProvider.GetUtcNow();
        var context = new DiagnosticCaptureContext(NormalizeSource(sourceRoute));
        var tasks = RequiredProviderIds.Select(id => CaptureOneAsync(id, context, cancellationToken));
        var reports = await Task.WhenAll(tasks).ConfigureAwait(false);

        var snapshot = new FeedbackDiagnostics
        {
            GeneratedAt = generatedAt,
            Providers = reports,
        };

        return FitPayloadBudget(snapshot);
    }

    private async Task<DiagnosticProviderReport> CaptureOneAsync(
        string providerId,
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            return Unavailable(providerId, 0, "Collector is not registered.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCts.CancelAfter(_providerTimeout);
        try
        {
            // Task.Run isolates a provider that does synchronous work before its first await.
            var captureTask = Task.Run(
                () => provider.CaptureAsync(context, providerCts.Token),
                CancellationToken.None);
            var data = await captureTask
                .WaitAsync(_providerTimeout, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();
            return Normalize(providerId, data, stopwatch.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            _logger.LogWarning("Diagnostic provider {Provider} timed out after {TimeoutMs} ms.",
                providerId, (int)_providerTimeout.TotalMilliseconds);
            return Unavailable(providerId, stopwatch.ElapsedMilliseconds, "Collector timed out.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning("Diagnostic provider {Provider} timed out after {TimeoutMs} ms.",
                providerId, (int)_providerTimeout.TotalMilliseconds);
            return Unavailable(providerId, stopwatch.ElapsedMilliseconds, "Collector timed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Diagnostic provider {Provider} failed.", providerId);
            return new DiagnosticProviderReport
            {
                Provider = providerId,
                Version = "1",
                Status = "error",
                DurationMs = ClampDuration(stopwatch.ElapsedMilliseconds),
                Summary = "Collector failed; other diagnostics were still captured.",
                Checks = [],
            };
        }
    }

    private static DiagnosticProviderReport Normalize(
        string providerId,
        DiagnosticProviderData? data,
        long elapsedMilliseconds)
    {
        if (data is null)
        {
            return Unavailable(providerId, elapsedMilliseconds, "Collector returned no data.");
        }

        var checks = (data.Checks ?? [])
            .Take(MaxChecksPerProvider)
            .Select(NormalizeCheck)
            .ToArray();

        return new DiagnosticProviderReport
        {
            Provider = Limit(providerId, 64),
            Version = LimitNullable(data.Version, 64),
            Status = NormalizeProviderStatus(data.Status),
            DurationMs = ClampDuration(elapsedMilliseconds),
            Summary = LimitNullable(data.Summary, 500),
            Checks = checks,
        };
    }

    private static DiagnosticCheck NormalizeCheck(DiagnosticCheck check)
    {
        var value = check.Value switch
        {
            null => null,
            string text => Limit(text, 256),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal => check.Value,
            _ => Limit(check.Value.ToString() ?? string.Empty, 256),
        };

        return new DiagnosticCheck
        {
            Key = Limit(check.Key, 64),
            Label = Limit(check.Label, 120),
            Status = NormalizeCheckStatus(check.Status),
            Value = value,
            Detail = LimitNullable(check.Detail, 500),
        };
    }

    private static FeedbackDiagnostics FitPayloadBudget(FeedbackDiagnostics snapshot)
    {
        if (SerializedSize(snapshot) <= MaxSerializedBytes)
        {
            return snapshot;
        }

        // Coverage keys/labels/status are load-bearing. First shed prose, then string values;
        // never remove a provider or route/script check just to meet the transport budget.
        var compact = CompactForTransport(snapshot);
        if (SerializedSize(compact) <= MaxSerializedBytes)
        {
            return compact;
        }

        // This should be unreachable with the fixed 12-provider manifest. Failing closed keeps
        // the legacy feedback request usable instead of producing a body the backend will reject.
        throw new InvalidOperationException("The diagnostic coverage manifest exceeds the 12 KiB transport limit.");
    }

    internal static FeedbackDiagnostics CompactForTransport(FeedbackDiagnostics snapshot)
    {
        var compactProviders = snapshot.Providers.Select(provider => provider with
        {
            Version = null,
            Summary = null,
            Checks = provider.Checks.Select(check => check with
            {
                Detail = null,
                Value = check.Value is string ? null : check.Value,
            }).ToArray(),
        }).ToArray();

        return snapshot with { Providers = compactProviders };
    }

    internal static int SerializedSize(FeedbackDiagnostics snapshot)
        => JsonSerializer.SerializeToUtf8Bytes(snapshot).Length;

    private static DiagnosticProviderReport Unavailable(string providerId, long elapsedMs, string summary)
        => new()
        {
            Provider = providerId,
            Version = "1",
            Status = "unavailable",
            DurationMs = ClampDuration(elapsedMs),
            Summary = summary,
            Checks = [],
        };

    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "feedback";
        var normalized = source.Split('?', '#')[0].Trim().Trim('/').ToLowerInvariant();
        return Limit(normalized.Length == 0 ? "feedback" : normalized, 64);
    }

    private static string NormalizeProviderStatus(string? status)
        => status is "ok" or "warning" or "error" or "unavailable" ? status : "unavailable";

    private static string NormalizeCheckStatus(string? status)
        => status is "pass" or "warning" or "fail" or "unknown" ? status : "unknown";

    private static int ClampDuration(long durationMs)
        => (int)Math.Clamp(durationMs, 0, 120_000);

    private static string Limit(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? LimitNullable(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : Limit(value.Trim(), maxLength);
}
