namespace RazorReaper.UnitTests.Infrastructure;

/// <summary>Hands out HttpClients over one shared handler; remembers which names were requested.</summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpMessageHandler _handler;
    private readonly List<string> _names = [];
    private readonly List<HttpClient> _clients = [];

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public IReadOnlyList<string> RequestedNames
    {
        get
        {
            lock (_names)
            {
                return _names.ToArray();
            }
        }
    }

    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(_handler, disposeHandler: false);
        lock (_names)
        {
            _names.Add(name);
            _clients.Add(client);
        }

        return client;
    }

    public void Dispose()
    {
        lock (_names)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }
}
