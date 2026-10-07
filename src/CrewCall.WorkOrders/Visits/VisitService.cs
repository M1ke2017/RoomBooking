using CrewCall.WorkOrders.Operations;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.WorkOrders.Visits;

public sealed class VisitService(IWorkOrdersDbContext db, TimeProvider clock)
{
    public async Task<CreateVisitOutcome> CreateAsync(CreateVisit command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        if (command.WorkOrderId == Guid.Empty)
        {
            errors.Add("workOrderId", "Required.");
        }

        if (command.Start is null)
        {
            errors.Add("start", "Required.");
        }

        if (command.End is null)
        {
            errors.Add("end", "Required.");
        }

        // Stored as instants (timestamptz): normalized to UTC (microsecond precision). The caller's offset is not kept.
        // Validate the normalized values, so values differing only below a microsecond cannot become an empty interval.
        DateTimeOffset? start = command.Start is { } requestedStart ? StoredTime.Normalize(requestedStart) : null;
        DateTimeOffset? end = command.End is { } requestedEnd ? StoredTime.Normalize(requestedEnd) : null;

        // Half-open interval [Start, End): a visit must last a positive amount of time.
        if (start is not null && end is not null && start >= end)
        {
            errors.Add("end", "Must be after start.");
        }

        var notes = errors.Optional("notes", command.Notes, Visit.NotesMaxLength);

        if (errors.Any)
        {
            return new CreateVisitOutcome.Invalid(errors.ToDictionary());
        }

        var workOrderStatus = await db.WorkOrders
            .Where(workOrder => workOrder.Id == command.WorkOrderId)
            .Select(workOrder => (WorkOrderStatus?)workOrder.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (workOrderStatus is null)
        {
            return new CreateVisitOutcome.WorkOrderNotFound(command.WorkOrderId);
        }

        if (WorkOrderLifecycle.IsTerminal(workOrderStatus.Value))
        {
            return new CreateVisitOutcome.WorkOrderClosed(command.WorkOrderId, workOrderStatus.Value);
        }

        var visit = Add(db, Guid.CreateVersion7(), command.WorkOrderId, start!.Value, end!.Value, notes, StoredTime.UtcNow(clock));

        // One SaveChanges: the visit and its event are written in a single transaction.
        await db.SaveChangesAsync(cancellationToken);

        return new CreateVisitOutcome.Created(visit);
    }

    /// <summary>
    /// Adds a validated, Planned visit and its VisitCreated event to the unit of work; nothing is written until SaveChanges.
    /// The one creation path, also used when an incident is dispatched.
    /// </summary>
    internal static Visit Add(
        IWorkOrdersDbContext db, Guid visitId, Guid workOrderId, DateTimeOffset start, DateTimeOffset end, string? notes, DateTimeOffset now)
    {
        var visit = new Visit(visitId, workOrderId, start, end, notes, now);

        db.Visits.Add(visit);
        db.AppendOperationalEvent(
            WorkOrderEvents.VisitCreated,
            WorkOrderEvents.VisitAggregate,
            visit.Id,
            now,
            new VisitCreatedPayload(visit.Id, visit.WorkOrderId, visit.Start, visit.End));

        return visit;
    }

    /// <summary>
    /// Moves the (tracked) visit to <paramref name="target"/> when the lifecycle allows it and adds the VisitStatusChanged
    /// event to the unit of work; otherwise changes nothing and returns false. Nothing is saved. The one status-change
    /// path, also used when field work starts or completes (ADR-0013).
    /// </summary>
    internal static bool TryChangeStatus(IWorkOrdersDbContext db, Visit visit, VisitStatus target, DateTimeOffset now)
    {
        var oldStatus = visit.Status;
        if (!visit.TryTransitionTo(target))
        {
            return false;
        }

        db.AppendOperationalEvent(
            WorkOrderEvents.VisitStatusChanged,
            WorkOrderEvents.VisitAggregate,
            visit.Id,
            now,
            new VisitStatusChangedPayload(visit.Id, oldStatus, visit.Status));
        return true;
    }

    /// <summary>Returns the work order's visits ordered by start, or null when the work order does not exist.</summary>
    public async Task<IReadOnlyList<Visit>?> ListForWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        if (!await db.WorkOrders.AnyAsync(workOrder => workOrder.Id == workOrderId, cancellationToken))
        {
            return null;
        }

        return await db.Visits
            .AsNoTracking()
            .Where(visit => visit.WorkOrderId == workOrderId)
            .OrderBy(visit => visit.Start)
            .ThenBy(visit => visit.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<Visit?> GetAsync(Guid visitId, CancellationToken cancellationToken) =>
        db.Visits.AsNoTracking().SingleOrDefaultAsync(visit => visit.Id == visitId, cancellationToken);

    public async Task<ChangeVisitStatusOutcome> ChangeStatusAsync(ChangeVisitStatus command, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var target = errors.EnumValue<VisitStatus>("status", command.Status);

        if (errors.Any)
        {
            return new ChangeVisitStatusOutcome.Invalid(errors.ToDictionary());
        }

        var visit = await db.Visits.SingleOrDefaultAsync(v => v.Id == command.VisitId, cancellationToken);
        if (visit is null)
        {
            return new ChangeVisitStatusOutcome.NotFound(command.VisitId);
        }

        var oldStatus = visit.Status;
        if (!TryChangeStatus(db, visit, target!.Value, StoredTime.UtcNow(clock)))
        {
            return new ChangeVisitStatusOutcome.TransitionNotAllowed(oldStatus, target.Value);
        }

        try
        {
            // One SaveChanges: the new status and its event are written in a single transaction.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Visits carry a row version: a concurrent change won; neither status nor event was saved.
            return new ChangeVisitStatusOutcome.ConcurrentChange(visit.Id);
        }

        return new ChangeVisitStatusOutcome.Changed(visit, oldStatus);
    }
}
