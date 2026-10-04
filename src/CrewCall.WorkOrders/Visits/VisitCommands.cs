namespace CrewCall.WorkOrders.Visits;

public sealed record CreateVisit(Guid WorkOrderId, DateTimeOffset? Start, DateTimeOffset? End, string? Notes);

public abstract record CreateVisitOutcome
{
    private CreateVisitOutcome()
    {
    }

    public sealed record Created(Visit Visit) : CreateVisitOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateVisitOutcome;

    public sealed record WorkOrderNotFound(Guid WorkOrderId) : CreateVisitOutcome;

    /// <summary>The work order is Completed or Cancelled and takes no new visits.</summary>
    public sealed record WorkOrderClosed(Guid WorkOrderId, WorkOrderStatus Status) : CreateVisitOutcome;
}

/// <param name="Status">A <see cref="VisitStatus"/> name (case-insensitive).</param>
public sealed record ChangeVisitStatus(Guid VisitId, string? Status);

public abstract record ChangeVisitStatusOutcome
{
    private ChangeVisitStatusOutcome()
    {
    }

    public sealed record Changed(Visit Visit, VisitStatus OldStatus) : ChangeVisitStatusOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ChangeVisitStatusOutcome;

    public sealed record NotFound(Guid VisitId) : ChangeVisitStatusOutcome;

    public sealed record TransitionNotAllowed(VisitStatus From, VisitStatus To) : ChangeVisitStatusOutcome;

    /// <summary>Another request changed the visit at the same time; nothing was saved.</summary>
    public sealed record ConcurrentChange(Guid VisitId) : ChangeVisitStatusOutcome;
}
