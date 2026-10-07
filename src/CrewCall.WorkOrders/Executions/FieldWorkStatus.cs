namespace CrewCall.WorkOrders.Executions;

/// <summary>
/// How the field work of a visit is actually going. Not a visit or work order status: the visit is the plan, its
/// execution what really happened (ADR-0013).
/// </summary>
public enum FieldWorkStatus
{
    /// <summary>Nothing recorded yet. A visit without an execution record is NotStarted.</summary>
    NotStarted,
    Traveling,
    Working,
    Paused,
    Completed,
    Cancelled
}

/// <summary>
/// Allowed field work transitions:
/// NotStarted → Traveling | Working | Cancelled; Traveling → Working | Cancelled;
/// Working → Paused | Completed | Cancelled; Paused → Working | Completed | Cancelled.
/// Completed and Cancelled are terminal.
/// </summary>
public static class FieldWorkLifecycle
{
    public static bool CanTransition(FieldWorkStatus from, FieldWorkStatus to) => (from, to) switch
    {
        (FieldWorkStatus.NotStarted, FieldWorkStatus.Traveling) => true,
        (FieldWorkStatus.NotStarted, FieldWorkStatus.Working) => true,
        (FieldWorkStatus.NotStarted, FieldWorkStatus.Cancelled) => true,
        (FieldWorkStatus.Traveling, FieldWorkStatus.Working) => true,
        (FieldWorkStatus.Traveling, FieldWorkStatus.Cancelled) => true,
        (FieldWorkStatus.Working, FieldWorkStatus.Paused) => true,
        (FieldWorkStatus.Working, FieldWorkStatus.Completed) => true,
        (FieldWorkStatus.Working, FieldWorkStatus.Cancelled) => true,
        (FieldWorkStatus.Paused, FieldWorkStatus.Working) => true,
        (FieldWorkStatus.Paused, FieldWorkStatus.Completed) => true,
        (FieldWorkStatus.Paused, FieldWorkStatus.Cancelled) => true,
        _ => false
    };

    public static bool IsTerminal(FieldWorkStatus status) =>
        status is FieldWorkStatus.Completed or FieldWorkStatus.Cancelled;
}
