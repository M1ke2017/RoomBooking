using System.Text.Json;
using System.Text.Json.Serialization;
using CrewCall.Api.Endpoints;
using CrewCall.Contracts.OperationalCalendar;
using CrewCall.Contracts.Operations;
using CrewCall.Persistence;
using CrewCall.Persistence.ReadModels.OperationalCalendar;
using CrewCall.WorkOrders.Incidents;
using CrewCall.WorkOrders.Operations;
using CrewCall.Workforce.Availability;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace CrewCall.Api.Operations;

/// <summary>
/// The manager's operations board (Sprint 16): today's plan, the urgent incidents, the team and the alerts in one read.
/// It composes what the modules already provide — the operational calendar (ADR-0010), incidents and their latest
/// analysis (ADR-0012), Workforce availability (ADR-0007) — and decides nothing itself: it is read-only and stores nothing.
/// Lives in the composition root because it reads across modules.
/// </summary>
public sealed class OperationsBoardService(
    CrewCallDbContext db,
    OperationalCalendarService calendar,
    WorkforceAvailabilityService availability,
    TimeProvider clock)
{
    public const string DefaultTimeZoneId = "Europe/Warsaw";

    /// <summary>A visit not started this long after its planned start is late.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(15);

    private static readonly string[] FieldWork = ["Traveling", "Working", "Paused"];

    private static readonly IncidentStatus[] Open = [IncidentStatus.New, IncidentStatus.Analyzing, IncidentStatus.ReadyForDispatch];

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The board for the local <paramref name="date"/> (today when null) in the time zone; errors for invalid input.</summary>
    public async Task<(OperationsBoardResponse? Board, IReadOnlyDictionary<string, string[]>? Errors)> GetAsync(
        string? date, string? timeZoneId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var zoneId = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId);
        if (zone is null)
        {
            errors["timeZoneId"] = ["Must be an IANA time zone id, e.g. Europe/Warsaw."];
        }

        var now = clock.GetUtcNow();
        DateOnly day = default;
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", out day))
        {
            errors["date"] = ["Must be a date, yyyy-MM-dd."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        if (string.IsNullOrWhiteSpace(date))
        {
            day = DateOnly.FromDateTime(Instant.FromDateTimeOffset(now).InZone(zone!).Date.ToDateTimeUnspecified());
        }

        var start = zone!.AtStartOfDay(LocalDate.FromDateOnly(day)).ToDateTimeOffset().ToUniversalTime();
        var end = zone.AtStartOfDay(LocalDate.FromDateOnly(day).PlusDays(1)).ToDateTimeOffset().ToUniversalTime();

        var outcome = await calendar.GetAsync(new OperationalCalendarQuery(start, end, null, null, zone.Id), cancellationToken);
        var today = ((OperationalCalendarOutcome.Listed)outcome).Calendar.Items;

        var incidents = await IncidentsAsync(start, end, cancellationToken);
        var team = await TeamAsync(today, now, cancellationToken);

        return (new OperationsBoardResponse(day, zone.Id, now, today, incidents, team, Alerts(today, incidents, now)), null);
    }

    private async Task<OperationsBoardIncidentResponse[]> IncidentsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var rows = await (
                from incident in db.Incidents.AsNoTracking().Include(i => i.RequiredSkills)
                where Open.Contains(incident.Status) || (incident.RequestedStart < end && incident.RequestedEnd > start)
                join customer in db.Customers.AsNoTracking() on incident.CustomerId equals customer.Id
                join site in db.Sites.AsNoTracking() on incident.SiteId equals site.Id
                select new { incident, CustomerName = customer.Name, SiteName = site.Name, site.City })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(row => row.incident.Id).ToList();
        var analyses = (await db.OperationalEvents.AsNoTracking()
                .Where(e => e.AggregateType == IncidentEvents.IncidentAggregate && e.EventType == IncidentEvents.IncidentAnalyzed && ids.Contains(e.AggregateId))
                .OrderBy(e => e.Sequence)
                .Select(e => new { e.AggregateId, e.PayloadJson })
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.AggregateId)
            .ToDictionary(group => group.Key, group => JsonSerializer.Deserialize<IncidentAnalyzedPayload>(group.Last().PayloadJson, PayloadJson));

        return rows
            .OrderBy(row => Open.Contains(row.incident.Status) ? 0 : 1)
            .ThenByDescending(row => row.incident.Priority)
            .ThenBy(row => row.incident.RequestedStart)
            .ThenBy(row => row.incident.Id)
            .Select(row => new OperationsBoardIncidentResponse(
                row.incident.ToResponse(),
                row.CustomerName,
                row.SiteName,
                row.City,
                analyses.GetValueOrDefault(row.incident.Id) is { } analysed
                    ? new OperationsBoardAnalysisResponse(analysed.GeneratedAtUtc, analysed.DirectAssignmentCount, analysed.RequiresRescheduleCount, analysed.UnavailableCount)
                    : null))
            .ToArray();
    }

    /// <summary>
    /// Each active technician now: on field work (Traveling, Working, Paused) when today's plan says so; otherwise
    /// Unavailable when Workforce says so (absence, holiday, outside working hours); otherwise Available.
    /// </summary>
    private async Task<OperationsBoardTechnicianResponse[]> TeamAsync(
        OperationalCalendarItemResponse[] today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var technicians = await db.Technicians.AsNoTracking()
            .Where(technician => technician.IsActive)
            .OrderBy(technician => technician.DisplayName)
            .ThenBy(technician => technician.Id)
            .Select(technician => new { technician.Id, technician.DisplayName, technician.PhoneNumber })
            .ToListAsync(cancellationToken);

        var team = new List<OperationsBoardTechnicianResponse>(technicians.Count);
        foreach (var technician in technicians)
        {
            var visits = today.Where(item => item.TechnicianId == technician.Id).ToList();
            var current = visits.FirstOrDefault(item => FieldWork.Contains(item.FieldWorkStatus));
            var next = visits
                .Where(item => item.FieldWorkStatus == "NotStarted" && item.VisitStatus == "Planned" && item.VisitEndUtc > now)
                .MinBy(item => item.VisitStartUtc);

            var (state, detail) = current is not null
                ? (current.FieldWorkStatus, current.WorkOrderTitle)
                : await AvailabilityAsync(technician.Id, now, cancellationToken);

            team.Add(new OperationsBoardTechnicianResponse(
                technician.Id, technician.DisplayName, technician.PhoneNumber, state, detail,
                current?.VisitId, current?.SiteName, current?.SiteCity, next?.VisitStartUtc, visits.Count));
        }

        return [.. team];
    }

    private async Task<(string State, string? Detail)> AvailabilityAsync(Guid technicianId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outcome = await availability.CheckAsync(new CheckAvailability(technicianId, now, now.AddMinutes(1)), cancellationToken);
        return outcome is CheckAvailabilityOutcome.Evaluated { Availability.Decision: { IsAvailable: false } decision }
            ? ("Unavailable", decision.Status switch
            {
                AvailabilityStatus.UnavailableAbsence => "Absence",
                AvailabilityStatus.UnavailableHoliday => "Holiday",
                AvailabilityStatus.UnavailableOutsideWorkingHours => "OutsideWorkingHours",
                _ => "Inactive"
            })
            : ("Available", null);
    }

    /// <summary>What needs attention, from the board's own data; most severe first, then by time.</summary>
    public static OperationsBoardAlertResponse[] Alerts(
        OperationalCalendarItemResponse[] today, OperationsBoardIncidentResponse[] incidents, DateTimeOffset now)
    {
        var alerts = new List<OperationsBoardAlertResponse>();
        foreach (var open in incidents.Where(entry => entry.Incident.WorkOrderId is null && entry.Incident.Status is "New" or "Analyzing" or "ReadyForDispatch"))
        {
            var subject = $"{open.Incident.Title} — {open.SiteName}, {open.SiteCity}";
            var needsReschedule = open.Analysis is { DirectAssignmentCount: 0, RequiresRescheduleCount: > 0 };
            alerts.Add(new OperationsBoardAlertResponse(
                needsReschedule ? "IncidentNeedsReschedule" : "IncidentAwaitingDecision",
                "Critical", "incident", open.Incident.Id, subject, open.Incident.RequestedStart, null));
        }

        foreach (var visit in today.Where(item => item.VisitStatus is "Planned" or "InProgress"))
        {
            var subject = $"{visit.WorkOrderTitle} — {visit.SiteName}, {visit.SiteCity}";
            if (visit.AssignmentId is null && visit.VisitStatus == "Planned")
            {
                alerts.Add(new OperationsBoardAlertResponse("VisitUnassigned", "Warning", "visit", visit.VisitId, subject, visit.VisitStartUtc, null));
            }
            else if (visit.FieldWorkStatus == "NotStarted" && visit.VisitStatus == "Planned" && now - visit.VisitStartUtc > LateAfter)
            {
                alerts.Add(new OperationsBoardAlertResponse(
                    "VisitLate", "Warning", "visit", visit.VisitId, subject, visit.VisitStartUtc, (int)(now - visit.VisitStartUtc).TotalMinutes));
            }

            if (visit.LastReschedule is { } moved && DateOnly.FromDateTime(moved.OccurredAtUtc.UtcDateTime) == DateOnly.FromDateTime(now.UtcDateTime))
            {
                alerts.Add(new OperationsBoardAlertResponse(
                    "VisitRescheduled", "Info", "visit", visit.VisitId, subject, moved.NewStartUtc, moved.DelayMinutes));
            }
        }

        return [.. alerts.OrderBy(alert => alert.Severity switch { "Critical" => 0, "Warning" => 1, _ => 2 }).ThenBy(alert => alert.At)];
    }
}
