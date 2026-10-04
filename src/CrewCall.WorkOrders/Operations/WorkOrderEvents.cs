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
}

public sealed record WorkOrderCreatedPayload(Guid WorkOrderId, Guid CustomerId, Guid SiteId, WorkOrderPriority Priority);

public sealed record WorkOrderStatusChangedPayload(Guid WorkOrderId, WorkOrderStatus OldStatus, WorkOrderStatus NewStatus);

public sealed record VisitCreatedPayload(Guid VisitId, Guid WorkOrderId, DateTimeOffset Start, DateTimeOffset End);

public sealed record VisitStatusChangedPayload(Guid VisitId, Visits.VisitStatus OldStatus, Visits.VisitStatus NewStatus);
