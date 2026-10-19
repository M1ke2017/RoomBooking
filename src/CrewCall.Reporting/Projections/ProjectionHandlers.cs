using CrewCall.Contracts.Integration;
using CrewCall.Reporting.Data;

namespace CrewCall.Reporting.Projections;

/// <summary>
/// One explicit handler per projected event (ADR-0016). A handler only changes detail rows (visits, assignments,
/// incidents); the daily technician rows are recomputed from them afterwards. Every handler is written so that the same
/// set of events gives the same rows whatever order they arrive in.
/// </summary>
public interface IProjectionHandler<in TEvent> where TEvent : IIntegrationEvent
{
    string Name { get; }

    Task ApplyAsync(TEvent integrationEvent, ProjectionContext context, CancellationToken cancellationToken);
}

/// <summary>A visit's planned window by time: the latest business change wins, whatever order its event arrived in.</summary>
internal static class VisitPlanRules
{
    public static void Apply(VisitActivity visit, DateTimeOffset start, DateTimeOffset end, DateTimeOffset at)
    {
        if (visit.PlannedChangedAtUtc is { } current && at < current)
        {
            return;
        }

        visit.PlannedStartUtc = start.ToUniversalTime();
        visit.PlannedEndUtc = end.ToUniversalTime();
        visit.PlannedChangedAtUtc = at.ToUniversalTime();
    }
}

/// <summary>Visit statuses by time: the latest business change wins, whatever order its event arrived in.</summary>
internal static class VisitStatusRules
{
    public const string Planned = "Planned";
    public const string Completed = "Completed";

    public static void Apply(VisitActivity visit, string status, DateTimeOffset at)
    {
        var newer = visit.StatusChangedAtUtc is not { } current
            || at > current
            // Several changes at the same instant (e.g. completion): the one further along wins, then by name.
            || (at == current && (Rank(status), status).CompareTo((Rank(visit.VisitStatus), visit.VisitStatus)) > 0);
        if (newer)
        {
            visit.VisitStatus = status;
            visit.StatusChangedAtUtc = at;
        }
    }

    private static int Rank(string status) => status switch
    {
        "Planned" => 1,
        "InProgress" => 2,
        "Completed" or "Cancelled" => 3,
        _ => 0
    };
}

public sealed class AssignmentCreatedProjectionHandler : IProjectionHandler<AssignmentCreatedIntegrationEvent>
{
    public string Name => "AssignmentCreated";

    public async Task ApplyAsync(AssignmentCreatedIntegrationEvent created, ProjectionContext context, CancellationToken cancellationToken)
    {
        // The row may already exist from an earlier assignment.replaced/cancelled or incident.dispatched: fill it in,
        // keeping how it ended.
        var assignment = await context.AssignmentAsync(created.AssignmentId, created.VisitId, cancellationToken);
        assignment.TechnicianId = created.TechnicianId;
        assignment.VehicleId = created.VehicleId;
        assignment.CreatedAtUtc = created.OccurredAtUtc;
        assignment.UpdatedAtUtc = context.Now;

        await context.RefreshVisitAttributionAsync(created.VisitId, cancellationToken);
    }
}

public sealed class AssignmentReplacedProjectionHandler : IProjectionHandler<AssignmentReplacedIntegrationEvent>
{
    public string Name => "AssignmentReplaced";

    public async Task ApplyAsync(AssignmentReplacedIntegrationEvent replaced, ProjectionContext context, CancellationToken cancellationToken)
    {
        // History is kept: the old assignment stays, as Replaced, pointing at its successor.
        var old = await context.AssignmentAsync(replaced.OldAssignmentId, replaced.VisitId, cancellationToken);
        old.Status = AssignmentActivityStatus.Replaced;
        old.EndedAtUtc = replaced.OccurredAtUtc;
        old.ReplacedByAssignmentId = replaced.NewAssignmentId;
        old.UpdatedAtUtc = context.Now;

        // The new one is Active; its technician comes with its own assignment.created, before or after this.
        var successor = await context.AssignmentAsync(replaced.NewAssignmentId, replaced.VisitId, cancellationToken);
        successor.UpdatedAtUtc = context.Now;

        await context.RefreshVisitAttributionAsync(replaced.VisitId, cancellationToken);
    }
}

public sealed class AssignmentCancelledProjectionHandler : IProjectionHandler<AssignmentCancelledIntegrationEvent>
{
    public string Name => "AssignmentCancelled";

    public async Task ApplyAsync(AssignmentCancelledIntegrationEvent cancelled, ProjectionContext context, CancellationToken cancellationToken)
    {
        var assignment = await context.AssignmentAsync(cancelled.AssignmentId, cancelled.VisitId, cancellationToken);
        if (assignment.Status != AssignmentActivityStatus.Replaced)
        {
            assignment.Status = AssignmentActivityStatus.Cancelled;
            assignment.EndedAtUtc = cancelled.OccurredAtUtc;
        }

        assignment.UpdatedAtUtc = context.Now;
        await context.RefreshVisitAttributionAsync(cancelled.VisitId, cancellationToken);
    }
}

public sealed class IncidentDispatchedProjectionHandler : IProjectionHandler<IncidentDispatchedIntegrationEvent>
{
    public string Name => "IncidentDispatched";

