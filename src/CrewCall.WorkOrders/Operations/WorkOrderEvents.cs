namespace CrewCall.WorkOrders.Operations;

/// <summary>
/// Operational events recorded by the WorkOrders module. Each is appended in the same database transaction as the
/// state change it describes. Payloads carry only what the history needs, never whole entities.
/// </summary>
public static class WorkOrderEvents
{
    public const string WorkOrderAggregate = "work-order";
    public const string VisitAggregate = "visit";

    public const string WorkOrderCreated = nameof(WorkOrderCreated);
    public const string WorkOrderStatusChanged = nameof(WorkOrderStatusChanged);
    public const string VisitCreated = nameof(VisitCreated);
    public const string VisitStatusChanged = nameof(VisitStatusChanged);
    public const string VisitRescheduled = nameof(VisitRescheduled);
}

public sealed record WorkOrderCreatedPayload(Guid WorkOrderId, Guid CustomerId, Guid SiteId, WorkOrderPriority Priority);

public sealed record WorkOrderStatusChangedPayload(Guid WorkOrderId, WorkOrderStatus OldStatus, WorkOrderStatus NewStatus);

/// <summary>
/// The visit and its work order's customer, site and priority (since Sprint 14), so the published visit.created event
/// carries the context read sides need. Events recorded earlier lack the three work-order fields.
/// </summary>
public sealed record VisitCreatedPayload(
    Guid VisitId, Guid WorkOrderId, DateTimeOffset Start, DateTimeOffset End, Guid CustomerId, Guid SiteId, WorkOrderPriority Priority);

public sealed record VisitStatusChangedPayload(Guid VisitId, Visits.VisitStatus OldStatus, Visits.VisitStatus NewStatus);

/// <summary>The visit kept its identity and moved to a new window (Sprint 15).</summary>
/// <param name="Reason">Why it moved, e.g. "UrgentIncident".</param>
/// <param name="IncidentId">The urgent incident that displaced it, if any.</param>
public sealed record VisitRescheduledPayload(
    Guid VisitId,
    DateTimeOffset OldStart,
    DateTimeOffset OldEnd,
    DateTimeOffset NewStart,
    DateTimeOffset NewEnd,
    string Reason,
    Guid? IncidentId);
