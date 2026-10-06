using CrewCall.Scheduling.Assignments;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CrewCall.Persistence.ReadModels.OperationalCalendar;

/// <summary>
/// The operational calendar read model (ADR-0010): composes visits, work orders, customers, sites, current assignments,
/// technicians, their current teams, vehicles and equipment into one projection. Read-only (no tracking, no writes) and
/// rule-free: it only joins what the modules have stored.
/// </summary>
/// <remarks>
/// Query strategy, constant in the number of visits (no N+1):
/// 1. at most one existence query for the perspective's technician, team, vehicle or site;
/// 2. one query for the visits overlapping the range, joined 1:1 with work order, customer and site, and left-joined
///    with the active assignment (at most one per visit), its technician, that technician's team and the vehicle;
/// 3. one query for the equipment of the returned assignments (1:N, kept separate so the main query cannot multiply rows).
/// </remarks>
public sealed class OperationalCalendarService(CrewCallDbContext db)
{
    /// <summary>Longest range one query may cover: enough for a month view.</summary>
    public static readonly TimeSpan MaxRangeLength = TimeSpan.FromDays(31);

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public async Task<OperationalCalendarOutcome> GetAsync(OperationalCalendarQuery query, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        DateTimeOffset? start = query.Start is { } requestedStart ? Normalize(requestedStart) : null;
        DateTimeOffset? end = query.End is { } requestedEnd ? Normalize(requestedEnd) : null;
        if (start is null)
        {
            errors["start"] = ["Required."];
        }

        if (end is null)
        {
            errors["end"] = ["Required."];
        }

        if (start is not null && end is not null)
        {
            if (start >= end)
            {
                errors["end"] = ["Must be after start."];
            }
            else if (end - start > MaxRangeLength)
            {
                errors["end"] = [$"The range can be at most {MaxRangeLength.TotalDays:0} days long."];
            }
        }

        var zone = string.IsNullOrWhiteSpace(query.TimeZoneId) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(query.TimeZoneId.Trim());
        if (zone is null)
        {
            errors["timeZoneId"] = [string.IsNullOrWhiteSpace(query.TimeZoneId)
                ? "Required."
                : "Must be an IANA time zone id, e.g. Europe/Warsaw."];
        }

        var perspective = OperationalCalendarPerspective.All;
        if (!string.IsNullOrWhiteSpace(query.Perspective)
            && !(query.Perspective.All(char.IsAsciiLetter) && Enum.TryParse(query.Perspective, ignoreCase: true, out perspective)))
        {
            errors["perspective"] = [$"Must be one of: {string.Join(", ", Enum.GetNames<OperationalCalendarPerspective>())}."];
        }
        else if (perspective == OperationalCalendarPerspective.All && query.PerspectiveId is not null)
        {
            errors["perspectiveId"] = ["Must be omitted for the All perspective."];
        }
        else if (perspective != OperationalCalendarPerspective.All && (query.PerspectiveId ?? Guid.Empty) == Guid.Empty)
        {
            errors["perspectiveId"] = [$"Required for the {perspective} perspective."];
        }

        if (errors.Count > 0)
        {
            return new OperationalCalendarOutcome.Invalid(errors);
        }

        var perspectiveId = query.PerspectiveId;
        if (perspectiveId is { } id && !await PerspectiveExistsAsync(perspective, id, cancellationToken))
        {
            return new OperationalCalendarOutcome.PerspectiveNotFound(perspective, id);
        }

        var rows = await LoadVisitsAsync(start!.Value, end!.Value, perspective, perspectiveId, cancellationToken);
        var equipment = await LoadEquipmentAsync(rows, cancellationToken);

        var items = rows
            .Select(row => new OperationalCalendarItem(
                row.VisitId,
                row.VisitStart,
                row.VisitEnd,
                Local(row.VisitStart, zone!),
                Local(row.VisitEnd, zone!),
                row.VisitStatus,
                row.WorkOrderId,
                row.WorkOrderTitle,
                row.WorkOrderPriority,
                row.WorkOrderStatus,
                row.CustomerId,
                row.CustomerName,
                row.SiteId,
                row.SiteName,
                row.SiteCity,
                row.AssignmentId,
                row.AssignmentStatus,
                row.TechnicianId,
                row.TechnicianName,
                row.TeamId,
                row.TeamName,
                row.VehicleId,
                row.VehicleName,
                row.VehicleRegistrationNumber,
                row.TravelBufferBeforeMinutes,
                row.TravelBufferAfterMinutes,
                row.AssignmentId is { } assignmentId && equipment.TryGetValue(assignmentId, out var assets) ? assets : []))
            .ToList();

        return new OperationalCalendarOutcome.Listed(new OperationalCalendar(
            start.Value, end.Value, Local(start.Value, zone!), Local(end.Value, zone!), zone!.Id, perspective, perspectiveId, items));
    }

    private sealed record VisitRow(
        Guid VisitId,
        DateTimeOffset VisitStart,
        DateTimeOffset VisitEnd,
        WorkOrders.Visits.VisitStatus VisitStatus,
        Guid WorkOrderId,
        string WorkOrderTitle,
        WorkOrders.WorkOrderPriority WorkOrderPriority,
        WorkOrders.WorkOrderStatus WorkOrderStatus,
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
        int? TravelBufferAfterMinutes);

