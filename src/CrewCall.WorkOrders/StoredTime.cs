namespace CrewCall.WorkOrders;

/// <summary>
/// PostgreSQL timestamps have microsecond precision, .NET ticks are 100 ns. Values are truncated to microseconds and
/// converted to UTC before storage, so what the API returns after a write equals what a later read returns.
/// </summary>
internal static class StoredTime
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), TimeSpan.Zero);
    }

    public static DateTimeOffset UtcNow(TimeProvider clock) => Normalize(clock.GetUtcNow());
}
