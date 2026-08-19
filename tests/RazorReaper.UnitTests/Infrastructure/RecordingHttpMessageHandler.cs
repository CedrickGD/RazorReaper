using System.Collections.Concurrent;
using System.Net;

namespace RazorReaper.UnitTests.Infrastructure;

public sealed record RecordedHttpRequest(HttpMethod Method, Uri? Uri, string? Body);

public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedHttpRequest> _requests = new();

    public IReadOnlyList<RecordedHttpRequest> Requests => _requests.ToArray();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ResponseFactory { get; set; }
        = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(new RecordedHttpRequest(request.Method, request.RequestUri, body));

        return await ResponseFactory(request, cancellationToken).ConfigureAwait(false);
    }
}