    private Task<List<VisitRow>> LoadVisitsAsync(
        DateTimeOffset start, DateTimeOffset end, OperationalCalendarPerspective perspective, Guid? perspectiveId,
        CancellationToken cancellationToken)
    {
        // Half-open overlap: the visit starts before the range ends and ends after it starts; touching is excluded.
        var joined =
            from visit in db.Visits.AsNoTracking()
            where visit.Start < end && visit.End > start
            join workOrder in db.WorkOrders.AsNoTracking() on visit.WorkOrderId equals workOrder.Id
            join customer in db.Customers.AsNoTracking() on workOrder.CustomerId equals customer.Id
            join site in db.Sites.AsNoTracking() on workOrder.SiteId equals site.Id
            join activeAssignment in db.Assignments.AsNoTracking().Where(a => a.Status == AssignmentStatus.Active)
                on visit.Id equals activeAssignment.VisitId into activeAssignments
            from assignment in activeAssignments.DefaultIfEmpty()
            join assignedTechnician in db.Technicians.AsNoTracking()
                on (Guid?)assignment!.TechnicianId equals (Guid?)assignedTechnician.Id into assignedTechnicians
            from technician in assignedTechnicians.DefaultIfEmpty()
            join currentTeam in db.Teams.AsNoTracking() on technician!.TeamId equals (Guid?)currentTeam.Id into currentTeams
            from team in currentTeams.DefaultIfEmpty()
            join assignedVehicle in db.Vehicles.AsNoTracking() on assignment!.VehicleId equals (Guid?)assignedVehicle.Id into assignedVehicles
            from vehicle in assignedVehicles.DefaultIfEmpty()
            select new { visit, workOrder, customer, site, assignment, technician, team, vehicle };

        // Technician, Team and Vehicle need an active assignment; All and Site also return unassigned visits.
        joined = perspective switch
        {
            OperationalCalendarPerspective.Technician => joined.Where(row => row.assignment!.TechnicianId == perspectiveId),
            OperationalCalendarPerspective.Team => joined.Where(row => row.technician!.TeamId == perspectiveId),
            OperationalCalendarPerspective.Vehicle => joined.Where(row => row.assignment!.VehicleId == perspectiveId),
            OperationalCalendarPerspective.Site => joined.Where(row => row.workOrder.SiteId == perspectiveId),
            _ => joined
        };

        return joined
            .OrderBy(row => row.visit.Start)
            .ThenBy(row => row.visit.End)
            .ThenBy(row => row.visit.Id)
            .Select(row => new VisitRow(
                row.visit.Id,
                row.visit.Start,
                row.visit.End,
                row.visit.Status,
                row.workOrder.Id,
                row.workOrder.Title,
                row.workOrder.Priority,
                row.workOrder.Status,
                row.customer.Id,
                row.customer.Name,
                row.site.Id,
                row.site.Name,
                row.site.City,
                row.assignment == null ? null : row.assignment.Id,
                row.assignment == null ? null : row.assignment.Status,
                row.technician == null ? null : row.technician.Id,
                row.technician == null ? null : row.technician.DisplayName,
                row.team == null ? null : row.team.Id,
                row.team == null ? null : row.team.Name,
                row.vehicle == null ? null : row.vehicle.Id,
                row.vehicle == null ? null : row.vehicle.DisplayName,
                row.vehicle == null ? null : row.vehicle.RegistrationNumber,
                row.assignment == null ? null : row.assignment.TravelBufferBeforeMinutes,
                row.assignment == null ? null : row.assignment.TravelBufferAfterMinutes))
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<OperationalCalendarEquipmentItem>>> LoadEquipmentAsync(
        IReadOnlyCollection<VisitRow> rows, CancellationToken cancellationToken)
    {
        var assignmentIds = rows.Where(row => row.AssignmentId is not null).Select(row => row.AssignmentId!.Value).ToList();
        if (assignmentIds.Count == 0)
        {
            return [];
        }

        var assets = await (
                from assignmentEquipment in db.AssignmentEquipment.AsNoTracking()
                where assignmentIds.Contains(assignmentEquipment.AssignmentId)
                join item in db.Equipment.AsNoTracking() on assignmentEquipment.EquipmentId equals item.Id
                orderby item.Name, item.AssetCode, item.Id
                select new { assignmentEquipment.AssignmentId, item.Id, item.Name, item.AssetCode })
            .ToListAsync(cancellationToken);

        return assets
            .GroupBy(asset => asset.AssignmentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OperationalCalendarEquipmentItem>)group
                    .Select(asset => new OperationalCalendarEquipmentItem(asset.Id, asset.Name, asset.AssetCode))
                    .ToList());
    }

    private Task<bool> PerspectiveExistsAsync(OperationalCalendarPerspective perspective, Guid id, CancellationToken cancellationToken) =>
        perspective switch
        {
            OperationalCalendarPerspective.Technician => db.Technicians.AnyAsync(technician => technician.Id == id, cancellationToken),
            OperationalCalendarPerspective.Team => db.Teams.AnyAsync(team => team.Id == id, cancellationToken),
            OperationalCalendarPerspective.Vehicle => db.Vehicles.AnyAsync(vehicle => vehicle.Id == id, cancellationToken),
            OperationalCalendarPerspective.Site => db.Sites.AnyAsync(site => site.Id == id, cancellationToken),
            _ => Task.FromResult(true)
        };

    /// <summary>The instant as wall-clock time in the zone, with the offset in effect at that instant (DST-aware).</summary>
    private static DateTimeOffset Local(DateTimeOffset instant, DateTimeZone zone) =>
        Instant.FromDateTimeOffset(instant).InZone(zone).ToDateTimeOffset();

    // Visits are stored in UTC with microsecond precision; compare the range in the same form.
    private static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), TimeSpan.Zero);
    }
}
