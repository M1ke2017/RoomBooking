namespace Daylo.Core;

/// <summary>
/// The team's working day. Times are stored in UTC; the day and the clock on the screens are the team's local time
/// (Europe/Warsaw).
/// </summary>
public static class WorkDay
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(Local(now).DateTime);

    /// <summary>The instant of a local time on a local day.</summary>
    public static DateTimeOffset At(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The day as [start, end) in UTC.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly day) => (At(day, 0), At(day.AddDays(1), 0));

    public static DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);
}
