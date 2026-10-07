using CrewCall.WorkOrders.Incidents;

namespace CrewCall.WorkOrders.Operations;

/// <summary>Operational events of the incident workflow (ADR-0012), appended in the transaction of each change.</summary>
public static class IncidentEvents
{
    public const string IncidentAggregate = "incident";

    public const string IncidentCreated = nameof(IncidentCreated);
    public const string IncidentAnalyzed = nameof(IncidentAnalyzed);
    public const string IncidentDispatched = nameof(IncidentDispatched);
    public const string IncidentResolved = nameof(IncidentResolved);
    public const string IncidentCancelled = nameof(IncidentCancelled);
}

public sealed record IncidentCreatedPayload(
    Guid IncidentId,
    Guid CustomerId,
    Guid SiteId,
    IncidentPriority Priority,
    DateTimeOffset RequestedStart,
    DateTimeOffset RequestedEnd,
    IReadOnlyList<string> RequiredSkillCodes);

/// <summary>A summary only: the suggestions are advice computed on request and are not stored.</summary>
public sealed record IncidentAnalyzedPayload(
    Guid IncidentId,
    IncidentStatus Status,
    DateTimeOffset GeneratedAtUtc,
    int CandidatesEvaluated,
    int EligibleCount,
    int RejectedCount,
    int DirectAssignmentCount,
    int RequiresRescheduleCount,
    int UnavailableCount);

public sealed record IncidentDispatchedPayload(
    Guid IncidentId,
    Guid WorkOrderId,
    Guid VisitId,
    Guid AssignmentId,
    Guid TechnicianId,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds);

public sealed record IncidentResolvedPayload(Guid IncidentId, Guid? WorkOrderId, DateTimeOffset ResolvedAtUtc);

public sealed record IncidentCancelledPayload(Guid IncidentId, IncidentStatus OldStatus);
