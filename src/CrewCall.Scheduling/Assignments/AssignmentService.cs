using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Scheduling.Assignments;

/// <summary>
/// Assignment is the commit that the scheduling check is not (ADR-0008, ADR-0009). Every create and reassign runs the
/// final check and the resource claim in one database transaction: assignment, its equipment, its resource
/// reservations and the operational events are written together or not at all. The check is advice; the database
/// constraints decide races: the reservations' exclusion constraint (no double booking) and the partial unique index on
/// active assignments (one per visit). A request that loses a race gets a conflict outcome (HTTP 409), never an error.
/// </summary>
public sealed class AssignmentService(
    ISchedulingDbContext db, IVisitSchedulingSource visits, SchedulingCheckService checks, TimeProvider clock)
{
    public async Task<AssignOutcome> CreateAsync(CreateAssignment command, CancellationToken cancellationToken)
    {
        var request = new ResourceRequest(
            command.TechnicianId, command.VehicleId, command.EquipmentIds, command.RequiredSkillCodes,
            command.TravelBufferBeforeMinutes, command.TravelBufferAfterMinutes);

        if (Validate(command.VisitId, request) is { } invalid)
        {
            return invalid;
        }

        var visit = await visits.GetVisitAsync(command.VisitId, cancellationToken);
        if (visit is null)
        {
            return new AssignOutcome.VisitNotFound(command.VisitId);
        }

        if (visit.IsClosed)
        {
            return new AssignOutcome.VisitClosed(visit.VisitId, visit.State.ToString());
        }

        return await InTransactionAsync(async (transaction, token) =>
        {
            if (await ActiveAssignmentIdAsync(visit.VisitId, token) is { } activeId)
            {
                return new AssignOutcome.AlreadyAssigned(visit.VisitId, activeId);
            }

            // Final check, inside the claiming operation. It may still race with another claim; the constraints decide.
            var check = await FinalCheckAsync(visit, request, token);
            if (check is not SchedulingCheckOutcome.Checked { Result.IsFeasible: true } feasible)
            {
                return ToOutcome(check);
            }

            var assignment = NewAssignment(visit, request, feasible.Result);
            Claim(assignment);

            try
            {
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                await RollBackAsync(transaction);

                // Another request claimed a resource, or assigned this visit, between the check and the insert.
                if (await ActiveAssignmentIdAsync(visit.VisitId, token) is { } concurrentId)
                {
                    return new AssignOutcome.AlreadyAssigned(visit.VisitId, concurrentId);
                }

                if (await ExplainRejectionAsync(visit, request, token) is { } rejected)
                {
                    return rejected;
                }

                throw;
            }

            return new AssignOutcome.Assigned(assignment);
        }, cancellationToken);
    }

    /// <summary>
    /// Replaces the active assignment: the new resources are checked while ignoring this visit's own reservations (but
    /// not those of other visits); then, in one transaction, the old reservations are released, the old assignment
    /// becomes Replaced and the new assignment claims its resources. On any failure nothing changes.
    /// </summary>
    public async Task<AssignOutcome> ReassignAsync(ReassignVisit command, CancellationToken cancellationToken)
    {
        var request = new ResourceRequest(
            command.TechnicianId, command.VehicleId, command.EquipmentIds, command.RequiredSkillCodes,
            command.TravelBufferBeforeMinutes, command.TravelBufferAfterMinutes);

        if (Validate(command.VisitId, request) is { } invalid)
        {
            return invalid;
        }

        var visit = await visits.GetVisitAsync(command.VisitId, cancellationToken);
        if (visit is null)
        {
            return new AssignOutcome.VisitNotFound(command.VisitId);
        }

        if (visit.IsClosed)
        {
            return new AssignOutcome.VisitClosed(visit.VisitId, visit.State.ToString());
        }

        return await InTransactionAsync(async (transaction, token) =>
        {
            var current = await db.Assignments
                .Include(assignment => assignment.Equipment)
                .SingleOrDefaultAsync(a => a.VisitId == visit.VisitId && a.Status == AssignmentStatus.Active, token);

            if (current is null)
            {
                return new AssignOutcome.NoActiveAssignment(visit.VisitId);
            }

            var check = await FinalCheckAsync(visit, request, token);
            if (check is not SchedulingCheckOutcome.Checked { Result.IsFeasible: true } feasible)
            {
                return ToOutcome(check);
            }

            var replacement = NewAssignment(visit, request, feasible.Result);

            try
            {
                // Fixed order inside the transaction, so neither the exclusion constraint nor the one-active-per-visit
                // index trips over this visit's own rows: release, retire, then claim.
                await db.ResourceReservations
                    .Where(reservation => reservation.AssignmentId == current.Id)
                    .ExecuteDeleteAsync(token);

                current.ReplaceWith(replacement.Id, replacement.CreatedAtUtc);
                await db.SaveChangesAsync(token); // fails on a concurrent change of the old assignment (row version)

                Claim(replacement);
                db.AppendOperationalEvent(
                    AssignmentEvents.AssignmentReplaced,
                    AssignmentEvents.AssignmentAggregate,
                    current.Id,
                    replacement.CreatedAtUtc,
                    new AssignmentReplacedPayload(current.Id, replacement.Id, visit.VisitId));
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateException)
            {
                await RollBackAsync(transaction);

                var activeId = await ActiveAssignmentIdAsync(visit.VisitId, token);
                if (activeId != current.Id)
                {
                    return new AssignOutcome.ConcurrentChange(visit.VisitId);
                }

                if (await ExplainRejectionAsync(visit, request, token) is { } rejected)
                {
                    return rejected;
                }

                throw;
            }

            return new AssignOutcome.Assigned(replacement);
        }, cancellationToken);
    }

    /// <summary>
    /// Cancels the active assignment and releases its reservations; the assignment stays as Cancelled history.
    /// Idempotent: with no active assignment nothing changes (NothingToCancel).
    /// </summary>
    public async Task<CancelAssignmentOutcome> CancelAsync(Guid visitId, CancellationToken cancellationToken)
    {
        if (await visits.GetVisitAsync(visitId, cancellationToken) is null)
        {
            return new CancelAssignmentOutcome.VisitNotFound(visitId);
        }

        return await InTransactionAsync<CancelAssignmentOutcome>(async (transaction, token) =>
        {
            var current = await db.Assignments
                .Include(assignment => assignment.Equipment)
                .SingleOrDefaultAsync(a => a.VisitId == visitId && a.Status == AssignmentStatus.Active, token);

            if (current is null)
            {
                return new CancelAssignmentOutcome.NothingToCancel(visitId);
            }

            var now = StoredTime.Normalize(clock.GetUtcNow());

            try
            {
                await db.ResourceReservations
                    .Where(reservation => reservation.AssignmentId == current.Id)
                    .ExecuteDeleteAsync(token);

                current.Cancel(now);
                db.AppendOperationalEvent(
                    AssignmentEvents.AssignmentCancelled,
                    AssignmentEvents.AssignmentAggregate,
                    current.Id,
                    now,
                    new AssignmentCancelledPayload(current.Id, visitId));
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                await RollBackAsync(transaction);
                return new CancelAssignmentOutcome.ConcurrentChange(visitId);
            }

            return new CancelAssignmentOutcome.Cancelled(current);
        }, cancellationToken);
    }

    /// <summary>The visit's active assignment, if any; VisitFound is false when the visit does not exist.</summary>
    public async Task<(bool VisitFound, Assignment? Active)> GetActiveForVisitAsync(Guid visitId, CancellationToken cancellationToken)
    {
        if (await visits.GetVisitAsync(visitId, cancellationToken) is null)
        {
            return (false, null);
        }

        var active = await db.Assignments
            .AsNoTracking()
            .Include(assignment => assignment.Equipment)
            .SingleOrDefaultAsync(a => a.VisitId == visitId && a.Status == AssignmentStatus.Active, cancellationToken);

        return (true, active);
    }

    /// <summary>Every assignment the visit has had, oldest first; null when the visit does not exist.</summary>
    public async Task<IReadOnlyList<Assignment>?> GetHistoryForVisitAsync(Guid visitId, CancellationToken cancellationToken)
    {
        if (await visits.GetVisitAsync(visitId, cancellationToken) is null)
        {
            return null;
        }

        return await db.Assignments
            .AsNoTracking()
            .Include(assignment => assignment.Equipment)
            .Where(assignment => assignment.VisitId == visitId)
            .OrderBy(assignment => assignment.CreatedAtUtc)
            .ThenBy(assignment => assignment.Id)
            .ToListAsync(cancellationToken);
    }

    private sealed record ResourceRequest(
        Guid? TechnicianId,
        Guid? VehicleId,
        IReadOnlyCollection<Guid>? EquipmentIds,
        IReadOnlyCollection<string>? RequiredSkillCodes,
        int? TravelBufferBeforeMinutes,
        int? TravelBufferAfterMinutes);

    /// <summary>
    /// What only an assignment rejects up front; everything else (ids, buffers) is validated by the scheduling check.
    /// Unlike the advisory check, an assignment does not silently merge duplicate equipment ids.
    /// </summary>
    private static AssignOutcome.Invalid? Validate(Guid visitId, ResourceRequest request)
    {
        var errors = new ValidationErrors();
        if (visitId == Guid.Empty)
        {
            errors.Add("visitId", "Required.");
        }

        if (request.EquipmentIds is { } equipmentIds && equipmentIds.Distinct().Count() != equipmentIds.Count)
        {
            errors.Add("equipmentIds", "Must not contain duplicates.");
        }

        return errors.Any ? new AssignOutcome.Invalid(errors.ToDictionary()) : null;
    }

    private Task<SchedulingCheckOutcome> FinalCheckAsync(VisitSchedulingInfo visit, ResourceRequest request, CancellationToken token) =>
        // VisitId: this visit's own (active) reservations are ignored; other visits' reservations are not.
        checks.CheckAsync(
            new SchedulingCheck(
                visit.VisitId,
                request.TechnicianId,
                request.VehicleId,
                request.EquipmentIds,
                visit.Start,
                visit.End,
                request.RequiredSkillCodes,
                request.TravelBufferBeforeMinutes,
                request.TravelBufferAfterMinutes),
            token);

    private static AssignOutcome ToOutcome(SchedulingCheckOutcome check) => check switch
    {
        SchedulingCheckOutcome.Invalid invalid => new AssignOutcome.Invalid(invalid.Errors),
        SchedulingCheckOutcome.ResourcesNotFound notFound => new AssignOutcome.ResourcesNotFound(notFound.Missing),
        SchedulingCheckOutcome.Checked rejected => new AssignOutcome.Rejected(rejected.Result.Reasons),
        _ => throw new InvalidOperationException($"Unhandled check outcome {check}.")
    };

    /// <summary>After a failed claim: the check again, now seeing whatever won the race.</summary>
    private async Task<AssignOutcome.Rejected?> ExplainRejectionAsync(VisitSchedulingInfo visit, ResourceRequest request, CancellationToken token) =>
        await FinalCheckAsync(visit, request, token) is SchedulingCheckOutcome.Checked { Result.IsFeasible: false } recheck
            ? new AssignOutcome.Rejected(recheck.Result.Reasons)
            : null;

    private Assignment NewAssignment(VisitSchedulingInfo visit, ResourceRequest request, SchedulingCheckResult checkedResult) =>
        new(
            Guid.CreateVersion7(),
            visit.VisitId,
            checkedResult.TechnicianId,
            checkedResult.VehicleId,
            checkedResult.EquipmentIds,
            request.TravelBufferBeforeMinutes ?? 0,
            request.TravelBufferAfterMinutes ?? 0,
            checkedResult.EffectiveStart,
            checkedResult.EffectiveEnd,
            StoredTime.Normalize(clock.GetUtcNow()));

    /// <summary>
    /// Adds the assignment, one reservation per resource over the buffered window (the same window the check used), and
    /// the AssignmentCreated event to the unit of work. Nothing is written until SaveChanges.
    /// </summary>
    private void Claim(Assignment assignment)
    {
        db.Assignments.Add(assignment);

        db.ResourceReservations.Add(Reservation(assignment, ResourceType.Technician, assignment.TechnicianId));
        if (assignment.VehicleId is { } vehicleId)
        {
            db.ResourceReservations.Add(Reservation(assignment, ResourceType.Vehicle, vehicleId));
        }

        foreach (var equipment in assignment.Equipment)
        {
            db.ResourceReservations.Add(Reservation(assignment, ResourceType.Equipment, equipment.EquipmentId));
        }

        db.AppendOperationalEvent(
            AssignmentEvents.AssignmentCreated,
            AssignmentEvents.AssignmentAggregate,
            assignment.Id,
            assignment.CreatedAtUtc,
            new AssignmentCreatedPayload(
                assignment.Id,
                assignment.VisitId,
                assignment.TechnicianId,
                assignment.VehicleId,
                assignment.Equipment.Select(equipment => equipment.EquipmentId).ToList(),
                assignment.TravelBufferBeforeMinutes,
                assignment.TravelBufferAfterMinutes,
                assignment.ClaimedStart,
                assignment.ClaimedEnd));
    }

    private static ResourceReservation Reservation(Assignment assignment, ResourceType type, Guid resourceId) =>
        new(Guid.CreateVersion7(), type, resourceId, assignment.VisitId, assignment.ClaimedStart, assignment.ClaimedEnd, assignment.Id);

    private Task<Guid?> ActiveAssignmentIdAsync(Guid visitId, CancellationToken token) =>
        db.Assignments
            .Where(assignment => assignment.VisitId == visitId && assignment.Status == AssignmentStatus.Active)
            .Select(assignment => (Guid?)assignment.Id)
            .SingleOrDefaultAsync(token);

    /// <summary>
    /// Runs <paramref name="operation"/> in one database transaction under the context's execution strategy (which may
    /// retry the whole operation on a transient failure, so it starts from a clean change tracker each time).
    /// </summary>
    private Task<TOutcome> InTransactionAsync<TOutcome>(
        Func<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction, CancellationToken, Task<TOutcome>> operation,
        CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                return await operation(transaction, token);
            },
            cancellationToken);

    private async Task RollBackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }
}
