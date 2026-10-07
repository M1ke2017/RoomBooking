namespace CrewCall.WorkOrders.Executions;

/// <summary>
/// The actual field execution of one visit (at most one per visit): when travel and work really started, the pauses, and
/// when the work was completed or the execution cancelled. The visit's own Start/End stay the plan and are never changed
/// by execution. Durations are not stored: they are derived from these timestamps (<see cref="VisitExecutionMetrics"/>).
/// </summary>
public sealed class VisitExecution
{
    private readonly List<VisitExecutionPause> _pauses = [];

    internal VisitExecution(Guid id, Guid visitId, DateTimeOffset createdAtUtc)
    {
        Id = id;
        VisitId = visitId;
        Status = FieldWorkStatus.NotStarted;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    // For EF Core materialization.
    private VisitExecution()
    {
    }

    public Guid Id { get; private set; }

    public Guid VisitId { get; private set; }

    public FieldWorkStatus Status { get; private set; }

    public DateTimeOffset? TravelStartedAtUtc { get; private set; }

    /// <summary>When work first started; a resume after a pause does not change it.</summary>
    public DateTimeOffset? WorkStartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Every pause, oldest first; at most one is open (no end yet), and only while Paused.</summary>
    public IReadOnlyList<VisitExecutionPause> Pauses => _pauses;

    public VisitExecutionPause? OpenPause => _pauses.SingleOrDefault(pause => pause.EndedAtUtc is null);

    internal bool TryStartTravel(DateTimeOffset now)
    {
        if (!Move(FieldWorkStatus.Traveling, now))
        {
            return false;
        }

        TravelStartedAtUtc = now;
        return true;
    }

    /// <summary>The first start of work, from NotStarted (no travel recorded) or Traveling. Not a resume.</summary>
    internal bool TryStartWork(DateTimeOffset now)
    {
        if (Status is not (FieldWorkStatus.NotStarted or FieldWorkStatus.Traveling) || !Move(FieldWorkStatus.Working, now))
        {
            return false;
        }

        WorkStartedAtUtc = now;
        return true;
    }

    /// <summary>Working → Paused with a new open pause; null when not allowed.</summary>
    internal VisitExecutionPause? TryPause(Guid pauseId, DateTimeOffset now)
    {
        if (Status != FieldWorkStatus.Working || OpenPause is not null || !Move(FieldWorkStatus.Paused, now))
        {
            return null;
        }

        var pause = new VisitExecutionPause(pauseId, Id, now);
        _pauses.Add(pause);
        return pause;
    }

    /// <summary>Paused → Working, closing the open pause; returns it, or null when not allowed.</summary>
    internal VisitExecutionPause? TryResume(DateTimeOffset now)
    {
        if (Status != FieldWorkStatus.Paused || OpenPause is not { } pause || !Move(FieldWorkStatus.Working, now))
        {
            return null;
        }

        pause.End(now);
        return pause;
    }

    /// <summary>Working or Paused → Completed. An open pause ends at the same instant.</summary>
    internal bool TryComplete(DateTimeOffset now)
    {
        if (!Move(FieldWorkStatus.Completed, now))
        {
            return false;
        }

        OpenPause?.End(now);
        CompletedAtUtc = now;
        return true;
    }

    /// <summary>Any non-terminal status → Cancelled. An open pause ends at the same instant.</summary>
    internal bool TryCancel(DateTimeOffset now)
    {
        if (!Move(FieldWorkStatus.Cancelled, now))
        {
            return false;
        }

        OpenPause?.End(now);
        CancelledAtUtc = now;
        return true;
    }

    private bool Move(FieldWorkStatus target, DateTimeOffset now)
    {
        if (!FieldWorkLifecycle.CanTransition(Status, target))
        {
            return false;
        }

        Status = target;
        UpdatedAtUtc = now;
        return true;
    }
}

/// <summary>One pause of the work; open (no end) while the execution is Paused.</summary>
public sealed class VisitExecutionPause
{
    internal VisitExecutionPause(Guid id, Guid visitExecutionId, DateTimeOffset startedAtUtc)
    {
        Id = id;
        VisitExecutionId = visitExecutionId;
        StartedAtUtc = startedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid VisitExecutionId { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    internal void End(DateTimeOffset now) => EndedAtUtc = now;
}
