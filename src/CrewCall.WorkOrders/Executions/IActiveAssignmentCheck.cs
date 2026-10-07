namespace CrewCall.WorkOrders.Executions;

/// <summary>
/// Whether a visit currently has an active assignment. Assignments belong to Scheduling, which WorkOrders does not
/// reference (ADR-0001): the composition root implements this port.
/// </summary>
public interface IActiveAssignmentCheck
{
    Task<bool> HasActiveAssignmentAsync(Guid visitId, CancellationToken cancellationToken);
}
