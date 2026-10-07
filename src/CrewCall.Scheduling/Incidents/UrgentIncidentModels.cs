using CrewCall.Scheduling.Assignments;
using CrewCall.Scheduling.Checks;
using CrewCall.Scheduling.Ports;
using CrewCall.Scheduling.Reservations;

namespace CrewCall.Scheduling.Incidents;

/// <summary>Analyses an incident: who could take it, and what that would do to the existing plan.</summary>
/// <param name="CandidateTechnicianIds">Optional subset to consider; by default all active technicians (as matching).</param>
/// <param name="PreferredTeamId">Optional team preference for the score (as matching).</param>
public sealed record AnalyzeIncident(Guid IncidentId, IReadOnlyCollection<Guid>? CandidateTechnicianIds, Guid? PreferredTeamId);

public enum DispatchSuggestionType
{
    /// <summary>Feasible and free: dispatchable as is.</summary>
    DirectAssignment,

    /// <summary>Feasible, but the existing plan collides: the operator would have to change it first.</summary>
    RequiresReschedule,

    /// <summary>Inactive, missing skills or unavailable.</summary>
    Unavailable
}

/// <summary>One reservation of the existing plan that a suggestion or a selection collides with. Nothing is changed.</summary>
/// <param name="ConflictingVisitId">The visit holding the reservation, when it belongs to one.</param>
/// <param name="ConflictingWorkOrderId">That visit's work order.</param>
/// <param name="WithinTravelBuffer">The clash is only with the travel buffer around the incident, not the incident itself.</param>
public sealed record DispatchImpact(
    Guid ReservationId,
    ResourceType ResourceType,
    Guid ResourceId,
    Guid? ConflictingVisitId,
    Guid? ConflictingWorkOrderId,
    DateTimeOffset ReservedStart,
    DateTimeOffset ReservedEnd,
    bool WithinTravelBuffer);

/// <param name="Rank">1-based position among the suggestions.</param>
/// <param name="Score">The matching score; for RequiresReschedule the score once the conflicts are resolved; null when Unavailable.</param>
/// <param name="SchedulingChecked">A full scheduling check confirmed the type (the top direct candidates).</param>
/// <param name="Reasons">Why not direct: the conflicts, or every reason the technician is unavailable.</param>
/// <param name="Impact">The existing reservations involved, earliest first.</param>
public sealed record IncidentDispatchSuggestion(
    int Rank,
    Guid TechnicianId,
    string TechnicianName,
    Guid? TeamId,
    DispatchSuggestionType SuggestionType,
    decimal? Score,
    bool SchedulingChecked,
    IReadOnlyList<SchedulingConflict> Reasons,
    IReadOnlyList<DispatchImpact> Impact)
{
    /// <summary>The first (earliest) conflicting visit, if any.</summary>
    public DispatchImpact? FirstConflict => Impact.Count > 0 ? Impact[0] : null;
}

/// <param name="EligibleCount">Candidates matching found eligible (feasible and free).</param>
/// <param name="RejectedCount">Candidates matching rejected (unavailable, or booked).</param>
public sealed record IncidentAnalysis(
    Guid IncidentId,
    IncidentState IncidentStatus,
    DateTimeOffset GeneratedAtUtc,
    int CandidatesEvaluated,
    bool CandidatesTruncated,
    int EligibleCount,
    int RejectedCount,
    IReadOnlyList<IncidentDispatchSuggestion> Suggestions);

public abstract record AnalyzeIncidentOutcome
{
    private AnalyzeIncidentOutcome()
    {
    }

    public sealed record Analyzed(IncidentAnalysis Analysis) : AnalyzeIncidentOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AnalyzeIncidentOutcome;

    public sealed record IncidentNotFound(Guid IncidentId) : AnalyzeIncidentOutcome;

    /// <summary>A requested candidate technician or team does not exist.</summary>
    public sealed record NotFound(IReadOnlyList<(string Kind, Guid Id)> Missing) : AnalyzeIncidentOutcome;

    /// <summary>The incident is Dispatched, Resolved or Cancelled.</summary>
    public sealed record NotAllowed(Guid IncidentId, IncidentState Status) : AnalyzeIncidentOutcome;

    public sealed record ConcurrentChange(Guid IncidentId) : AnalyzeIncidentOutcome;
}

/// <summary>The operator's choice of resources for an incident (prepare and dispatch take the same).</summary>
/// <param name="EquipmentIds">Each asset at most once.</param>
/// <param name="TravelBufferBeforeMinutes">0–480, default 0.</param>
/// <param name="TravelBufferAfterMinutes">0–480, default 0.</param>
public sealed record IncidentResourceSelection(
    Guid IncidentId,
    Guid? TechnicianId,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <param name="CanDispatch">The final check passes right now. Advice: dispatch checks again.</param>
public sealed record IncidentDispatchReadiness(
    Guid IncidentId,
    bool CanDispatch,
    SchedulingCheckResult Check,
    IReadOnlyList<DispatchImpact> Impact);

/// <summary>Failures common to prepare and dispatch.</summary>
public abstract record IncidentDispatchOutcome
{
    private protected IncidentDispatchOutcome()
    {
    }

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : IncidentDispatchOutcome;

    public sealed record IncidentNotFound(Guid IncidentId) : IncidentDispatchOutcome;

    public sealed record ResourcesNotFound(IReadOnlyList<(ResourceType Type, Guid ResourceId)> Missing) : IncidentDispatchOutcome;

    /// <summary>The incident is not ReadyForDispatch (not analysed yet, cancelled, resolved).</summary>
    public sealed record NotReady(Guid IncidentId, IncidentState Status) : IncidentDispatchOutcome;

    /// <summary>The incident has been dispatched already (possibly by a concurrent request).</summary>
    public sealed record AlreadyDispatched(Guid IncidentId, Guid WorkOrderId) : IncidentDispatchOutcome;

    /// <summary>Prepare: the readiness, also when the selection cannot be dispatched.</summary>
    public sealed record Prepared(IncidentDispatchReadiness Readiness) : IncidentDispatchOutcome;

    /// <summary>Dispatch: everything committed.</summary>
    public sealed record Dispatched(Guid IncidentId, Guid WorkOrderId, Guid VisitId, Assignment Assignment) : IncidentDispatchOutcome;

    /// <summary>Dispatch: the final check failed, or another claim won the race; nothing was written.</summary>
    public sealed record Rejected(IReadOnlyList<SchedulingConflict> Reasons, IReadOnlyList<DispatchImpact> Impact) : IncidentDispatchOutcome;
}
