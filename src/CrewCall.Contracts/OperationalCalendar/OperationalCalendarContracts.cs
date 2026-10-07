namespace CrewCall.Contracts.OperationalCalendar;

/// <summary>
/// GET /api/operational-calendar?start=...&amp;end=...&amp;timeZoneId=...&amp;perspective=...&amp;perspectiveId=...
/// The visits overlapping the half-open range [RangeStartUtc, RangeEndUtc), ordered by start, end, id.
/// </summary>
/// <param name="RangeLocalStart">RangeStartUtc in TimeZoneId (with that moment's offset).</param>
/// <param name="Perspective">All, Technician, Team, Vehicle or Site.</param>
public sealed record OperationalCalendarResponse(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    DateTimeOffset RangeLocalStart,
    DateTimeOffset RangeLocalEnd,
    string TimeZoneId,
    string Perspective,
    Guid? PerspectiveId,
    int TotalCount,
    OperationalCalendarItemResponse[] Items);

/// <summary>
/// One visit with its work order, customer, site and current (active) assignment. Assignment, technician, team, vehicle,
/// equipment and buffer fields are null/empty for an unassigned visit. TeamId/TeamName are the technician's current team.
/// </summary>
/// <param name="VisitLocalStart">VisitStartUtc in the requested time zone (with that moment's offset).</param>
/// <param name="VisitStatus">Planned, InProgress, Completed or Cancelled: completed and cancelled visits are included.</param>
/// <param name="AssignmentStatus">Always Active when present (the calendar shows the current assignment only).</param>
/// <param name="FieldWorkStatus">
/// What actually happened: NotStarted (nothing recorded), Traveling, Working, Paused, Completed or Cancelled. The visit's
/// planned Start/End are unaffected.
/// </param>
/// <param name="ActualTravelMinutes">Work start − travel start, when both were recorded.</param>
/// <param name="ActualWorkMinutes">Net work minutes (gross minus pauses), only once Completed.</param>
/// <param name="PauseMinutes">Closed pauses; null without field work.</param>
public sealed record OperationalCalendarItemResponse(
    Guid VisitId,
    DateTimeOffset VisitStartUtc,
    DateTimeOffset VisitEndUtc,
    DateTimeOffset VisitLocalStart,
    DateTimeOffset VisitLocalEnd,
    string VisitStatus,
    Guid WorkOrderId,
    string WorkOrderTitle,
    string WorkOrderPriority,
    string WorkOrderStatus,
    Guid CustomerId,
    string CustomerName,
    Guid SiteId,
    string SiteName,
    string SiteCity,
    Guid? AssignmentId,
    string? AssignmentStatus,
    Guid? TechnicianId,
    string? TechnicianName,
    Guid? TeamId,
    string? TeamName,
    Guid? VehicleId,
    string? VehicleName,
    string? VehicleRegistrationNumber,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes,
    OperationalCalendarEquipmentResponse[] Equipment,
    string FieldWorkStatus,
    DateTimeOffset? TravelStartedAtUtc,
    DateTimeOffset? WorkStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    decimal? ActualTravelMinutes,
    decimal? ActualWorkMinutes,
    decimal? PauseMinutes);

public sealed record OperationalCalendarEquipmentResponse(Guid EquipmentId, string Name, string AssetCode);
