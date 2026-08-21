using System.Net;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests;

public sealed class SmokeTests
{
    [Fact]
    public void AppAssemblyLoads()
    {
        Assert.Equal("RazorReaper", typeof(MauiProgram).Assembly.GetName().Name);
    }
}

public sealed class InfrastructureTests
{
    [Fact]
    public void PreferencesGetReturnsStoredValue()
    {
        var store = new FakePreferencesStore();

        store.Set("enabled", true);

        Assert.True(Assert.IsType<bool>(store.Get("enabled")));
    }

    [Fact]
    public void PreferencesGetReturnsProvidedDefaultForMissingKey()
    {
        var store = new FakePreferencesStore();

        Assert.Equal("fallback", store.Get("missing", "fallback"));
    }

    [Fact]
    public void PreferencesRemoveDeletesOnlyExistingValue()
    {
        var store = new FakePreferencesStore();
        store.Set("enabled", true);

        Assert.True(store.Remove("enabled"));
        Assert.Null(store.Get("enabled"));
        Assert.False(store.Remove("enabled"));
    }

    [Fact]
    public void PreferencesClearDeletesAllValues()
    {
        var store = new FakePreferencesStore();
        store.Set("first", 1);
        store.Set("second", 2);

        store.Clear();

        Assert.Null(store.Get("first"));
        Assert.Null(store.Get("second"));
    }

    [Fact]
    public async Task OsLocationProviderReturnsProgrammedResult()
    {
        var expected = new object();
        var provider = new FakeOsLocationProvider { Result = expected };

        var actual = await provider.GetAsync();

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task OsLocationProviderRecordsEachCallToken()
    {
        using var source = new CancellationTokenSource();
        var provider = new FakeOsLocationProvider();

        await provider.GetAsync(source.Token);

        Assert.Equal(source.Token, Assert.Single(provider.Calls));
    }

    [Fact]
    public async Task HttpHandlerRecordsStableRequestSnapshot()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/privacy")
        {
            Content = new StringContent("payload"),
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal(new Uri("https://example.invalid/privacy"), recorded.Uri);
        Assert.Equal("payload", recorded.Body);
    }

    [Fact]
    public async Task HttpHandlerReturnsProgrammedResponse()
    {
        using var handler = new RecordingHttpMessageHandler
        {
            ResponseFactory = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/privacy");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task HttpHandlerRejectsPreCanceledRequestWithoutRecordingIt()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var handler = new RecordingHttpMessageHandler();
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/privacy");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.SendAsync(request, source.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ManualClockNormalizesInitialValueToUtc()
    {
        var localOffset = new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(2));

        var timeProvider = new ManualTimeProvider(localOffset);

        Assert.Equal(new DateTimeOffset(2026, 8, 16, 18, 0, 0, TimeSpan.Zero), timeProvider.GetUtcNow());
    }

    [Fact]
    public void ManualClockAdvanceMovesUtcClockForward()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 16, 18, 0, 0, TimeSpan.Zero));

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 16, 18, 5, 0, TimeSpan.Zero),
            timeProvider.GetUtcNow());
    }

    [Fact]
    public void ManualClockRejectsBackwardAdvance()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 16, 18, 0, 0, TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => timeProvider.Advance(TimeSpan.FromTicks(-1)));
    }
}