    public async Task ApplyAsync(IncidentDispatchedIntegrationEvent dispatched, ProjectionContext context, CancellationToken cancellationToken)
    {
        var incident = await context.IncidentAsync(dispatched.IncidentId, cancellationToken);
        incident.WorkOrderId = dispatched.WorkOrderId;
        incident.VisitId = dispatched.VisitId;
        incident.AssignmentId = dispatched.AssignmentId;
        incident.TechnicianId = dispatched.TechnicianId;
        incident.DispatchedAtUtc = dispatched.OccurredAtUtc;
        incident.UpdatedAtUtc = context.Now;

        // The dispatch names the assignment's technician: fill in what assignment.created has not told yet (it wins
        // when it arrives, so the result does not depend on the order).
        var assignment = await context.AssignmentAsync(dispatched.AssignmentId, dispatched.VisitId, cancellationToken);
        assignment.TechnicianId ??= dispatched.TechnicianId;
        assignment.UpdatedAtUtc = context.Now;

        var visit = await context.VisitAsync(dispatched.VisitId, cancellationToken);
        visit.WorkOrderId ??= dispatched.WorkOrderId;
        visit.UpdatedAtUtc = context.Now;

        await context.RefreshVisitAttributionAsync(dispatched.VisitId, cancellationToken);
    }
}

public sealed class VisitCreatedProjectionHandler : IProjectionHandler<VisitCreatedIntegrationEvent>
{
    public string Name => "VisitCreated";

    public async Task ApplyAsync(VisitCreatedIntegrationEvent created, ProjectionContext context, CancellationToken cancellationToken)
    {
        var visit = await context.VisitAsync(created.VisitId, cancellationToken);
        visit.WorkOrderId = created.WorkOrderId;
        visit.CustomerId = created.CustomerId;
        visit.SiteId = created.SiteId;
        visit.WorkOrderPriority = created.WorkOrderPriority;
        VisitPlanRules.Apply(visit, created.PlannedStartUtc, created.PlannedEndUtc, created.OccurredAtUtc);
        VisitStatusRules.Apply(visit, VisitStatusRules.Planned, created.OccurredAtUtc);
        visit.UpdatedAtUtc = context.Now;

        // Assignment events may have arrived before the visit itself.
        await context.RefreshVisitAttributionAsync(created.VisitId, cancellationToken);
    }
}

public sealed class VisitStatusChangedProjectionHandler : IProjectionHandler<VisitStatusChangedIntegrationEvent>
{
    public string Name => "VisitStatusChanged";

    public async Task ApplyAsync(VisitStatusChangedIntegrationEvent changed, ProjectionContext context, CancellationToken cancellationToken)
    {
        var visit = await context.VisitAsync(changed.VisitId, cancellationToken);
        VisitStatusRules.Apply(visit, changed.NewStatus, changed.OccurredAtUtc);
        visit.UpdatedAtUtc = context.Now;

        await context.RefreshVisitAttributionAsync(changed.VisitId, cancellationToken);
    }
}

public sealed class VisitRescheduledProjectionHandler : IProjectionHandler<VisitRescheduledIntegrationEvent>
{
    public string Name => "VisitRescheduled";

    public async Task ApplyAsync(VisitRescheduledIntegrationEvent rescheduled, ProjectionContext context, CancellationToken cancellationToken)
    {
        // Counted, not set: each move is its own message, and the inbox (same transaction) makes a redelivery a
        // duplicate, so a move is never counted twice. Sums do not depend on the order the moves arrive in.
        var visit = await context.VisitAsync(rescheduled.VisitId, cancellationToken);
        visit.RescheduleCount++;
        visit.TotalDelayMinutes += (int)Math.Round((rescheduled.NewStartUtc - rescheduled.OldStartUtc).TotalMinutes);
        VisitPlanRules.Apply(visit, rescheduled.NewStartUtc, rescheduled.NewEndUtc, rescheduled.OccurredAtUtc);
        visit.UpdatedAtUtc = context.Now;

        await context.RefreshVisitAttributionAsync(rescheduled.VisitId, cancellationToken);
    }
}

public sealed class VisitWorkCompletedProjectionHandler : IProjectionHandler<VisitWorkCompletedIntegrationEvent>
{
    public string Name => "VisitWorkCompleted";

    public async Task ApplyAsync(VisitWorkCompletedIntegrationEvent completed, ProjectionContext context, CancellationToken cancellationToken)
    {
        // Set, not added: a visit's actual durations are its completion's, however often the event is seen.
        var visit = await context.VisitAsync(completed.VisitId, cancellationToken);
        visit.ExecutionId = completed.ExecutionId;
        visit.ActualTravelMinutes = completed.TravelMinutes;
        visit.ActualGrossWorkMinutes = completed.GrossWorkMinutes;
        visit.ActualPauseMinutes = completed.PauseMinutes;
        visit.ActualNetWorkMinutes = completed.NetWorkMinutes;
        visit.CompletedAtUtc = completed.OccurredAtUtc.ToUniversalTime();
        VisitStatusRules.Apply(visit, VisitStatusRules.Completed, completed.OccurredAtUtc);
        visit.UpdatedAtUtc = context.Now;

        // The technician comes from this service's own assignment projection, never from the operational database. If
        // no assignment event has arrived yet it stays unknown, and a later assignment event attributes the visit.
        await context.RefreshVisitAttributionAsync(completed.VisitId, cancellationToken);
    }
}
