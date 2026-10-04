namespace CrewCall.WorkOrders.Visits;

/// <summary>
/// A planned or ongoing field visit within a work order, over the half-open interval [Start, End), stored in UTC.
/// No technician, vehicle or equipment yet: assignment is a separate, later concern.
/// </summary>
public sealed class Visit
{
    public const int NotesMaxLength = 2000;

    internal Visit(Guid id, Guid workOrderId, DateTimeOffset start, DateTimeOffset end, string? notes, DateTimeOffset createdAtUtc)
    {
        Id = id;
        WorkOrderId = workOrderId;
        Start = start;
        End = end;
        Notes = notes;
        Status = VisitStatus.Planned;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid WorkOrderId { get; private set; }

    public DateTimeOffset Start { get; private set; }

    public DateTimeOffset End { get; private set; }

    public VisitStatus Status { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Moves to <paramref name="target"/> when the lifecycle allows it; otherwise changes nothing and returns false.</summary>
    internal bool TryTransitionTo(VisitStatus target)
    {
        if (!VisitLifecycle.CanTransition(Status, target))
        {
            return false;
        }

        Status = target;
        return true;
    }
}
