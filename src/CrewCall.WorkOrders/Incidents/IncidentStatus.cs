namespace CrewCall.WorkOrders.Incidents;

/// <summary>How urgent an incident is. A priority, never a status.</summary>
public enum IncidentPriority
{
    High,
    Urgent,
    Critical
}

public enum IncidentStatus
{
    New,
    Analyzing,
    ReadyForDispatch,
    Dispatched,
    Resolved,
    Cancelled
}

/// <summary>
/// Allowed incident transitions:
/// New → Analyzing | Cancelled; Analyzing → ReadyForDispatch | Cancelled; ReadyForDispatch → Dispatched | Cancelled;
/// Dispatched → Resolved. Resolved and Cancelled are terminal. A dispatched incident cannot be cancelled: its work order,
/// visit and assignment exist and are changed only explicitly, by an operator.
/// </summary>
public static class IncidentLifecycle
{
    public static bool CanTransition(IncidentStatus from, IncidentStatus to) => (from, to) switch
    {
        (IncidentStatus.New, IncidentStatus.Analyzing) => true,
        (IncidentStatus.New, IncidentStatus.Cancelled) => true,
        (IncidentStatus.Analyzing, IncidentStatus.ReadyForDispatch) => true,
        (IncidentStatus.Analyzing, IncidentStatus.Cancelled) => true,
        (IncidentStatus.ReadyForDispatch, IncidentStatus.Dispatched) => true,
        (IncidentStatus.ReadyForDispatch, IncidentStatus.Cancelled) => true,
        (IncidentStatus.Dispatched, IncidentStatus.Resolved) => true,
        _ => false
    };

    public static bool IsTerminal(IncidentStatus status) =>
        status is IncidentStatus.Resolved or IncidentStatus.Cancelled;

    /// <summary>
    /// The priority of the work order created by dispatch. WorkOrderPriority has no Critical: a critical incident becomes
    /// an Urgent work order (the incident keeps its own Critical priority).
    /// </summary>
    public static WorkOrderPriority ToWorkOrderPriority(IncidentPriority priority) => priority switch
    {
        IncidentPriority.High => WorkOrderPriority.High,
        IncidentPriority.Urgent => WorkOrderPriority.Urgent,
        IncidentPriority.Critical => WorkOrderPriority.Urgent,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown incident priority.")
    };
}
