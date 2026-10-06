namespace CrewCall.Contracts.Scheduling;

/// <summary>POST /api/scheduling/check: could these resources take a visit in [Start, End)? Nothing is reserved.</summary>
/// <param name="VisitId">Optional: the visit being re-planned; its own reservations are ignored.</param>
/// <param name="TravelBufferBeforeMinutes">0–480, default 0.</param>
/// <param name="TravelBufferAfterMinutes">0–480, default 0.</param>
public sealed record SchedulingCheckRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    Guid[]? EquipmentIds,
    Guid? VisitId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string[]? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <param name="IsFeasible">True only when Reasons is empty.</param>
/// <param name="EffectiveStart">Start minus the travel buffer: resource conflicts are checked from here (UTC).</param>
/// <param name="EffectiveEnd">End plus the travel buffer: resource conflicts are checked to here (UTC).</param>
public sealed record SchedulingCheckResponse(
    bool IsFeasible,
    SchedulingConflictResponse[] Reasons,
    Guid TechnicianId,
    Guid? VehicleId,
    Guid[] EquipmentIds,
    Guid? VisitId,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset EffectiveStart,
    DateTimeOffset EffectiveEnd);

/// <param name="Code">
/// TechnicianInactive, TechnicianUnavailable, MissingRequiredSkills, TechnicianReservationConflict,
/// VehicleReservationConflict, EquipmentReservationConflict or TravelBufferConflict.
/// </param>
/// <param name="ResourceType">Technician, Vehicle or Equipment.</param>
/// <param name="RelatedResourceId">The technician, vehicle or equipment asset concerned.</param>
/// <param name="ReservationId">The clashing reservation, for reservation and travel-buffer conflicts.</param>
/// <param name="Details">Missing skill codes, or Workforce's unavailability reasons and detail.</param>
/// <param name="ReservationVisitId">The visit the clashing reservation belongs to, when it belongs to one.</param>
public sealed record SchedulingConflictResponse(
    string Code,
    string Message,
    string? ResourceType,
    Guid? RelatedResourceId,
    Guid? ReservationId,
    DateTimeOffset? ReservedStart,
    DateTimeOffset? ReservedEnd,
    string[] Details,
    Guid? ReservationVisitId);
