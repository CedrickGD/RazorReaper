using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;
using RazorReaper.Services.Implementations;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Identity;

public sealed class SupportInboxTests
{
    private const string List = """{"ok":true,"scope":"account:a","unread":1,"next_before":null,"replies":[{"id":7,"feedback_id":2,"message":"Try repair","original_message":"Startup fails","report_id":"FB-000002","created_at":"2026-09-05T12:00:00Z","read_at":null}]}""";
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static SupportInboxService Create(FakeHttpClientFactory factory, Account account, Identity? identity = null) => new(factory, identity ?? new(), account, Options.Create(new AppConfiguration()));

    [Fact]
    public async Task LoadingDoesNotMarkRepliesReadAndOpeningUpdatesTheCountOnce()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json(List)) };
        using var factory = new FakeHttpClientFactory(handler);
        using var service = Create(factory, new Account());
        await service.RefreshAsync();
        Assert.Equal(1, service.UnreadCount); Assert.Null(Assert.Single(service.Replies).ReadAt);
        Assert.Equal("Startup fails", service.Replies[0].OriginalMessage);
        Assert.All(factory.RequestedNames, name => Assert.Equal("RazorReaperTelemetry", name));
        Assert.Contains("\"action\":\"list\"", Assert.Single(handler.Requests).Body);
        handler.ResponseFactory = (_, _) => Task.FromResult(Json("""{"ok":true}"""));
        await service.MarkReadAsync(7); await service.MarkReadAsync(7);
        Assert.Equal(0, service.UnreadCount); Assert.NotNull(service.Replies[0].ReadAt);
        Assert.Contains("\"id\":7", handler.Requests[1].Body);
    }
    [Fact]
    public async Task FailedReadsAndOfflinePollingPreserveUnreadReplies()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json(List)) };
        using var factory = new FakeHttpClientFactory(handler);
        using var service = Create(factory, new Account());
        await service.RefreshAsync();
        handler.ResponseFactory = (_, _) => Task.FromResult(Json("""{"ok":false}""", HttpStatusCode.ServiceUnavailable));
        await service.MarkReadAsync(7); await service.RefreshAsync();
        Assert.Equal(1, service.UnreadCount); Assert.Null(Assert.Single(service.Replies).ReadAt); Assert.NotNull(service.Error);
    }
    [Fact]
    public async Task AccountSwitchClearsCachedRepliesAndDiscardsAnInflightResponse()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json(List)) };
        using var factory = new FakeHttpClientFactory(handler);
        var account = new Account();
        using var service = Create(factory, account);
        await service.RefreshAsync();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ResponseFactory = (_, _) => pending.Task;
        var refresh = service.RefreshAsync();
        account.SignOut();
        Assert.Empty(service.Replies); Assert.Equal(0, service.UnreadCount);
        pending.SetResult(Json(List)); await refresh;
        Assert.Empty(service.Replies); Assert.False(service.HasLoaded);
    }
    [Fact]
    public async Task ChangedServerScopeNeverMergesAnotherAccountsHistory()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json(List)) };
        using var factory = new FakeHttpClientFactory(handler);
        using var service = Create(factory, new Account());
        await service.RefreshAsync();
        handler.ResponseFactory = (_, _) => Task.FromResult(Json("""{"ok":true,"scope":"account:b","unread":0,"replies":[],"next_before":null}"""));
        await service.RefreshAsync();
        Assert.Empty(service.Replies); Assert.Equal(0, service.UnreadCount);
    }
    [Fact]
    public async Task PaginationKeepsOlderRepliesWithoutDuplicates()
    {
        using var handler = new RecordingHttpMessageHandler { ResponseFactory = (_, _) => Task.FromResult(Json(List.Replace("\"next_before\":null", "\"next_before\":7"))) };
        using var factory = new FakeHttpClientFactory(handler);
        using var service = Create(factory, new Account());
        await service.RefreshAsync();
        handler.ResponseFactory = (_, _) => Task.FromResult(Json(List.Replace("\"id\":7", "\"id\":6")));
        await service.RefreshAsync(older: true);
        Assert.Contains("\"before\":7", handler.Requests.Last().Body);
        Assert.Equal(2, service.Replies.Count); Assert.Null(service.NextBefore);
        handler.ResponseFactory = (_, _) => Task.FromResult(Json(List));
        await service.RefreshAsync();
        Assert.Equal(2, service.Replies.Count);
    }
    [Fact]
    public async Task UnregisteredInstallDoesNotSendAnInboxRequest()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var factory = new FakeHttpClientFactory(handler);
        using var service = Create(factory, new Account(), new Identity { IsRegistered = false });
        await service.RefreshAsync();
        Assert.Empty(handler.Requests); Assert.NotNull(service.Error);
    }
    private sealed class Identity : IInstallIdentityService
    {
        public bool IsRegistered { get; set; } = true;
        public Task EnsureRegisteredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<InstallPublicKeyJwk?> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<InstallPublicKeyJwk?>(null);
        public Task<SignedRequestHeaders?> SignAsync(HttpMethod method, Uri uri, byte[] body, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult<SignedRequestHeaders?>(null);
        public void ReportSignedRequestRejected(Uri uri, SignedRequestHeaders rejectedHeaders) { }
    }
    private sealed class Account : ICustomerAccountService
    {
        public CustomerAccountProfile? Profile { get; private set; } = new("a", "Example", "1", "example", null);
        public IReadOnlyList<CustomerAccountDevice> Devices => [];
        public event Action? Changed;
        public void SignOut() { Profile = null; Changed?.Invoke(); }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CustomerAccountLogin> BeginSignInAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomerAccountProfile?> PollSignInAsync(string requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfirmSignInAsync(string requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveProfileAsync(string displayName, string? avatar, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) { SignOut(); return Task.CompletedTask; }
    }
}
