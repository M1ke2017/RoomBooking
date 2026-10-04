namespace CrewCall.Workforce.WorkingHours;

/// <summary>
/// One weekly working-hours range: on <see cref="DayOfWeek"/>, from <see cref="StartLocalTime"/> to
/// <see cref="EndLocalTime"/> in the technician's local time zone (wall-clock time, not UTC). A day can have several
/// ranges (e.g. a split shift); ranges of the same technician and day never overlap, but may touch.
/// </summary>
/// <remarks>
/// Half-open [start, end). An end of 00:00 means the end of the day (24:00), so a range can run up to midnight. A night
/// shift that crosses midnight is two ranges: e.g. Monday 22:00–24:00 and Tuesday 00:00–06:00; touching ranges are
/// treated as one continuous working period.
/// </remarks>
public sealed class TechnicianWorkingHours
{
    internal TechnicianWorkingHours(Guid id, Guid technicianId, DayOfWeek dayOfWeek, TimeOnly startLocalTime, TimeOnly endLocalTime)
    {
        Id = id;
        TechnicianId = technicianId;
        DayOfWeek = dayOfWeek;
        StartLocalTime = startLocalTime;
        EndLocalTime = endLocalTime;
    }

    public Guid Id { get; private set; }

    public Guid TechnicianId { get; private set; }

    public DayOfWeek DayOfWeek { get; private set; }

    public TimeOnly StartLocalTime { get; private set; }

    /// <summary>00:00 means the end of the day (24:00).</summary>
    public TimeOnly EndLocalTime { get; private set; }

    public bool EndsAtEndOfDay => EndLocalTime == TimeOnly.MinValue;

    /// <summary>Whether [start, end) overlaps this range on the same day. Ranges that only touch do not overlap.</summary>
    public bool Overlaps(TimeOnly startLocalTime, TimeOnly endLocalTime) =>
        StartLocalTime.ToTimeSpan() < EndOffset(endLocalTime) && startLocalTime.ToTimeSpan() < EndOffset(EndLocalTime);

    /// <summary>The end as an offset from the start of the day: 00:00 becomes 24:00.</summary>
    internal static TimeSpan EndOffset(TimeOnly endLocalTime) =>
        endLocalTime == TimeOnly.MinValue ? TimeSpan.FromDays(1) : endLocalTime.ToTimeSpan();
}
