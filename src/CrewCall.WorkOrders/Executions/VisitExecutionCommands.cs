namespace CrewCall.WorkOrders.Executions;

/// <summary>The field work operations on a visit's execution.</summary>
public enum FieldWorkAction
{
    StartTravel,
    StartWork,
    Pause,
    Resume,
    Complete,
    Cancel
}

public abstract record VisitExecutionOutcome
{
    private VisitExecutionOutcome()
    {
    }

    public sealed record Changed(VisitExecution Execution) : VisitExecutionOutcome;

    public sealed record VisitNotFound(Guid VisitId) : VisitExecutionOutcome;

    /// <summary>Travel or work cannot start on a Completed or Cancelled visit.</summary>
    public sealed record VisitClosed(Guid VisitId, Visits.VisitStatus VisitStatus) : VisitExecutionOutcome;

    /// <summary>Travel and work need an active assignment: the execution must be really assigned work.</summary>
    public sealed record NoActiveAssignment(Guid VisitId) : VisitExecutionOutcome;

    public sealed record TransitionNotAllowed(FieldWorkStatus From, FieldWorkAction Action) : VisitExecutionOutcome;

    /// <summary>Another request changed the execution at the same time; nothing was saved.</summary>
    public sealed record ConcurrentChange(Guid VisitId) : VisitExecutionOutcome;
}
