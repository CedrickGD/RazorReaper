using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Telemetry;

public sealed class HttpTelemetryClient : ITelemetryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppConfiguration _configuration;
    private readonly ILogger<HttpTelemetryClient> _logger;
    private readonly string _telemetryFolder;
    private readonly string _queuePath;
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private enum SendOutcome
    {
        Success,
        TransientFailure,
        PermanentFailure
    }

    public HttpTelemetryClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AppConfiguration> configuration,
        ILogger<HttpTelemetryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration.Value;
        _logger = logger;
        _telemetryFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper",
            "Telemetry");
        _queuePath = Path.Combine(_telemetryFolder, "telemetry_retry_queue.json");
    }

    public async Task<bool> SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration.Telemetry.Endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var appKey = _configuration.Telemetry.AppKey?.Trim();

        await DrainRetryQueueAsync(endpoint, appKey, cancellationToken).ConfigureAwait(false);

        var sendOutcome = await SendCoreAsync(telemetryEvent, endpoint, appKey, cancellationToken).ConfigureAwait(false);
        if (sendOutcome == SendOutcome.TransientFailure)
        {
            await EnqueueRetryEventAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
        }

        return sendOutcome == SendOutcome.Success;
    }

    private async Task<SendOutcome> SendCoreAsync(
        TelemetryEvent telemetryEvent,
        string endpoint,
        string? appKey,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(_configuration.Telemetry.RequestTimeoutSeconds, 1, 15);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(telemetryEvent)
            };

            if (!string.IsNullOrWhiteSpace(appKey))
            {
                request.Headers.TryAddWithoutValidation("X-App-Key", appKey);
            }

            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return SendOutcome.Success;
            }

            var statusCode = (int)response.StatusCode;
            if (statusCode is 408 or 429 || statusCode >= 500)
            {
                return SendOutcome.TransientFailure;
            }

            return SendOutcome.PermanentFailure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Telemetry request timed out for {EventName}.", telemetryEvent.EventName);
            return SendOutcome.TransientFailure;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry request failed for {EventName}.", telemetryEvent.EventName);
            return SendOutcome.TransientFailure;
        }
    }

    private async Task DrainRetryQueueAsync(string endpoint, string? appKey, CancellationToken cancellationToken)
    {
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await ReadQueueUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (queue.Count == 0)
            {
                return;
            }

            var maxBatchSize = Math.Clamp(_configuration.Telemetry.RetryBatchSize, 1, 100);
            var batchCount = Math.Min(queue.Count, maxBatchSize);
            var processedCount = 0;

            for (var index = 0; index < batchCount; index++)
            {
                var queuedEvent = queue[index];
                var sendOutcome = await SendCoreAsync(queuedEvent.Event, endpoint, appKey, cancellationToken).ConfigureAwait(false);
                if (sendOutcome == SendOutcome.TransientFailure)
                {
                    break;
                }

                processedCount++;
            }

            if (processedCount > 0)
            {
                queue.RemoveRange(0, processedCount);
                await WriteQueueUnsafeAsync(queue, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to drain telemetry retry queue.");
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private async Task EnqueueRetryEventAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken)
    {
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await ReadQueueUnsafeAsync(cancellationToken).ConfigureAwait(false);
            queue.Add(new QueuedTelemetryEvent
            {
                Event = telemetryEvent,
                QueuedAtUtc = DateTimeOffset.UtcNow
            });

            var maxItems = Math.Clamp(_configuration.Telemetry.RetryQueueMaxItems, 10, 5000);
            if (queue.Count > maxItems)
            {
                var removeCount = queue.Count - maxItems;
                queue.RemoveRange(0, removeCount);
            }

            await WriteQueueUnsafeAsync(queue, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to queue telemetry event {EventName} for retry.", telemetryEvent.EventName);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private async Task<List<QueuedTelemetryEvent>> ReadQueueUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_queuePath))
        {
            return new List<QueuedTelemetryEvent>();
        }

        try
        {
            var rawJson = await File.ReadAllTextAsync(_queuePath, cancellationToken).ConfigureAwait(false);
            var queue = JsonSerializer.Deserialize<List<QueuedTelemetryEvent>>(rawJson, JsonOptions);
            return queue ?? new List<QueuedTelemetryEvent>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse telemetry retry queue from {Path}.", _queuePath);
            return new List<QueuedTelemetryEvent>();
        }
    }

    private async Task WriteQueueUnsafeAsync(List<QueuedTelemetryEvent> queue, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_telemetryFolder);
        var tempPath = $"{_queuePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(queue, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _queuePath, true);
    }

    private sealed class QueuedTelemetryEvent
    {
        public TelemetryEvent Event { get; set; } = new();
        public DateTimeOffset QueuedAtUtc { get; set; }
    }
}
