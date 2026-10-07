namespace CrewCall.WorkOrders.Tests;

/// <summary>A clock the test sets and advances: deterministic timestamps for field work.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan duration) => _now += duration;
}

/// <summary>
/// A clock that, once armed, holds its next <c>callers</c> readers until all of them have read it. Field work reads the
/// clock after loading the execution and before saving, so armed concurrent operations have all loaded the same state
/// before any of them saves: the database guards, not timing, must then decide.
/// </summary>
public sealed class BarrierClock(DateTimeOffset now) : TimeProvider
{
    private ManualResetEventSlim? _open;
    private int _remaining;

    public void Arm(int callers)
    {
        _remaining = callers;
        _open = new ManualResetEventSlim(false);
    }

    public override DateTimeOffset GetUtcNow()
    {
        if (_open is { IsSet: false } open)
        {
            if (Interlocked.Decrement(ref _remaining) <= 0)
            {
                open.Set();
            }
            else if (!open.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The other operations never read the clock.");
            }
        }

        return now;
    }
}
