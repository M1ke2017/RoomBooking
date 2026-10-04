using CrewCall.Workforce.Absences;
using CrewCall.Workforce.Holidays;
using CrewCall.Workforce.WorkingHours;
using NodaTime;
using NodaTime.TimeZones;
using NodaTime.Utility;

namespace CrewCall.Workforce.Availability;

/// <summary>
/// The pure availability decision: no I/O, every input passed in. The interval is half-open [start, end) of instants;
/// working hours and holidays are local to the technician's time zone and are converted to instants with the zone's
/// rules for each concrete date, so DST changes are applied on the days they happen.
/// </summary>
public static class AvailabilityRules
{
    /// <summary>
    /// Maps a local wall-clock time to the first instant at which the technician's clock shows it:
    /// in a DST gap (spring forward, the time does not exist) the instant the clock jumps past it, e.g. 02:30 on a
    /// 02:00 → 03:00 night resolves to 03:00 summer time; in a DST overlap (fall back, the time occurs twice) the earlier
    /// occurrence. One rule for range starts and ends keeps touching ranges touching after conversion.
    /// </summary>
    public static readonly ZoneLocalMappingResolver WallClockResolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnStartOfIntervalAfter);

    /// <summary>The local dates [start, end) touches in the zone: from the date of start to the date of the last instant before end.</summary>
    public static (LocalDate First, LocalDate Last) LocalDates(DateTimeZone zone, Instant start, Instant end) =>
        (start.InZone(zone).Date, (end - Duration.Epsilon).InZone(zone).Date);

    public static AvailabilityDecision Evaluate(
        bool isActive,
        DateTimeZone zone,
        Instant start,
        Instant end,
        IEnumerable<HolidayCalendarEntry> holidays,
        IEnumerable<TechnicianAbsence> absences,
        IEnumerable<TechnicianWorkingHours> workingHours)
    {
        if (start >= end)
        {
            throw new ArgumentException("The interval must have start before end.", nameof(end));
        }

        if (!isActive)
        {
            return new AvailabilityDecision(AvailabilityStatus.UnavailableInactiveTechnician, null);
        }

        var (firstDate, lastDate) = LocalDates(zone, start, end);

        // Holidays are whole local days: any part of the interval on a holiday makes it unavailable.
        // Only national entries apply (technicians have no region yet).
        var holiday = holidays
            .Where(entry => entry.RegionCode is null)
            .Select(entry => (Entry: entry, Date: LocalDate.FromDateOnly(entry.Date)))
            .Where(entry => entry.Date >= firstDate && entry.Date <= lastDate)
            .OrderBy(entry => entry.Date)
            .Select(entry => entry.Entry)
            .FirstOrDefault();
        if (holiday is not null)
        {
            return new AvailabilityDecision(AvailabilityStatus.UnavailableHoliday, $"{holiday.Date:yyyy-MM-dd} {holiday.Name}");
        }

        // Absences are instants: unavailable when one overlaps [start, end). Touching ([a, b) then [b, c)) does not.
        var absence = absences
            .Where(entry => Instant.FromDateTimeOffset(entry.Start) < end && start < Instant.FromDateTimeOffset(entry.End))
            .OrderBy(entry => entry.Start)
            .FirstOrDefault();
        if (absence is not null)
        {
            return new AvailabilityDecision(AvailabilityStatus.UnavailableAbsence, absence.Type.ToString());
        }

        // The whole interval must lie inside one continuous working period (touching ranges join, e.g. across midnight).
        var covered = WorkingPeriods(zone, firstDate, lastDate, workingHours)
            .Any(period => period.Start <= start && end <= period.End);

        return covered
            ? new AvailabilityDecision(AvailabilityStatus.Available, null)
            : new AvailabilityDecision(AvailabilityStatus.UnavailableOutsideWorkingHours, null);
    }

    /// <summary>
    /// The weekly ranges made concrete for each local date in [firstDate, lastDate], converted to instants in the zone,
    /// sorted, with overlapping or touching periods merged.
    /// </summary>
    public static IReadOnlyList<Interval> WorkingPeriods(
        DateTimeZone zone, LocalDate firstDate, LocalDate lastDate, IEnumerable<TechnicianWorkingHours> workingHours)
    {
        var ranges = workingHours.ToList();
        var periods = new List<Interval>();

        for (var date = firstDate; date <= lastDate; date = date.PlusDays(1))
        {
            var dayOfWeek = BclConversions.ToDayOfWeek(date.DayOfWeek);
            foreach (var range in ranges.Where(range => range.DayOfWeek == dayOfWeek))
            {
                var startLocal = date + LocalTime.FromTimeOnly(range.StartLocalTime);
                var endLocal = range.EndsAtEndOfDay
                    ? date.PlusDays(1).AtMidnight()
                    : date + LocalTime.FromTimeOnly(range.EndLocalTime);

                var startInstant = zone.ResolveLocal(startLocal, WallClockResolver).ToInstant();
                var endInstant = zone.ResolveLocal(endLocal, WallClockResolver).ToInstant();

                // A range lying entirely inside a DST gap (e.g. 02:15–02:45 on a spring-forward night) has no real time.
                if (startInstant < endInstant)
                {
                    periods.Add(new Interval(startInstant, endInstant));
                }
            }
        }

        periods.Sort((left, right) => left.Start.CompareTo(right.Start));

        var merged = new List<Interval>();
        foreach (var period in periods)
        {
            if (merged.Count > 0 && period.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = new Interval(last.Start, Instant.Max(last.End, period.End));
            }
            else
            {
                merged.Add(period);
            }
        }

        return merged;
    }
}
