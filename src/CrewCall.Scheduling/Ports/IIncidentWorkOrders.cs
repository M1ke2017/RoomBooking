namespace CrewCall.Scheduling.Ports;

/// <summary>
/// What the incident workflow (ADR-0012) needs from the WorkOrders module, which owns incidents, work orders and visits;
/// implemented by the composition root (ADR-0001). Scheduling decides; WorkOrders keeps the incident's lifecycle.
/// </summary>
public interface IIncidentWorkOrders
{
    /// <summary>The incident's scheduling data, or null when it does not exist.</summary>
    Task<IncidentSchedulingInfo?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>New → Analyzing (Analyzing and ReadyForDispatch stay as they are). Saved immediately.</summary>
    Task<IncidentStateChange> BeginAnalysisAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>Analyzing → ReadyForDispatch with the IncidentAnalyzed event. Saved immediately.</summary>
    Task<IncidentStateChange> CompleteAnalysisAsync(IncidentAnalysisSummary analysis, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the work order, its visit, the incident's move to Dispatched and their events to the shared unit of work,
    /// WITHOUT saving: the caller saves and commits them in its own transaction together with the resource claim.
    /// </summary>
    Task<IncidentDispatchStaging> StageDispatchAsync(IncidentDispatchPlan plan, CancellationToken cancellationToken);

    /// <summary>The work order of each existing visit among <paramref name="visitIds"/>.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetWorkOrderIdsAsync(IReadOnlyCollection<Guid> visitIds, CancellationToken cancellationToken);
}

/// <param name="RequestedStart">UTC.</param>
/// <param name="RequestedEnd">UTC.</param>
/// <param name="RequiredSkillCodes">Normalized codes.</param>
/// <param name="WorkOrderId">Set once the incident has been dispatched.</param>
public sealed record IncidentSchedulingInfo(
    Guid IncidentId,
    IncidentState State,
    DateTimeOffset RequestedStart,
    DateTimeOffset RequestedEnd,
    IReadOnlyList<string> RequiredSkillCodes,
    Guid? WorkOrderId);

/// <summary>Mirrors the WorkOrders incident statuses without depending on the WorkOrders module.</summary>
public enum IncidentState
{
    New,
    Analyzing,
    ReadyForDispatch,
    Dispatched,
    Resolved,
    Cancelled
}

public abstract record IncidentStateChange
{
    private IncidentStateChange()
    {
    }

    public sealed record Changed(IncidentState State) : IncidentStateChange;

    public sealed record NotFound(Guid IncidentId) : IncidentStateChange;

    public sealed record NotAllowed(IncidentState Current) : IncidentStateChange;

    /// <summary>Another request changed the incident at the same time; nothing was saved.</summary>
    public sealed record ConcurrentChange(Guid IncidentId) : IncidentStateChange;
}

public sealed record IncidentAnalysisSummary(
    Guid IncidentId,
    DateTimeOffset GeneratedAtUtc,
    int CandidatesEvaluated,
    int EligibleCount,
    int RejectedCount,
    int DirectAssignmentCount,
    int RequiresRescheduleCount,
    int UnavailableCount);

/// <summary>The dispatch decided by Scheduling: the visit and assignment ids are fixed before anything is written.</summary>
public sealed record IncidentDispatchPlan(
    Guid IncidentId,
    Guid VisitId,
    Guid AssignmentId,
    Guid TechnicianId,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds);

public abstract record IncidentDispatchStaging
{
    private IncidentDispatchStaging()
    {
    }

    public sealed record Staged(Guid WorkOrderId, Guid VisitId) : IncidentDispatchStaging;

    public sealed record NotFound(Guid IncidentId) : IncidentDispatchStaging;

    public sealed record AlreadyDispatched(Guid WorkOrderId) : IncidentDispatchStaging;

    public sealed record NotReady(IncidentState Current) : IncidentDispatchStaging;
}
