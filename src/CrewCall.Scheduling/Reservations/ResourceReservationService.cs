using CrewCall.Scheduling.Core;
using CrewCall.Scheduling.Ports;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Scheduling.Reservations;

/// <summary>
/// Creates, removes and queries resource reservations. Sprint 6B uses it for test fixtures and conflict queries only:
/// the scheduling check never writes reservations. Assignments (Sprint 7) will claim resources through it.
/// </summary>
public sealed class ResourceReservationService(
    ISchedulingDbContext db, ITechnicianSchedulingSource technicians, IResourceCatalog resources)
{
    public async Task<CreateReservationOutcome> CreateAsync(CreateReservation command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var resourceType = errors.EnumValue<ResourceType>("resourceType", command.ResourceType);

        if (command.ResourceId == Guid.Empty)
        {
            errors.Add("resourceId", "Required.");
        }

        if (command.VisitId == Guid.Empty)
        {
            errors.Add("visitId", "Must not be empty when provided.");
        }

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
        if (start is not null && end is not null && start >= end)
        {
            errors.Add("end", "Must be after start.");
        }

        if (errors.Any)
        {
            return new CreateReservationOutcome.Invalid(errors.ToDictionary());
        }

        if (!await ResourceExistsAsync(resourceType!.Value, command.ResourceId, cancellationToken))
        {
            return new CreateReservationOutcome.ResourceNotFound(resourceType.Value, command.ResourceId);
        }

        if (await FindOverlapAsync(resourceType.Value, command.ResourceId, start!.Value, end!.Value, cancellationToken) is { } existingId)
        {
            return new CreateReservationOutcome.Overlaps(existingId);
        }

        var reservation = new ResourceReservation(
            Guid.CreateVersion7(), resourceType.Value, command.ResourceId, command.VisitId, start.Value, end.Value);
        db.ResourceReservations.Add(reservation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request reserved an overlapping period between the check and the insert; the exclusion
            // constraint rejected this one. Anything else is a genuine failure.
            db.ResourceReservations.Entry(reservation).State = EntityState.Detached;
            if (await FindOverlapAsync(resourceType.Value, command.ResourceId, start.Value, end.Value, cancellationToken) is { } concurrentId)
            {
                return new CreateReservationOutcome.Overlaps(concurrentId);
            }

            throw;
        }

        return new CreateReservationOutcome.Created(reservation);
    }

    /// <summary>Idempotent: removing a reservation that does not exist changes nothing.</summary>
    public Task RemoveAsync(Guid reservationId, CancellationToken cancellationToken) =>
        db.ResourceReservations.Where(reservation => reservation.Id == reservationId).ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// The reservations of <paramref name="resources"/> that clash with a visit occupying <paramref name="effectiveRange"/>
    /// (the visit plus its travel buffer). Reservations of <paramref name="ignoredVisitId"/> are skipped. The rule itself
    /// (overlap, touching, travel buffer, self-exclusion, ordering) is the pure ResourceConflicts.detect.
    /// </summary>
    public async Task<IReadOnlyList<ReservationConflict>> GetConflictsAsync(
        IReadOnlyCollection<(ResourceType Type, Guid ResourceId)> resources,
        TimeRange visit,
        TimeRange effectiveRange,
        Guid? ignoredVisitId,
        CancellationToken cancellationToken)
    {
        if (resources.Count == 0)
        {
            return [];
        }

        var resourceIds = resources.Select(resource => resource.ResourceId).Distinct().ToList();
        var effectiveStart = effectiveRange.Start;
        var effectiveEnd = effectiveRange.End;

        // Narrow in SQL to overlapping reservations of the requested resource ids; the exact decision is made in F#.
        var candidates = await db.ResourceReservations
            .AsNoTracking()
            .Where(reservation => resourceIds.Contains(reservation.ResourceId)
                && reservation.Start < effectiveEnd && effectiveStart < reservation.End)
            .ToListAsync(cancellationToken);

        var byId = candidates.ToDictionary(reservation => reservation.Id);
        var conflicts = ResourceConflicts.detect(
            resources.Select(resource => CoreMapping.ToKey(resource.Type, resource.ResourceId)),
            visit,
            effectiveRange,
            CoreMapping.ToOption(ignoredVisitId),
            candidates.Select(CoreMapping.ToBooking));

        return conflicts
            .Select(conflict => conflict switch
            {
                ResourceConflict.BookingOverlap overlap => new ReservationConflict(byId[overlap.Item.BookingId], WithinTravelBuffer: false),
                ResourceConflict.TravelBufferOverlap buffer => new ReservationConflict(byId[buffer.Item.BookingId], WithinTravelBuffer: true),
                _ => throw new InvalidOperationException($"Unhandled conflict {conflict}.")
            })
            .ToList();
    }

    private Task<bool> ResourceExistsAsync(ResourceType type, Guid resourceId, CancellationToken cancellationToken) => type switch
    {
        ResourceType.Technician => technicians.ExistsAsync(resourceId, cancellationToken),
        ResourceType.Vehicle => resources.VehicleExistsAsync(resourceId, cancellationToken),
        ResourceType.Equipment => ExistsAsEquipmentAsync(resourceId, cancellationToken),
        _ => Task.FromResult(false)
    };

    private async Task<bool> ExistsAsEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        (await resources.FindMissingEquipmentAsync([equipmentId], cancellationToken)).Count == 0;

    // Half-open: [a, b) and [b, c) do not overlap.
    private Task<Guid?> FindOverlapAsync(
        ResourceType type, Guid resourceId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        db.ResourceReservations
            .Where(reservation => reservation.ResourceType == type && reservation.ResourceId == resourceId
                && reservation.Start < end && start < reservation.End)
            .OrderBy(reservation => reservation.Start)
            .Select(reservation => (Guid?)reservation.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
