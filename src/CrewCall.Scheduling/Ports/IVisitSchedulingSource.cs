namespace CrewCall.Scheduling.Ports;

/// <summary>What Scheduling needs to know about a visit, provided by the WorkOrders module through the composition root.</summary>
public interface IVisitSchedulingSource
{
    /// <summary>The visit's time and status, or null when the visit does not exist.</summary>
    Task<VisitSchedulingInfo?> GetVisitAsync(Guid visitId, CancellationToken cancellationToken);
}

/// <param name="Start">UTC.</param>
/// <param name="End">UTC.</param>
public sealed record VisitSchedulingInfo(Guid VisitId, DateTimeOffset Start, DateTimeOffset End, VisitState State)
{
    /// <summary>Completed and Cancelled visits cannot be (re)assigned.</summary>
    public bool IsClosed => State is VisitState.Completed or VisitState.Cancelled;
}

/// <summary>Mirrors the WorkOrders visit statuses without depending on the WorkOrders module.</summary>
public enum VisitState
{
    Planned,
    InProgress,
    Completed,
    Cancelled
}
