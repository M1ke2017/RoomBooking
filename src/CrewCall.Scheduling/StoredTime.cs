namespace CrewCall.Scheduling;

/// <summary>
/// Instants are stored as PostgreSQL timestamptz: UTC with microsecond precision. Normalizing before validation and
/// storage means the value the module checks is exactly the value the database keeps.
/// </summary>
internal static class StoredTime
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), TimeSpan.Zero);
    }
}
