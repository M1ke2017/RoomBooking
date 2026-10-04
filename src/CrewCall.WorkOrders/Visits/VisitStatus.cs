namespace CrewCall.WorkOrders.Visits;

public enum VisitStatus
{
    Planned,
    InProgress,
    Completed,
    Cancelled
}

/// <summary>
/// Allowed visit transitions: Planned → InProgress | Cancelled; InProgress → Completed | Cancelled.
/// Completed and Cancelled are terminal. Travel and pause states come with the field status workflow later.
/// </summary>
public static class VisitLifecycle
{
    public static bool CanTransition(VisitStatus from, VisitStatus to) => (from, to) switch
    {
        (VisitStatus.Planned, VisitStatus.InProgress) => true,
        (VisitStatus.Planned, VisitStatus.Cancelled) => true,
        (VisitStatus.InProgress, VisitStatus.Completed) => true,
        (VisitStatus.InProgress, VisitStatus.Cancelled) => true,
        _ => false
    };
}
