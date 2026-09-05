using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

public sealed class SupportInboxService : ISupportInboxService, IDisposable
{
    private readonly IHttpClientFactory _clients;
    private readonly IInstallIdentityService _identity;
    private readonly ICustomerAccountService _account;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? _scope;
    private int _generation;
    public IReadOnlyList<SupportReply> Replies { get; private set; } = [];
    public int UnreadCount { get; private set; }
    public long? NextBefore { get; private set; }
    public bool HasLoaded { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public SupportInboxService(IHttpClientFactory clients, IInstallIdentityService identity,
        ICustomerAccountService account, IOptions<AppConfiguration> configuration)
    {
        _clients = clients; _identity = identity; _account = account;
        _accountId = account.Profile?.Id;
        _endpoint = new Uri(configuration.Value.AdminPanel.BaseUrl.TrimEnd('/') + "/api/feedback/inbox");
        if (_endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Support inbox requires HTTPS.");
        _account.Changed += AccountChanged;
    }

    private sealed record InboxResponse(bool Ok, string? Error, string? Scope, SupportReply[]? Replies,
        int Unread, [property: JsonPropertyName("next_before")] long? NextBefore);

    private async Task<InboxResponse> RequestAsync(object body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _identity.EnsureRegisteredAsync(timeout.Token).ConfigureAwait(false);
        if (!_identity.IsRegistered) throw new InvalidOperationException("Your inbox will be available when this installation reconnects.");
        using var client = _clients.CreateClient("RazorReaperTelemetry");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<InboxResponse>(JsonOptions, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || result?.Ok != true)
            throw new InvalidOperationException(result?.Error ?? "Your inbox could not be reached.");
        return result;
    }

    public async Task RefreshAsync(bool older = false, CancellationToken cancellationToken = default)
    {
        if (!await _requests.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        var generation = _generation;
        try
        {
            var cursor = older ? NextBefore : null;
            if (older && cursor is null) return;
            object body = cursor is long before ? new { action = "list", before } : new { action = "list" };
            var result = await RequestAsync(body, cancellationToken).ConfigureAwait(false);
            if (generation != _generation) return;
            var sameScope = _scope == result.Scope;
            var existing = sameScope ? Replies : [];
            Replies = existing.Concat(result.Replies ?? []).GroupBy(reply => reply.Id)
                .Select(group => group.Last()).OrderByDescending(reply => reply.Id).ToArray();
            if (older || !HasLoaded || !sameScope) NextBefore = result.NextBefore;
            UnreadCount = result.Unread; _scope = result.Scope; HasLoaded = true; Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { if (generation == _generation) Error = "Inbox unavailable. We'll reconnect automatically."; }
        finally { _requests.Release(); Changed?.Invoke(); }
    }

    public async Task MarkReadAsync(long replyId, CancellationToken cancellationToken = default)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        var generation = _generation;
        try
        {
            await RequestAsync(new { action = "read", id = replyId }, cancellationToken).ConfigureAwait(false);
            if (generation != _generation) return;
            var unread = Replies.Any(reply => reply.Id == replyId && reply.ReadAt is null);
            Replies = Replies.Select(reply => reply.Id == replyId ? reply with { ReadAt = reply.ReadAt ?? DateTimeOffset.UtcNow } : reply).ToArray();
            if (unread) UnreadCount = Math.Max(0, UnreadCount - 1);
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { if (generation == _generation) Error = "Could not mark this answer as read. Open it again when connected."; }
        finally { _requests.Release(); Changed?.Invoke(); }
    }

    private string? _accountId;
    private void AccountChanged()
    {
        var accountId = _account.Profile?.Id;
        if (_accountId == accountId && !(_scope?.StartsWith("account:", StringComparison.Ordinal) == true && _scope != $"account:{accountId}")) return;
        _accountId = accountId;
        Interlocked.Increment(ref _generation);
        _scope = null; Replies = []; UnreadCount = 0; NextBefore = null; HasLoaded = false; Error = null;
        Changed?.Invoke();
        _ = RefreshAsync();
    }
    public void Dispose() => _account.Changed -= AccountChanged;
}
