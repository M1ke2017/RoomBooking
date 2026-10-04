using CrewCall.Workforce.TimeZones;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CrewCall.Workforce.Availability;

/// <summary>
/// Answers "is this technician available for [start, end)?" from Workforce data only: technician status, the
/// national holiday calendar, absences and weekly working hours. Visits, assignments, vehicles and equipment are not
/// considered. Loads only the rows relevant to the interval and leaves the decision to <see cref="AvailabilityRules"/>.
/// </summary>
public sealed class WorkforceAvailabilityService(IWorkforceDbContext db)
{
    /// <summary>Upper bound on one query, so a request cannot make the service expand years of weekly ranges.</summary>
    public static readonly TimeSpan MaxIntervalLength = TimeSpan.FromDays(31);

    public async Task<CheckAvailabilityOutcome> CheckAsync(CheckAvailability command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        if (command.Start is null)
        {
            errors.Add("start", "Required.");
        }

        if (command.End is null)
        {
            errors.Add("end", "Required.");
        }

        DateTimeOffset? start = command.Start is { } requestedStart ? StoredTime.Normalize(requestedStart) : null;
        DateTimeOffset? end = command.End is { } requestedEnd ? StoredTime.Normalize(requestedEnd) : null;

        if (start is not null && end is not null)
        {
            if (start >= end)
            {
                errors.Add("end", "Must be after start.");
            }
            else if (end - start > MaxIntervalLength)
            {
                errors.Add("end", $"The interval can be at most {MaxIntervalLength.TotalDays:0} days long.");
            }
        }

        if (errors.Any)
        {
            return new CheckAvailabilityOutcome.Invalid(errors.ToDictionary());
        }

        // 1. The technician exists.
        var technician = await db.Technicians
            .AsNoTracking()
            .Where(t => t.Id == command.TechnicianId)
            .Select(t => new { t.IsActive, t.TimeZoneId, t.CountryCode })
            .SingleOrDefaultAsync(cancellationToken);

        if (technician is null)
        {
            return new CheckAvailabilityOutcome.TechnicianNotFound(command.TechnicianId);
        }

        // 2.-3. The interval in the technician's zone: which local dates it touches.
        var zone = TechnicianTimeContext.GetZone(technician.TimeZoneId);
        var startInstant = Instant.FromDateTimeOffset(start!.Value);
        var endInstant = Instant.FromDateTimeOffset(end!.Value);
        var (firstDate, lastDate) = AvailabilityRules.LocalDates(zone, startInstant, endInstant);

        AvailabilityDecision decision;
        if (!technician.IsActive)
        {
            // 2. Inactive: nothing else is looked up.
            decision = AvailabilityRules.Evaluate(false, zone, startInstant, endInstant, [], [], []);
        }
        else
        {
            var firstDay = firstDate.ToDateOnly();
            var lastDay = lastDate.ToDateOnly();
            var holidays = await db.HolidayCalendar
                .AsNoTracking()
                .Where(holiday => holiday.CountryCode == technician.CountryCode && holiday.RegionCode == null
                    && holiday.Date >= firstDay && holiday.Date <= lastDay)
                .ToListAsync(cancellationToken);

            var absences = await db.TechnicianAbsences
                .AsNoTracking()
                .Where(absence => absence.TechnicianId == command.TechnicianId && absence.Start < end && start < absence.End)
                .ToListAsync(cancellationToken);

            var workingHours = await db.TechnicianWorkingHours
                .AsNoTracking()
                .Where(range => range.TechnicianId == command.TechnicianId)
                .ToListAsync(cancellationToken);

            // 4.-6. Holiday, then absence, then working-hours coverage.
            decision = AvailabilityRules.Evaluate(true, zone, startInstant, endInstant, holidays, absences, workingHours);
        }

        return new CheckAvailabilityOutcome.Evaluated(new TechnicianAvailability(
            command.TechnicianId,
            start.Value,
            end.Value,
            technician.TimeZoneId,
            startInstant.InZone(zone).ToDateTimeOffset(),
            endInstant.InZone(zone).ToDateTimeOffset(),
            decision));
    }
}
