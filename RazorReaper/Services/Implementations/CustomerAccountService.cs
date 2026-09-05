using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

/// <summary>Accounts use the install's existing protected P-256 key. No password or Discord token is stored in the app.</summary>
public sealed class CustomerAccountService : ICustomerAccountService
{
    private readonly IHttpClientFactory _clients;
    private readonly IInstallIdentityService _identity;
    private readonly Uri _baseUri;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public CustomerAccountProfile? Profile { get; private set; }
    public IReadOnlyList<CustomerAccountDevice> Devices { get; private set; } = [];
    public event Action? Changed;

    public CustomerAccountService(IHttpClientFactory clients, IInstallIdentityService identity, IOptions<AppConfiguration> configuration)
    {
        _clients = clients;
        _identity = identity;
        _baseUri = new Uri(configuration.Value.AdminPanel.BaseUrl.TrimEnd('/') + "/");
        if (_baseUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Account sign-in requires HTTPS.");
    }
    private sealed record Reply(bool Ok, string? Error, string? Code, string? Status, CustomerAccountProfile? Account,
        CustomerAccountDevice[]? Devices, string? RequestId, string? UserCode, string? VerificationUrl, int ExpiresIn);

    private async Task<Reply> RequestAsync(HttpMethod method, string action, object? body, CancellationToken cancellationToken)
    {
        await _identity.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        if (!_identity.IsRegistered) throw new InvalidOperationException("Could not verify this installation. Please try again when connected.");
        using var client = _clients.CreateClient("RazorReaperTelemetry");
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, "api/discord/account/" + action));
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        Reply? reply;
        try { reply = await response.Content.ReadFromJsonAsync<Reply>(JsonOptions, timeout.Token).ConfigureAwait(false); }
        catch (JsonException) { throw new InvalidOperationException("The account service could not be reached. Please try again."); }
        if (response.StatusCode == HttpStatusCode.Forbidden && reply?.Code == "account_signed_out")
        {
            Profile = null; Devices = []; Changed?.Invoke();
            if (method == HttpMethod.Get || method == HttpMethod.Delete) return reply;
        }
        if (!response.IsSuccessStatusCode || reply?.Ok != true)
            throw new InvalidOperationException(reply?.Error ?? "The account service could not complete this request.");
        return reply;
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(HttpMethod.Get, "me", null, cancellationToken).ConfigureAwait(false);
        Profile = reply.Account; Devices = reply.Devices ?? []; Changed?.Invoke();
    }
    public async Task<CustomerAccountLogin> BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(HttpMethod.Post, "start", new { }, cancellationToken).ConfigureAwait(false);
        if (reply.RequestId is null || reply.UserCode is null || !IsValidVerificationUrl(reply.VerificationUrl))
            throw new InvalidOperationException("The sign-in link was invalid. Please try again.");
        return new(reply.RequestId, reply.UserCode, reply.VerificationUrl!, Math.Clamp(reply.ExpiresIn, 1, 600));
    }
    internal bool IsValidVerificationUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Authority == _baseUri.Authority
        && uri.AbsolutePath == "/api/discord/account/authorize" && string.IsNullOrEmpty(uri.UserInfo);
    public async Task<CustomerAccountProfile?> PollSignInAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(HttpMethod.Post, "poll", new { requestId }, cancellationToken).ConfigureAwait(false);
        return reply.Status == "confirm" ? reply.Account : null;
    }
    public async Task ConfirmSignInAsync(string requestId, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = await RequestAsync(HttpMethod.Post, "confirm", new { requestId }, cancellationToken).ConfigureAwait(false);
            if (reply.Account is null) throw new InvalidOperationException("Complete Discord sign-in in the browser first.");
            Profile = reply.Account; Changed?.Invoke();
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _mutations.Release(); }
    }
    public async Task SaveProfileAsync(string displayName, string? avatar, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = await RequestAsync(HttpMethod.Put, "me", new { displayName, avatar }, cancellationToken).ConfigureAwait(false);
            Profile = reply.Account; Devices = reply.Devices ?? []; Changed?.Invoke();
        }
        finally { _mutations.Release(); }
    }
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequestAsync(HttpMethod.Delete, "me", null, cancellationToken).ConfigureAwait(false);
            Profile = null; Devices = []; Changed?.Invoke();
        }
        finally { _mutations.Release(); }
    }
}
