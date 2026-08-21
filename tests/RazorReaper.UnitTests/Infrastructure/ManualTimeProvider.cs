namespace RazorReaper.UnitTests.Infrastructure;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset initialTime)
    {
        _utcNow = initialTime.ToUniversalTime();
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Time cannot move backward.");
        }

        lock (_sync)
        {
            _utcNow = _utcNow.Add(amount);
        }
    }
}
