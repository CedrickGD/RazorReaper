namespace RazorReaper.UnitTests.Infrastructure;

public sealed class FakeOsLocationProvider
{
    private readonly List<CancellationToken> _calls = [];

    public object? Result { get; set; }

    public IReadOnlyList<CancellationToken> Calls => _calls.ToArray();

    public ValueTask<object?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(cancellationToken);
        return ValueTask.FromResult(Result);
    }
}
