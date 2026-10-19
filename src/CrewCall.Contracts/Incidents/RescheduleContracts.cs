namespace CrewCall.Contracts.Incidents;

// Dynamic rescheduling of an urgent incident (Sprint 15, ADR-0017). One model from the Scheduling module to the API
// response: the proposal is built as this contract, with no internal copy in between.

/// <summary>
/// POST /api/incidents/{id}/reschedule-proposal. Everything optional: candidates default to all active technicians;
/// the vehicle, equipment and travel buffers are the operator's choice for the urgent visit, as for dispatch.
/// </summary>
public sealed record RescheduleProposalRequest(
    IReadOnlyCollection<Guid>? CandidateTechnicianIds,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <summary>
/// One way to dispatch the incident: a technician (with the selected vehicle and equipment) for the urgent window, and at
/// most one lower-priority visit moved to its next free slot to make room. Advice only: nothing is changed until a
/// manager applies it, and applying checks everything again.
/// </summary>
/// <param name="Rank">1 for the proposal with the smallest business impact.</param>
/// <param name="ConflictingPriority">The moved visit's work order priority ("Low", "Normal", "High"), when a visit must move.</param>
/// <param name="ProposedNewStartUtc">The moved visit's new start: its first free slot after the urgent visit.</param>
/// <param name="IsFeasible">False when the technician could only take the incident by moving work that may not or cannot
/// move; the warnings say why.</param>
public sealed record RescheduleProposal(
    int Rank,
    Guid IncidentId,
    DateTimeOffset UrgentVisitStartUtc,
    DateTimeOffset UrgentVisitEndUtc,
    Guid RecommendedTechnicianId,
    string TechnicianName,
    Guid? VehicleId,
    IReadOnlyList<Guid> EquipmentIds,
    int TravelBufferBeforeMinutes,
    int TravelBufferAfterMinutes,
    Guid? ConflictingVisitId,
    Guid? ConflictingAssignmentId,
    string? ConflictingPriority,
    DateTimeOffset? ProposedNewStartUtc,
    DateTimeOffset? ProposedNewEndUtc,
    RescheduleImpact Impact,
    bool IsFeasible);

/// <param name="DelayMinutes">How much later the moved visit starts (0 without a move).</param>
/// <param name="ConflictResolved">After the change the urgent visit and the moved visit are both free of conflicts.</param>
/// <param name="AffectedVisits">The customer impact: each moved visit, with its customer, site and old and new time.</param>
public sealed record RescheduleImpact(
    int AffectedVisitCount,
    int AffectedCustomerCount,
    int DelayMinutes,
    bool ConflictResolved,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AffectedVisit> AffectedVisits);

public sealed record AffectedVisit(
    Guid VisitId,
    Guid CustomerId,
    Guid SiteId,
    DateTimeOffset OldStartUtc,
    DateTimeOffset OldEndUtc,
    DateTimeOffset NewStartUtc,
    DateTimeOffset NewEndUtc,
    int DelayMinutes);

/// <summary>
/// POST /api/incidents/{id}/reschedule-proposal/apply: the manager accepts a proposal. The same resources as the proposal,
/// plus the visit to move and its new window (none for a proposal without a move). Revalidated in full on apply.
/// </summary>
public sealed record ApplyRescheduleRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    IReadOnlyCollection<Guid>? EquipmentIds,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes,
    Guid? MovedVisitId,
    DateTimeOffset? MovedVisitNewStartUtc,
    DateTimeOffset? MovedVisitNewEndUtc);

/// <summary>What apply did: the dispatch (as POST /dispatch returns it) and the visit it moved, if any.</summary>
public sealed record ApplyRescheduleResponse(IncidentDispatchResponse Dispatch, AffectedVisit? MovedVisit);
