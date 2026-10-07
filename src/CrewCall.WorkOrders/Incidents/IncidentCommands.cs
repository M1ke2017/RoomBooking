using CrewCall.WorkOrders.Visits;

namespace CrewCall.WorkOrders.Incidents;

/// <param name="Priority">An <see cref="IncidentPriority"/> name (case-insensitive). Required.</param>
/// <param name="RequiredSkillCodes">Skill codes; trimmed and upper-cased, each at most once.</param>
public sealed record CreateIncident(
    Guid CustomerId,
    Guid SiteId,
    string? Title,
    string? Description,
    string? Priority,
    DateTimeOffset? RequestedStart,
    DateTimeOffset? RequestedEnd,
    IReadOnlyCollection<string?>? RequiredSkillCodes);

public abstract record CreateIncidentOutcome
{
    private CreateIncidentOutcome()
    {
    }

    public sealed record Created(Incident Incident) : CreateIncidentOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateIncidentOutcome;

    public sealed record CustomerNotFound(Guid CustomerId) : CreateIncidentOutcome;

    public sealed record SiteNotFound(Guid SiteId) : CreateIncidentOutcome;
}

/// <param name="Status">Optional <see cref="IncidentStatus"/> name filter.</param>
/// <param name="Priority">Optional <see cref="IncidentPriority"/> name filter.</param>
public sealed record ListIncidents(string? Status, string? Priority);

public abstract record ListIncidentsOutcome
{
    private ListIncidentsOutcome()
    {
    }

    public sealed record Listed(IReadOnlyList<Incident> Incidents) : ListIncidentsOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ListIncidentsOutcome;
}

/// <summary>The outcome of a status change (analysis, resolve, cancel).</summary>
public abstract record ChangeIncidentOutcome
{
    private ChangeIncidentOutcome()
    {
    }

    public sealed record Changed(Incident Incident, IncidentStatus OldStatus) : ChangeIncidentOutcome;

    public sealed record NotFound(Guid IncidentId) : ChangeIncidentOutcome;

    public sealed record TransitionNotAllowed(IncidentStatus From, IncidentStatus To) : ChangeIncidentOutcome;

    /// <summary>Another request changed the incident at the same time; nothing was saved.</summary>
    public sealed record ConcurrentChange(Guid IncidentId) : ChangeIncidentOutcome;
}

/// <summary>What one analysis found; recorded as the IncidentAnalyzed event (the suggestions themselves are not stored).</summary>
public sealed record RecordIncidentAnalysis(
    Guid IncidentId,
    DateTimeOffset GeneratedAtUtc,
    int CandidatesEvaluated,
    int EligibleCount,
    int RejectedCount,
    int DirectAssignmentCount,
    int RequiresRescheduleCount,
    int UnavailableCount);

/// <summary>
/// The WorkOrders part of a dispatch: the work order, its visit over the requested window and the incident's move to
/// Dispatched. The ids of the visit and of the assignment Scheduling claims for it are decided by the caller, so the
/// IncidentDispatched event can name them.
/// </summary>
public sealed record StageIncidentDispatch(
    Guid IncidentId,
    Guid VisitId,
    Guid AssignmentId,
    Guid TechnicianId,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds);

public abstract record StageIncidentDispatchOutcome
{
    private StageIncidentDispatchOutcome()
    {
    }

    /// <summary>Added to the unit of work; written only by the caller's SaveChanges, in the caller's transaction.</summary>
    public sealed record Staged(Incident Incident, WorkOrder WorkOrder, Visit Visit) : StageIncidentDispatchOutcome;

    public sealed record NotFound(Guid IncidentId) : StageIncidentDispatchOutcome;

    /// <summary>The incident already has a work order: it was dispatched before.</summary>
    public sealed record AlreadyDispatched(Guid IncidentId, Guid WorkOrderId) : StageIncidentDispatchOutcome;

    public sealed record NotReady(Guid IncidentId, IncidentStatus Status) : StageIncidentDispatchOutcome;
}
