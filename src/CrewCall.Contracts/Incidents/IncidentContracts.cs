using CrewCall.Contracts.Scheduling;

namespace CrewCall.Contracts.Incidents;

/// <summary>POST /api/incidents.</summary>
/// <param name="Priority">High, Urgent or Critical.</param>
/// <param name="RequestedStart">Start of the requested window; any offset, stored as UTC.</param>
/// <param name="RequestedEnd">End of the requested window (exclusive); after RequestedStart, at most 31 days later.</param>
/// <param name="RequiredSkillCodes">Skill codes the technician must have; trimmed and upper-cased, each at most once.</param>
public sealed record CreateIncidentRequest(
    Guid CustomerId,
    Guid SiteId,
    string? Title,
    string? Description,
    string? Priority,
    DateTimeOffset? RequestedStart,
    DateTimeOffset? RequestedEnd,
    string?[]? RequiredSkillCodes);

/// <param name="Status">New, Analyzing, ReadyForDispatch, Dispatched, Resolved or Cancelled.</param>
/// <param name="WorkOrderId">The work order created by dispatch.</param>
public sealed record IncidentResponse(
    Guid Id,
    Guid CustomerId,
    Guid SiteId,
    string Title,
    string? Description,
    string Priority,
    string Status,
    DateTimeOffset RequestedStart,
    DateTimeOffset RequestedEnd,
    string[] RequiredSkillCodes,
    Guid? WorkOrderId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

/// <summary>POST /api/incidents/{id}/analyze. The body is optional.</summary>
/// <param name="CandidateTechnicianIds">Consider only these technicians; by default all active ones (at most 200).</param>
/// <param name="PreferredTeamId">Members of this team score higher (a preference, never a requirement).</param>
public sealed record AnalyzeIncidentRequest(Guid[]? CandidateTechnicianIds, Guid? PreferredTeamId);

/// <summary>One existing reservation a suggestion or selection collides with. Shown, never changed.</summary>
/// <param name="ResourceType">Technician, Vehicle or Equipment.</param>
/// <param name="WithinTravelBuffer">The clash is only with the travel buffer around the incident.</param>
public sealed record IncidentImpactResponse(
    Guid ReservationId,
    string ResourceType,
    Guid ResourceId,
    Guid? ConflictingVisitId,
    Guid? ConflictingWorkOrderId,
    DateTimeOffset ReservedStart,
    DateTimeOffset ReservedEnd,
    bool WithinTravelBuffer);

/// <param name="SuggestionType">DirectAssignment, RequiresReschedule or Unavailable.</param>
/// <param name="Score">0–100. For RequiresReschedule: the score once the conflicts are resolved. Null when Unavailable.</param>
/// <param name="SchedulingChecked">Confirmed by a full scheduling check.</param>
/// <param name="ConflictingVisitId">The earliest conflicting visit (see Impact for all).</param>
/// <param name="ConflictStart">Start of the earliest conflicting reservation.</param>
public sealed record IncidentDispatchSuggestionResponse(
    int Rank,
    Guid TechnicianId,
    string TechnicianName,
    Guid? TeamId,
    string SuggestionType,
    decimal? Score,
    bool SchedulingChecked,
    SchedulingConflictResponse[] Reasons,
    Guid? ConflictingVisitId,
    Guid? ConflictingWorkOrderId,
    DateTimeOffset? ConflictStart,
    DateTimeOffset? ConflictEnd,
    IncidentImpactResponse[] Impact);

/// <summary>The analysis: advice only. Nothing was claimed and no existing work was changed.</summary>
public sealed record IncidentAnalysisResponse(
    Guid IncidentId,
    string IncidentStatus,
    DateTimeOffset GeneratedAtUtc,
    int CandidatesEvaluated,
    bool CandidatesTruncated,
    int EligibleCount,
    int RejectedCount,
    IncidentDispatchSuggestionResponse[] Suggestions);

/// <summary>POST /api/incidents/{id}/prepare-dispatch: would this selection dispatch now? Nothing is written.</summary>
/// <param name="EquipmentIds">Each asset at most once.</param>
/// <param name="TravelBufferBeforeMinutes">0–480, default 0.</param>
/// <param name="TravelBufferAfterMinutes">0–480, default 0.</param>
public sealed record PrepareIncidentDispatchRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    Guid[]? EquipmentIds,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <param name="CanDispatch">The final check passes right now (advice: dispatch checks again).</param>
/// <param name="Reasons">Why not, as the scheduling check reports them.</param>
/// <param name="Impact">The existing reservations the selection collides with.</param>
public sealed record PrepareIncidentDispatchResponse(
    Guid IncidentId,
    bool CanDispatch,
    SchedulingConflictResponse[] Reasons,
    IncidentImpactResponse[] Impact,
    Guid TechnicianId,
    Guid? VehicleId,
    Guid[] EquipmentIds,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset EffectiveStart,
    DateTimeOffset EffectiveEnd);

/// <summary>POST /api/incidents/{id}/dispatch: the operator's decision. Same fields as prepare.</summary>
public sealed record DispatchIncidentRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    Guid[]? EquipmentIds,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <summary>201: what dispatch created, atomically.</summary>
public sealed record IncidentDispatchResponse(
    IncidentResponse Incident,
    Guid WorkOrderId,
    Guid VisitId,
    AssignmentResponse Assignment);
