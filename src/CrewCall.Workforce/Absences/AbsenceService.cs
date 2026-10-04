using Microsoft.EntityFrameworkCore;

namespace CrewCall.Workforce.Absences;

public sealed class AbsenceService(IWorkforceDbContext db)
{
    public async Task<CreateAbsenceOutcome> CreateAsync(CreateAbsence command, CancellationToken cancellationToken)
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

        // Validate the normalized values (UTC, microseconds), so the stored interval is never empty.
        DateTimeOffset? start = command.Start is { } requestedStart ? StoredTime.Normalize(requestedStart) : null;
        DateTimeOffset? end = command.End is { } requestedEnd ? StoredTime.Normalize(requestedEnd) : null;

        if (start is not null && end is not null && start >= end)
        {
            errors.Add("end", "Must be after start.");
        }

        var type = errors.EnumValue<AbsenceType>("type", command.Type);
        var reason = errors.Optional("reason", command.Reason, TechnicianAbsence.ReasonMaxLength);

        if (errors.Any)
        {
            return new CreateAbsenceOutcome.Invalid(errors.ToDictionary());
        }

        if (!await db.Technicians.AnyAsync(technician => technician.Id == command.TechnicianId, cancellationToken))
        {
            return new CreateAbsenceOutcome.TechnicianNotFound(command.TechnicianId);
        }

        if (await FindOverlapAsync(command.TechnicianId, start!.Value, end!.Value, cancellationToken) is { } existingId)
        {
            return new CreateAbsenceOutcome.Overlaps(existingId);
        }

        var absence = new TechnicianAbsence(Guid.CreateVersion7(), command.TechnicianId, start.Value, end.Value, type!.Value, reason);
        db.TechnicianAbsences.Add(absence);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request may have added an overlapping absence between the check and the insert; the exclusion
            // constraint then rejected this one. Anything else is a genuine failure.
            db.TechnicianAbsences.Entry(absence).State = EntityState.Detached;
            if (await FindOverlapAsync(command.TechnicianId, start.Value, end.Value, cancellationToken) is { } concurrentId)
            {
                return new CreateAbsenceOutcome.Overlaps(concurrentId);
            }

            throw;
        }

        return new CreateAbsenceOutcome.Created(absence);
    }

    /// <summary>Returns the technician's absences ordered by start, or null when the technician does not exist.</summary>
    public async Task<IReadOnlyList<TechnicianAbsence>?> ListAsync(Guid technicianId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return null;
        }

        return await db.TechnicianAbsences
            .AsNoTracking()
            .Where(absence => absence.TechnicianId == technicianId)
            .OrderBy(absence => absence.Start)
            .ThenBy(absence => absence.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<DeleteAbsenceOutcome> DeleteAsync(Guid technicianId, Guid absenceId, CancellationToken cancellationToken)
    {
        if (!await db.Technicians.AnyAsync(technician => technician.Id == technicianId, cancellationToken))
        {
            return new DeleteAbsenceOutcome.TechnicianNotFound(technicianId);
        }

        await db.TechnicianAbsences
            .Where(absence => absence.Id == absenceId && absence.TechnicianId == technicianId)
            .ExecuteDeleteAsync(cancellationToken);

        return new DeleteAbsenceOutcome.Deleted();
    }

    // Half-open intervals overlap when each starts before the other ends; touching ([a, b) and [b, c)) does not.
    private Task<Guid?> FindOverlapAsync(Guid technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        db.TechnicianAbsences
            .Where(absence => absence.TechnicianId == technicianId && absence.Start < end && start < absence.End)
            .OrderBy(absence => absence.Start)
            .Select(absence => (Guid?)absence.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
