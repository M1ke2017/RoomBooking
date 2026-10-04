using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce.WorkingHours;

public sealed class WorkingHoursService(IWorkforceDbContext db)
{
    public async Task<CreateWorkingHoursOutcome> CreateAsync(CreateWorkingHours command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var dayOfWeek = errors.EnumValue<DayOfWeek>("dayOfWeek", command.DayOfWeek);
        var start = LocalTimeText.Parse(errors, "startLocalTime", command.StartLocalTime, isEnd: false);
        var end = LocalTimeText.Parse(errors, "endLocalTime", command.EndLocalTime, isEnd: true);

        // [start, end) must be a positive range within one day; an end of 00:00 is 24:00, so 00:00–00:00 is the whole day.
        if (start is not null && end is not null && start.Value.ToTimeSpan() >= TechnicianWorkingHours.EndOffset(end.Value))
        {
            errors.Add("endLocalTime", "Must be after startLocalTime. Split a shift that crosses midnight into two ranges.");
        }

        if (errors.Any)
        {
            return new CreateWorkingHoursOutcome.Invalid(errors.ToDictionary());
        }

        if (!await db.Technicians.AnyAsync(technician => technician.Id == command.TechnicianId, cancellationToken))
        {
            return new CreateWorkingHoursOutcome.TechnicianNotFound(command.TechnicianId);
        }

        if (await FindOverlapAsync(command.TechnicianId, dayOfWeek!.Value, start!.Value, end!.Value, cancellationToken) is { } existingId)
        {
            return new CreateWorkingHoursOutcome.Overlaps(existingId);
        }

        var workingHours = new TechnicianWorkingHours(Guid.CreateVersion7(), command.TechnicianId, dayOfWeek.Value, start.Value, end.Value);
        db.TechnicianWorkingHours.Add(workingHours);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have added an overlapping range between the check and the insert; the exclusion
            // constraint then rejected this one. Anything else is a genuine failure.
            db.TechnicianWorkingHours.Entry(workingHours).State = EntityState.Detached;
            if (await FindOverlapAsync(command.TechnicianId, dayOfWeek.Value, start.Value, end.Value, cancellationToken) is { } concurrentId)
            {
                return new CreateWorkingHoursOutcome.Overlaps(concurrentId);
            }

            throw;
        }

        return new CreateWorkingHoursOutcome.Created(workingHours);
    }

    /// <summary>Returns the technician's ranges, Monday first, or null when the technician does not exist.</summary>
    public async Task<IReadOnlyList<TechnicianWorkingHours>?> ListAsync(Guid technicianId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return null;
        }

        var ranges = await db.TechnicianWorkingHours
            .AsNoTracking()
            .Where(range => range.TechnicianId == technicianId)
            .ToListAsync(cancellationToken);

        return ranges
            .OrderBy(range => IsoDayNumber(range.DayOfWeek))
            .ThenBy(range => range.StartLocalTime)
            .ToList();
    }

    public async Task<DeleteWorkingHoursOutcome> DeleteAsync(Guid technicianId, Guid workingHoursId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return new DeleteWorkingHoursOutcome.TechnicianNotFound(technicianId);
        }

        await db.TechnicianWorkingHours
            .Where(range => range.Id == workingHoursId && range.TechnicianId == technicianId)
            .ExecuteDeleteAsync(cancellationToken);

        return new DeleteWorkingHoursOutcome.Deleted();
    }

    private async Task<Guid?> FindOverlapAsync(
        Guid technicianId, DayOfWeek dayOfWeek, TimeOnly start, TimeOnly end, CancellationToken cancellationToken)
    {
        // A handful of ranges per day: compare in memory, where the "00:00 = 24:00" end rule is one helper.
        var sameDay = await db.TechnicianWorkingHours
            .AsNoTracking()
            .Where(range => range.TechnicianId == technicianId && range.DayOfWeek == dayOfWeek)
            .ToListAsync(cancellationToken);

        return sameDay.FirstOrDefault(range => range.Overlaps(start, end))?.Id;
    }

    private static int IsoDayNumber(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;
}
