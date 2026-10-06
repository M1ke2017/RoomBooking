using CrewCall.Scheduling.Assignments;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Visits;

namespace CrewCall.Persistence.ReadModels.OperationalCalendar;

/// <summary>Whose calendar: everything, or the visits of one technician, team, vehicle or site.</summary>
public enum OperationalCalendarPerspective
{
    All,
    Technician,
    Team,
    Vehicle,
    Site
}

/// <summary>
/// The visits overlapping the half-open range [Start, End), seen from one perspective, with local times in TimeZoneId.
/// The same query serves a day, a week or a month (at most <see cref="OperationalCalendarService.MaxRangeLength"/>).
/// </summary>
/// <param name="Perspective">All (default), Technician, Team, Vehicle or Site (case-insensitive).</param>
/// <param name="PerspectiveId">Required for every perspective except All, where it must be omitted.</param>
/// <param name="TimeZoneId">IANA id the local times are projected into, e.g. "Europe/Warsaw". Required.</param>
public sealed record OperationalCalendarQuery(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? Perspective,
    Guid? PerspectiveId,
    string? TimeZoneId);

public sealed record OperationalCalendar(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    DateTimeOffset RangeLocalStart,
    DateTimeOffset RangeLocalEnd,
    string TimeZoneId,
    OperationalCalendarPerspective Perspective,
    Guid? PerspectiveId,
    IReadOnlyList<OperationalCalendarItem> Items);

/// <summary>
/// One visit as the calendar shows it: the visit, its work order, customer and site, and the visit's current (active)
/// assignment with its technician, the technician's current team, vehicle and equipment. Assignment fields are null for
/// an unassigned visit.
/// </summary>
public sealed record OperationalCalendarItem(
    Guid VisitId,
    DateTimeOffset VisitStartUtc,
    DateTimeOffset VisitEndUtc,
    DateTimeOffset VisitLocalStart,
    DateTimeOffset VisitLocalEnd,
    VisitStatus VisitStatus,
    Guid WorkOrderId,
    string WorkOrderTitle,
    WorkOrderPriority WorkOrderPriority,
    WorkOrderStatus WorkOrderStatus,
    Guid CustomerId,
    string CustomerName,
    Guid SiteId,
    string SiteName,
    string SiteCity,
    Guid? AssignmentId,
    AssignmentStatus? AssignmentStatus,
    Guid? TechnicianId,
    string? TechnicianName,
    Guid? TeamId,
    string? TeamName,
    Guid? VehicleId,
    string? VehicleName,
    string? VehicleRegistrationNumber,
    int? TravelBufferBeforeMinutes,
    int? TravelBufferAfterMinutes,
    IReadOnlyList<OperationalCalendarEquipmentItem> Equipment);

public sealed record OperationalCalendarEquipmentItem(Guid EquipmentId, string Name, string AssetCode);

public abstract record OperationalCalendarOutcome
{
    private OperationalCalendarOutcome()
    {
    }

    public sealed record Listed(OperationalCalendar Calendar) : OperationalCalendarOutcome;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : OperationalCalendarOutcome;

    /// <summary>The technician, team, vehicle or site named by the perspective does not exist.</summary>
    public sealed record PerspectiveNotFound(OperationalCalendarPerspective Perspective, Guid PerspectiveId) : OperationalCalendarOutcome;
}
