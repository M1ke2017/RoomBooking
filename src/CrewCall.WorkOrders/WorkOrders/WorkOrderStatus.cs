namespace CrewCall.WorkOrders;

public enum WorkOrderStatus
{
    Open,
    Planned,
    InProgress,
    Completed,
    Cancelled
}

/// <summary>
/// Allowed work order transitions:
/// Open → Planned | Cancelled; Planned → InProgress | Cancelled; InProgress → Completed | Cancelled.
/// Completed and Cancelled are terminal.
/// </summary>
public static class WorkOrderLifecycle
{
    public static bool CanTransition(WorkOrderStatus from, WorkOrderStatus to) => (from, to) switch
    {
        (WorkOrderStatus.Open, WorkOrderStatus.Planned) => true,
        (WorkOrderStatus.Open, WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.Planned, WorkOrderStatus.InProgress) => true,
        (WorkOrderStatus.Planned, WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.InProgress, WorkOrderStatus.Completed) => true,
        (WorkOrderStatus.InProgress, WorkOrderStatus.Cancelled) => true,
        _ => false
    };

    public static bool IsTerminal(WorkOrderStatus status) =>
        status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled;
}
