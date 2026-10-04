namespace CrewCall.Contracts.Scheduling;

/// <summary>POST /api/visits/{visitId}/assignment. The visit's own start and end are used.</summary>
/// <param name="EquipmentIds">Each asset at most once.</param>
/// <param name="RequiredSkillCodes">Skills the technician must have for this visit; checked, not stored.</param>
/// <param name="TravelBufferBeforeMinutes">0–480, default 0.</param>
/// <param name="TravelBufferAfterMinutes">0–480, default 0.</param>
public sealed record CreateAssignmentRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    Guid[]? EquipmentIds,
    string[]? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <summary>POST /api/visits/{visitId}/assignment/reassign: replaces the active assignment.</summary>
public sealed record ReassignAssignmentRequest(
    Guid? TechnicianId,
    Guid? VehicleId,
    Guid[]? EquipmentIds,
    string[]? RequiredSkillCodes,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes);

/// <param name="Status">Active, Replaced or Cancelled.</param>
/// <param name="ClaimedStart">Visit start minus the before buffer: where the reservations start (UTC).</param>
/// <param name="ClaimedEnd">Visit end plus the after buffer: where the reservations end (UTC).</param>
/// <param name="ReplacedByAssignmentId">The assignment that replaced this one, when Replaced.</param>
public sealed record AssignmentResponse(
    Guid AssignmentId,
    Guid VisitId,
    Guid TechnicianId,
    Guid? VehicleId,
    Guid[] EquipmentIds,
    int TravelBufferBeforeMinutes,
    int TravelBufferAfterMinutes,
    string Status,
    DateTimeOffset ClaimedStart,
    DateTimeOffset ClaimedEnd,
    Guid? ReplacedByAssignmentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>GET /api/visits/{visitId}/assignments/history: every assignment of the visit, oldest first.</summary>
public sealed record AssignmentHistoryResponse(Guid VisitId, AssignmentResponse[] Assignments);
