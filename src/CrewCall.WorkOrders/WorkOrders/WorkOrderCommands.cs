namespace CrewCall.WorkOrders;

/// <param name="Priority">A <see cref="WorkOrderPriority"/> name (case-insensitive); Normal when omitted.</param>
public sealed record CreateWorkOrder(Guid CustomerId, Guid SiteId, string? Title, string? Description, string? Priority);

public abstract record CreateWorkOrderOutcome
{
    private CreateWorkOrderOutcome()
    {
    }

    public sealed record Created(WorkOrder WorkOrder) : CreateWorkOrderOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateWorkOrderOutcome;

    public sealed record CustomerNotFound(Guid CustomerId) : CreateWorkOrderOutcome;

    public sealed record SiteNotFound(Guid SiteId) : CreateWorkOrderOutcome;
}

/// <param name="Status">A <see cref="WorkOrderStatus"/> name (case-insensitive).</param>
public sealed record ChangeWorkOrderStatus(Guid WorkOrderId, string? Status);

public abstract record ChangeWorkOrderStatusOutcome
{
    private ChangeWorkOrderStatusOutcome()
    {
    }

    public sealed record Changed(WorkOrder WorkOrder, WorkOrderStatus OldStatus) : ChangeWorkOrderStatusOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ChangeWorkOrderStatusOutcome;

    public sealed record NotFound(Guid WorkOrderId) : ChangeWorkOrderStatusOutcome;

    public sealed record TransitionNotAllowed(WorkOrderStatus From, WorkOrderStatus To) : ChangeWorkOrderStatusOutcome;

    /// <summary>Another request changed the work order at the same time; nothing was saved.</summary>
    public sealed record ConcurrentChange(Guid WorkOrderId) : ChangeWorkOrderStatusOutcome;
}
